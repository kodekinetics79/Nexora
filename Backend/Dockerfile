# Nexora backend — production container image (ASP.NET Core 8 + workers)
# Build context = the Backend/ directory.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ERP_RFQ_Automation/ERP_RFQ_Automation.csproj ERP_RFQ_Automation/
RUN dotnet restore ERP_RFQ_Automation/ERP_RFQ_Automation.csproj
COPY ERP_RFQ_Automation/ ERP_RFQ_Automation/

# Workstation GC for the build, because this step is running out of memory.
#
# Three consecutive deploys were OOM-killed here: the build log stops mid-publish with no error
# at all and the deploy is marked failed two minutes later, which is what a kernel OOM looks like
# from inside BuildKit. Measured locally in a memory-capped container, `dotnet publish` on this
# project fails at 2 GB, 3 GB AND 4 GB with "Out of memory" from Roslyn.
#
# The reason is Migrations/. There are 113 of them, and every one carries a .Designer.cs holding a
# complete copy of the database model as a single ~22,000-line BuildTargetModel method. That is
# 1.83 million of the project's 1.99 million lines — 92% of everything the compiler reads. Each
# new migration embeds another, larger copy, so this gets monotonically worse and it crossed the
# builder's limit today rather than being caused by anything today.
#
# Server GC sizes its heaps per CPU, and a build container reports the HOST's core count while
# being allowed a fraction of its memory. Workstation GC collects sooner and keeps peak RSS lower;
# a build is throughput insensitive, so this costs nothing.
#
# THIS IS A STOPGAP, not the fix. The fix is to stop compiling 113 copies of the schema, and it is
# not a five-minute change: deleting the Designer files was tried and REVERTED, because EF's SQL
# generator reads each migration's TargetModel at runtime — without it, AlterColumn is emitted as
# a bare ALTER COLUMN TYPE and PostgreSQL refuses it on any column named in an RLS policy
# (0A000). Squashing is the real answer and has to be hand-authored, because the security controls
# in this schema — RLS policies, column grants, ENABLE ALWAYS triggers — live in raw SQL inside
# individual migrations and would be silently lost by a regenerated baseline.
ENV DOTNET_gcServer=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    NUGET_XMLDOC_MODE=skip
RUN dotnet publish ERP_RFQ_Automation/ERP_RFQ_Automation.csproj -c Release -o /app /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
# Native libs for Tesseract/Leptonica OCR (the Tesseract nuget also ships linux-x64 natives).
RUN apt-get update \
    && apt-get install -y --no-install-recommends libleptonica-dev libtesseract-dev tesseract-ocr \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
# Containers listen on 8080 by convention; the host maps it to 443/80.
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
# Health probe target: GET /health
ENTRYPOINT ["dotnet", "ERP_RFQ_Automation.dll"]
