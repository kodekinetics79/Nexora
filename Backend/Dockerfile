# Nexora backend — production container image (ASP.NET Core 8 + workers)
# Build context = the Backend/ directory.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ERP_RFQ_Automation/ERP_RFQ_Automation.csproj ERP_RFQ_Automation/
RUN dotnet restore ERP_RFQ_Automation/ERP_RFQ_Automation.csproj --runtime linux-x64
COPY ERP_RFQ_Automation/ ERP_RFQ_Automation/

# BUILD MEMORY. History first, because the stopgap this replaces was load-bearing for weeks and
# the reasoning still matters. Every number below is reproducible with
# scripts/dev/measure-build-memory.sh; none of it is estimated.
#
# WHAT BROKE. Three consecutive deploys were OOM-killed at this step: the build log stops
# mid-publish with no error at all and the deploy is marked failed two minutes later, which is what
# a kernel OOM looks like from inside BuildKit. Render's Starter build pipeline gives EVERY build
# 2 CPU / 8 GB RAM. That is a WORKSPACE-wide tier, not a per-service one — it is unaffected by
# nexora-fyjw's instance type, so scaling the service would never have helped. The only lever
# Render offers is the Performance pipeline tier (16 CPU / 64 GB), which needs a Pro workspace and
# bills build minutes at a higher rate.
#
# WHY. Migrations/. Every migration carries a .Designer.cs holding a complete copy of the database
# model as a single BuildTargetModel method — 4,378 lines for the first migration, 27,579 for the
# 134th, growing by ~174 lines each time. At 134 migrations that was 2,308,701 lines, 92% of
# everything the compiler read. Roslyn materialises an IOperation tree per method body, so each
# copy cost real heap; the failing stack is CSharpOperationFactory under MethodCompiler.
#
# THE STOPGAP THAT USED TO BE HERE, AND WHY IT IS GONE. This file set DOTNET_gcServer=0 so the
# compiler would collect sooner. Measured, it did not work. In a 7 GiB container the build fails
# with System.OutOfMemoryException WITH the flag and WITHOUT it — 6030 MB peak versus 5958 MB.
# All the flag bought was 513 seconds of build before the failure instead of 113. It is removed
# rather than kept "just in case": a knob that does not fix the problem still taxes every build
# and, worse, reads to the next person like an explanation.
#
# ALSO MEASURED. Do not re-propose these without numbers (host, unconstrained,
# baseline 7343 MB):
#     -m:1                           7547 MB   WORSE  (+2.8%)
#     /p:UseSharedCompilation=false  7752 MB   WORSE  (+5.6%)
#     /p:Nullable=disable            7819 MB   WORSE  (+6.5%)
#     DOTNET_GCHeapHardLimit=4GiB    4550 MB   but the BUILD FAILS
#     RunAnalyzers=false             5935 MB   helps (-19%)
#
# Analyzer execution is disabled ONLY for this packaging publish. GitHub CI restores and performs
# a Release build with analyzers before a commit can merge, so diagnostics remain a required gate;
# rerunning the same analyzers inside Render's image packager adds no coverage. Measured against
# commit 0f098bd in the Linux AMD64 sdk:8.0 image with a 7 GiB cgroup: the final RID-specific
# packaging command passed in 71 seconds at 5,434,818,560 bytes peak under emulation. The previous
# command exceeded Render's 8 GB build limit.
#
# THE ACTUAL FIX is the squashed baseline: one Designer file instead of 134. Same harness, same
# 7 GiB container: 5946 MB and FAILING before, 3589 MB and PASSING after, and 113 s -> 23 s.
#
# WHY THE DESIGNER FILES CANNOT SIMPLY BE DELETED, AND WHY THE SQUASH HAD TO BE HAND-AUTHORED.
# This was previously recorded here as received wisdom; it has now been verified end to end.
#   * [Migration] and [DbContext] exist ONLY in the .Designer.cs half — 134 of 134 Designer files
#     carry them, 0 of 135 hand-written halves do. EF's MigrationsAssembly selects migrations with
#     `t.GetCustomAttribute<DbContextAttribute>()?.ContextType == contextType`, so a migration
#     that has lost its Designer file is not broken, it is INVISIBLE. Program.cs calls
#     Database.MigrateAsync() at boot (Database__ApplyMigrationsOnStartup=true in render.yaml)
#     and would silently apply nothing.
#   * BuildTargetModel is also what supplies Migration.TargetModel, which Migrator.GenerateUpSql
#     hands to the SQL generator. Without it the model is EMPTY, so GetProductVersion() is null,
#     TryGetVersion fails, IsOldColumnSupported is false, oldType is null, and the generator's
#     `type != oldType` test is ALWAYS true — AlterColumn emits a bare ALTER COLUMN ... TYPE even
#     when nothing about the type changed. PostgreSQL rejects exactly that with SQLSTATE 0A000
#     ("cannot alter type of a column used in a policy definition") on any column named in an RLS
#     policy, and it rejects it even when the new type is IDENTICAL. 51 migrations here create
#     policies and 5 of those also AlterColumn, so this is a live path, not a theoretical one.
#   * The security controls — RLS policies, column grants, ENABLE ALWAYS triggers — live in raw
#     SQL inside individual migrations and would be silently lost by a regenerated baseline.
#     That is why the baseline is hand-authored and reviewed, not scaffolded.
#
# TO RE-MEASURE, don't guess:
#     scripts/dev/measure-build-memory.sh --snapshot --mode docker --memory 7g --scenario baseline
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    NUGET_XMLDOC_MODE=skip

# Budget guard. Fails in seconds, at the top of the build, if the migration estate grows back
# toward the size that broke it — instead of the team rediscovering it as an unexplained OOM kill
# four minutes into a deploy. Denominated in Designer LINES rather than migration COUNT because
# lines are what the compiler reads and because Designer files get bigger as the schema grows.
#
# Calibrated by bisection in a 7 GiB container, not guessed:
#     519,482 Designer lines -> 4614 MB, 34 s, passes
#   2,039,430               -> 6367 MB, 90 s, passes  <- last measured PASS
#   2,225,279               ->          72 s, System.OutOfMemoryException
#   2,308,701 (today, 134)  ->         113 s, System.OutOfMemoryException
# The live estate reached 639,787 lines on 2026-08-26. The 800,000-line budget leaves room for
# roughly five more current-size migrations while forcing the next squash well before Render can
# rediscover the limit as an unexplained OOM kill.
#
# It counts the EVALUATED @(Compile) item list, not files on disk. That distinction is the whole
# point: the squash leaves the 134 superseded migrations on disk for reversibility and removes
# them from compilation in the .csproj, so a `cat Migrations/*.Designer.cs` guard would fail a
# build that is actually fine. What costs memory is what Roslyn is handed.
RUN designers=$(dotnet msbuild ERP_RFQ_Automation/ERP_RFQ_Automation.csproj -nologo -getItem:Compile \
              | grep -o '"FullPath": "[^"]*\.Designer\.cs"' | cut -d'"' -f4 || true); \
    count=$(printf '%s\n' "${designers}" | sed '/^$/d' | wc -l); \
    lines=$(printf '%s\n' "${designers}" | tr '\n' '\0' \
              | xargs -0 --no-run-if-empty cat | wc -l); \
    budget=800000; \
    echo "migration Designer files: ${count} files, ${lines} lines (budget ${budget})"; \
    if [ "${count}" -eq 0 ]; then \
      echo "BUILD SAFETY FAILURE: no compiled EF migration Designer files."; \
      echo "The runtime would start without a discoverable schema migration."; \
      exit 1; \
    fi; \
    if [ "${lines}" -gt "${budget}" ]; then \
      echo "=================================================================="; \
      echo "BUILD MEMORY BUDGET EXCEEDED: ${lines} > ${budget} Designer lines."; \
      echo "Each migration embeds a full copy of the schema and Roslyn holds an"; \
      echo "IOperation tree per method body. At 2,308,701 lines this build died"; \
      echo "with System.OutOfMemoryException on Render's 8 GB Starter pipeline."; \
      echo "Squash the live migration estate again before adding more."; \
      echo "Measure: scripts/dev/measure-build-memory.sh --snapshot --mode docker"; \
      echo "=================================================================="; \
      exit 1; \
    fi

RUN dotnet publish ERP_RFQ_Automation/ERP_RFQ_Automation.csproj \
      -c Release -o /app --no-restore \
      --runtime linux-x64 --self-contained false \
      /p:UseAppHost=false \
      /p:RunAnalyzers=false \
      /p:EnableNETAnalyzers=false \
      /p:EnforceCodeStyleInBuild=false

# NuGet otherwise publishes native binaries for every supported OS/CPU. The RID-specific publish
# above retains Render's Linux x64 QuestPDF/pdfium/qpdf assets. Tesseract's package still copies
# Windows x86/x64 DLLs as content, so remove only those two unusable payloads; the runtime stage
# installs Linux libraries and binds the exact wrapper names below. Verified by the production
# OCR startup probe (Tesseract 5.3.0 recognition + pdfium rasterization both Operational).
RUN rm -rf /app/x86 \
    && find /app/x64 -maxdepth 1 -type f -name '*.dll' -delete

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
# Native libs for Tesseract/Leptonica OCR.
#
# The comment here used to claim "the Tesseract nuget also ships linux-x64 natives". It does
# not — Tesseract 5.2.0 contains no .so and no runtimes/ directory at all, so the managed
# wrapper depends entirely on what this image provides, under the exact names it probes for.
# wget is the single-box container health probe. Keep the probe dependency in the runtime
# image instead of relying on an undocumented base-image tool.
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget liblept5 libtesseract5 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./

# THE NAMES ARE THE WHOLE PROBLEM, and this was silently broken in production: every scanned
# PDF and every image returned no text, while /ready reported ocr-engine as Degraded and the
# startup probe logged only the outer TargetInvocationException.
#
# Reproduced in this exact base image. The inner exception the probe swallowed is:
#
#   DllNotFoundException: Failed to find library "libleptonica-1.82.0.so" for platform x64.
#
# Tesseract.dll hard-codes the names "tesseract50" and "leptonica-1.82.0" (readable with
# `strings`), so it looks for libtesseract50.so and libleptonica-1.82.0.so. Debian ships
# libtesseract.so.5 and liblept.so.5. Neither probed name exists, so the load always failed.
#
# The loader searches APP-RELATIVE paths, not the system library path — linking these into
# /usr/lib alone does nothing, which is why the obvious fix does not work. It wants an x64/
# subdirectory beside the assembly, exactly as the "for platform x64" in the error says.
#
# The third link is separate: glibc 2.34 folded libdl into libc and stopped shipping
# libdl.so, but the wrapper's loader still dlopen()s "libdl" before anything else. Without
# it the load fails one step later, complaining about libdl rather than about Tesseract.
#
# Verified in this base image: without these three it raises TargetInvocationException, with
# them it prints "OCR ENGINE LOADED OK — version 5.3.0".
RUN set -eux; L=/usr/lib/x86_64-linux-gnu; mkdir -p /app/x64; \
    ln -sf "$L/libtesseract.so.5" /app/x64/libtesseract50.so; \
    ln -sf "$L/liblept.so.5"      /app/x64/libleptonica-1.82.0.so; \
    ln -sf "$L/libdl.so.2"        "$L/libdl.so"; \
    test -e /app/x64/libtesseract50.so && test -e /app/x64/libleptonica-1.82.0.so
# Containers listen on 8080 by convention; the host maps it to 443/80.
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
# Health probe target: GET /health
ENTRYPOINT ["dotnet", "ERP_RFQ_Automation.dll"]
