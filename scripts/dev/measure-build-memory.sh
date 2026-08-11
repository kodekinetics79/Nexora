#!/usr/bin/env bash
# =============================================================================
# measure-build-memory.sh — peak memory of a cold `dotnet publish`
#
# WHY THIS EXISTS
# ---------------
# Render kills this repo's Docker build with
#     "Ran out of memory (used over 8GB) while building your code."
# Every fix proposed for that has been argued from intuition. This script turns
# the number into a measurement so that "X made it better" is falsifiable.
#
# TEST/TOOLING ONLY. It builds; it never publishes, pushes or deploys, and it
# never modifies tracked files (the Designer-exclusion scenario is injected via
# an MSBuild hook file written to a temp dir, not by editing the .csproj).
#
# TWO BACKENDS, DELIBERATELY
# --------------------------
#   --mode docker  (DEFAULT, authoritative)
#       Runs the publish inside mcr.microsoft.com/dotnet/sdk:8.0 — the same
#       image Backend/Dockerfile uses — and reads cgroup v2 `memory.peak` from
#       the container's own cgroup afterwards.
#       * Kernel-accounted high-water mark. ZERO sampling error: the kernel
#         updates it on every page charge, so a 5 ms spike is captured exactly
#         like a 5 minute plateau.
#       * Shared pages are charged ONCE to the cgroup, so this does not
#         double-count the runtime/libs mapped into dotnet + csc + VBCSCompiler.
#       * It is the same accounting a container runtime uses to decide to
#         OOM-kill you, i.e. it is the number Render is actually comparing
#         against its limit.
#
#   --mode host    (cross-check / fast iteration)
#       Runs the publish natively and samples RSS of every dotnet / csc /
#       VBCSCompiler / MSBuild process. Reports THREE numbers because they mean
#       different things and mixing them up is how the folklore started:
#         peak_concurrent    max over sample instants of SUM(rss) at that instant
#                            <- the honest "how much was live at once"
#         sum_of_maxima      SUM over pids of that pid's own max rss
#                            <- what a naive harness reports; an OVER-count,
#                               because the peaks need not be simultaneous
#         kernel_max_single  max resident set of the largest single process,
#                            from getrusage() via /usr/bin/time -l — exact,
#                            unsampled, and therefore a hard LOWER bound on the
#                            true concurrent peak
#       On macOS, summed RSS also double-counts shared/copy-on-write pages, so
#       host numbers run HIGH versus a Linux cgroup. Compare host-to-host only.
#
# ERROR BOUND
# -----------
#   docker mode : exact (kernel high-water mark, no sampling).
#   host mode   : peak_concurrent can only UNDER-report, by at most the growth
#                 a process achieves inside one sample interval. Default
#                 interval is 100 ms (see --interval); the harness also asserts
#                 peak_concurrent >= kernel_max_single and warns loudly if the
#                 sampler missed enough to violate that. Run --interval 1.0 and
#                 compare to quantify the miss on your own hardware.
#
# USAGE
#   scripts/dev/measure-build-memory.sh --scenario baseline
#   scripts/dev/measure-build-memory.sh --scenario no-designer --mode host
#   scripts/dev/measure-build-memory.sh --all                 # every scenario
#   scripts/dev/measure-build-memory.sh --list                # scenario names
#
# EXIT CODE is the build's exit code, so an OOM-killed build fails the script.
# =============================================================================
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT_REL="Backend/ERP_RFQ_Automation/ERP_RFQ_Automation.csproj"
CONTEXT_REL="Backend/ERP_RFQ_Automation"
SDK_IMAGE="mcr.microsoft.com/dotnet/sdk:8.0"

# Source tree to measure. Defaults to the live project directory, but you should
# usually point this at an immutable snapshot (--source, see --snapshot below):
# measuring the live tree means anyone editing the .csproj mid-suite silently
# changes what scenario 5 compiled versus scenario 1, and the table becomes a
# comparison of two different projects.
SRC_DIR=""
MAKE_SNAPSHOT=0
# Compile only the N NEWEST Migrations/*.Designer.cs files (empty = all of them).
# This exists to answer "how many more migrations does the fixed build buy?" by
# MEASURING a curve instead of extrapolating one: sweep N and find where the
# build stops fitting. Pair it with --scenario keep-n.
KEEP_DESIGNERS=""
SNAPSHOT_PINS_HEAD=1   # snapshot restores tracked files to git HEAD

MODE="docker"
INTERVAL="0.1"
SCENARIO=""
RUN_ALL=0
MEM_LIMIT=""          # docker mode: --memory value, e.g. 8g. empty = unlimited
OUT_DIR="${TMPDIR:-/tmp}/nexora-build-mem"
# Host mode only. Docker mode always uses SDK_IMAGE and needs no pin.
# Empty string disables pinning and uses whatever `dotnet` resolves.
PIN_SDK="$(dotnet --list-sdks 2>/dev/null | awk '/^8\./{v=$1} END{print v}')"

# -----------------------------------------------------------------------------
# Scenarios. Each is: extra MSBuild args | extra env (KEY=VAL,KEY=VAL) | blurb
# Keep these ORTHOGONAL — the entire point is to attribute a delta to one knob.
# -----------------------------------------------------------------------------
scenario_args() {
  case "$1" in
    baseline)          echo "" ;;
    no-designer)       echo "__NODESIGNER__" ;;
    m1)                echo "-m:1" ;;
    no-shared-compile) echo "/p:UseSharedCompilation=false" ;;
    no-analyzers)      echo "/p:RunAnalyzers=false /p:EnableNETAnalyzers=false /p:EnforceCodeStyleInBuild=false" ;;
    nullable-off)      echo "/p:Nullable=disable" ;;
    gc-heap-limit)     echo "" ;;
    combo-stopgap)     echo "" ;;
    combo-fixed)       echo "__NODESIGNER__" ;;
    *) return 1 ;;
  esac
}
scenario_env() {
  case "$1" in
    # DOTNET_GCHeapHardLimit is a HEX byte count. 0x100000000 = 4 GiB, applied to
    # every dotnet process in the build (MSBuild nodes AND VBCSCompiler), which
    # caps each heap rather than each machine. Roslyn throws OutOfMemory rather
    # than growing past it, so this can convert a slow build into a failed one —
    # that is a legitimate result and the harness reports it as such.
    gc-heap-limit)  echo "DOTNET_GCHeapHardLimit=100000000" ;;
    # What Backend/Dockerfile ships today.
    combo-stopgap)  echo "DOTNET_gcServer=0" ;;
    combo-fixed)    echo "DOTNET_gcServer=0" ;;
    *)              echo "" ;;
  esac
}
scenario_blurb() {
  case "$1" in
    baseline)          echo "as Backend/Dockerfile builds today, minus DOTNET_gcServer" ;;
    no-designer)       if [[ -n "$KEEP_DESIGNERS" ]]; then
                         echo "keep newest $KEEP_DESIGNERS Designer files = ${KEPT_LINES:-?} Designer lines"
                       else echo "<Compile Remove=Migrations/*.Designer.cs> via MSBuild hook"; fi ;;
    m1)                echo "single MSBuild node" ;;
    no-shared-compile) echo "no persistent VBCSCompiler server" ;;
    no-analyzers)      echo "Roslyn analyzers + code style off" ;;
    nullable-off)      echo "nullable flow analysis off across 2.5M lines" ;;
    gc-heap-limit)     echo "DOTNET_GCHeapHardLimit=4GiB per dotnet process" ;;
    combo-stopgap)     echo "TODAY'S SHIPPING CONFIG (workstation GC, all sources)" ;;
    combo-fixed)       echo "workstation GC + Designer files excluded" ;;
  esac
}
ALL_SCENARIOS=(baseline no-designer m1 no-shared-compile no-analyzers nullable-off gc-heap-limit combo-stopgap combo-fixed)

usage() { sed -n '2,60p' "${BASH_SOURCE[0]}"; exit "${1:-0}"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --mode)      MODE="$2"; shift 2 ;;
    --scenario)  SCENARIO="$2"; shift 2 ;;
    --interval)  INTERVAL="$2"; shift 2 ;;
    --memory)    MEM_LIMIT="$2"; shift 2 ;;
    --out)       OUT_DIR="$2"; shift 2 ;;
    --pin-sdk)   PIN_SDK="$2"; shift 2 ;;
    --no-pin)    PIN_SDK=""; shift ;;
    --source)    SRC_DIR="$2"; shift 2 ;;
    --snapshot)  MAKE_SNAPSHOT=1; shift ;;
    --keep-designers) KEEP_DESIGNERS="$2"; shift 2 ;;
    --snapshot-worktree) MAKE_SNAPSHOT=1; SNAPSHOT_PINS_HEAD=0; shift ;;
    --all)       RUN_ALL=1; shift ;;
    --list)      for s in "${ALL_SCENARIOS[@]}"; do printf '  %-18s %s\n' "$s" "$(scenario_blurb "$s")"; done; exit 0 ;;
    -h|--help)   usage 0 ;;
    *) echo "unknown arg: $1" >&2; usage 1 ;;
  esac
done

mkdir -p "$OUT_DIR"

# ---------------------------------------------------------------------------
# SOURCE SNAPSHOT. --snapshot copies the project into $OUT_DIR/src and (by
# default) resets every TRACKED file to git HEAD, so the suite measures one
# fixed revision even while somebody else is editing the working tree. Untracked
# files are carried over verbatim so a new-but-uncommitted Migrations folder
# still gets compiled. --snapshot-worktree keeps working-tree edits instead.
# ---------------------------------------------------------------------------
if (( MAKE_SNAPSHOT )); then
  SNAP="$OUT_DIR/src"
  rm -rf "$SNAP"; mkdir -p "$SNAP"
  if (( SNAPSHOT_PINS_HEAD )); then
    # `git archive` of the committed tree ONLY. Deliberately not "working tree
    # with tracked files reverted": that hybrid mixes an old .csproj with new
    # untracked files and produces builds that exist in no revision (it fails
    # here with CS0111, two ModelSnapshot classes, because HEAD's .csproj does
    # not know to exclude the old Migrations folder). Render builds a commit,
    # so the baseline must be a commit.
    ( cd "$REPO_ROOT" && git archive "HEAD:$CONTEXT_REL" ) | tar -C "$SNAP" -xf -
    echo "snapshot: $SNAP (git archive HEAD:$CONTEXT_REL @ $(cd "$REPO_ROOT" && git rev-parse --short HEAD))"
  else
    # Working tree as-is, including uncommitted and untracked work. Use this to
    # measure a squash-in-progress before it is committed.
    tar -C "$REPO_ROOT/$CONTEXT_REL" -cf - --exclude=./bin --exclude=./obj . | tar -C "$SNAP" -xf -
    echo "snapshot: $SNAP (working tree as-is, uncommitted work included)"
  fi
  SRC_DIR="$SNAP"
fi
[[ -n "$SRC_DIR" ]] || SRC_DIR="$REPO_ROOT/$CONTEXT_REL"
[[ -f "$SRC_DIR/ERP_RFQ_Automation.csproj" ]] || { echo "no csproj under $SRC_DIR" >&2; exit 2; }
# Fingerprint what we are actually compiling, so a results table can be tied to
# a specific project definition after the fact.
CSPROJ_HASH=$(shasum -a 256 "$SRC_DIR/ERP_RFQ_Automation.csproj" | cut -c1-12)
COMPILE_LINES=$(find "$SRC_DIR" -name '*.cs' -not -path '*/bin/*' -not -path '*/obj/*' -print0 | xargs -0 cat 2>/dev/null | wc -l | tr -d ' ')
echo "measuring: $SRC_DIR  csproj=$CSPROJ_HASH  cs_lines=$COMPILE_LINES"

RESULTS_TSV="$OUT_DIR/results.tsv"
[[ -f "$RESULTS_TSV" ]] || printf 'scenario\tmode\tpeak_mb\tmethod\tseconds\texit\tnote\n' > "$RESULTS_TSV"

# The MSBuild hook that drops the Designer files. Written to a temp dir and
# passed as CustomAfterMicrosoftCommonTargets, which is imported at the very end
# of Microsoft.Common.targets — after the SDK's default Compile glob has already
# populated @(Compile), so the Remove actually removes something. Verified with
# `dotnet msbuild -getItem:Compile` (268 Designer entries -> 0).
HOOK_HOST="$OUT_DIR/exclude-designers.targets"
if [[ -n "$KEEP_DESIGNERS" ]]; then
  # Remove them all, then Include back the N newest (filenames are timestamped,
  # so `sort | tail -N` is newest-N). Keeping the NEWEST is the right end to
  # keep: Designer files grow monotonically (4,378 lines for the first, 27,579
  # for the 134th), so the newest are the expensive ones and this models future
  # growth rather than flattering it.
  { echo '<Project>'; echo '  <ItemGroup>';
    echo '    <Compile Remove="Migrations/*.Designer.cs" />'
    for f in $(cd "$SRC_DIR/Migrations" 2>/dev/null && ls *.Designer.cs 2>/dev/null | sort | tail -n "$KEEP_DESIGNERS"); do
      echo "    <Compile Include=\"Migrations/$f\" />"
    done
    echo '  </ItemGroup>'; echo '</Project>'; } > "$HOOK_HOST"
  KEPT_LINES=$( (cd "$SRC_DIR/Migrations" 2>/dev/null && ls *.Designer.cs 2>/dev/null | sort | tail -n "$KEEP_DESIGNERS" | tr '\n' '\0' | xargs -0 cat 2>/dev/null) | wc -l | tr -d ' ')
  echo "keep-designers: newest $KEEP_DESIGNERS of $(ls "$SRC_DIR/Migrations"/*.Designer.cs 2>/dev/null | wc -l | tr -d ' ') files = ${KEPT_LINES:-0} Designer lines"
else
  cat > "$HOOK_HOST" <<'TARGETS'
<Project>
  <ItemGroup>
    <Compile Remove="Migrations/*.Designer.cs" />
  </ItemGroup>
</Project>
TARGETS
fi

human_mb() { awk -v b="$1" 'BEGIN{printf "%.0f", b/1048576}'; }

# -----------------------------------------------------------------------------
# DOCKER MODE — authoritative
# -----------------------------------------------------------------------------
# Deliberately `docker run` (not `docker build`): BuildKit gives no access to the
# build container's cgroup, so a docker build can only be observed from outside.
# `docker run` puts us inside the cgroup, where memory.peak is readable. The
# steps mirror Backend/Dockerfile's build stage exactly (restore, then publish
# -c Release -o /app /p:UseAppHost=false).
#
# NOTE: this sandbox blocks `docker start` on pre-existing containers; everything
# here is `docker run --rm`, which works.
run_docker() {
  local scen="$1" args env_pairs
  args="$(scenario_args "$scen")" || { echo "unknown scenario: $scen" >&2; return 2; }
  env_pairs="$(scenario_env "$scen")"

  local hook_args=""
  if [[ "$args" == *__NODESIGNER__* ]]; then
    args="${args//__NODESIGNER__/}"
    hook_args="-p:CustomAfterMicrosoftCommonTargets=/hook/exclude-designers.targets"
  fi

  local -a docker_env=()
  if [[ -n "$env_pairs" ]]; then
    IFS=',' read -r -a _kvs <<< "$env_pairs"
    for kv in "${_kvs[@]}"; do docker_env+=(-e "$kv"); done
  fi
  local -a mem_args=()
  [[ -n "$MEM_LIMIT" ]] && mem_args=(--memory "$MEM_LIMIT" --memory-swap "$MEM_LIMIT")

  # /src is a COPY, not a bind mount: a bind-mounted obj/ would let a previous
  # run's intermediate output leak in and make "cold" a lie, and would write the
  # container's linux-x64 obj/ back over the host's.
  local script='set -e
mkdir -p /src
tar -C /repo -cf - --exclude=./bin --exclude=./obj . | tar -C /src -xf -
cd /src
rm -rf obj bin
export DOTNET_CLI_TELEMETRY_OPTOUT=1 NUGET_XMLDOC_MODE=skip
dotnet restore ERP_RFQ_Automation.csproj >/dev/null
START=$(date +%s)
set +e
dotnet publish ERP_RFQ_Automation.csproj -c Release -o /app /p:UseAppHost=false '"$args $hook_args"' > /publish.log 2>&1
RC=$?
set -e
END=$(date +%s)
PEAK=$(cat /sys/fs/cgroup/memory.peak 2>/dev/null || echo 0)
echo "__RESULT__ peak_bytes=$PEAK rc=$RC seconds=$((END-START))"
# Keep the WHOLE compiler log outside the container. A truncated tail loses the
# exception header, which is the one line that says whether the build died of
# OutOfMemoryException, a cgroup kill, or an ordinary compile error.
cp /publish.log /out/'"$scen"'.publish.log 2>/dev/null || true
grep -m1 -B4 -iE "out of memory|OutOfMemoryException|Killed" /publish.log 2>/dev/null || true
tail -15 /publish.log'

  echo "==> [docker] $scen — $(scenario_blurb "$scen")"
  local out
  out=$(docker run --rm \
        "${mem_args[@]+"${mem_args[@]}"}" \
        "${docker_env[@]+"${docker_env[@]}"}" \
        -v "$SRC_DIR:/repo:ro" \
        -v "$OUT_DIR:/hook:ro" \
        -v "$OUT_DIR:/out" \
        -v "nexora-nuget-cache:/root/.nuget/packages" \
        -w /src "$SDK_IMAGE" bash -c "$script" 2>&1)
  local docker_rc=$?

  local line peak rc secs
  line=$(printf '%s\n' "$out" | grep '__RESULT__' | tail -1)
  if [[ -z "$line" ]]; then
    # No result line => the container itself died (OOM kill by the daemon).
    printf '%s\n' "$out" | tail -20
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$scen" docker "" "cgroup2 memory.peak" "" "$docker_rc" \
      "CONTAINER KILLED (likely OOM at --memory=$MEM_LIMIT)" >> "$RESULTS_TSV"
    echo "    RESULT: container died, rc=$docker_rc  (this reproduces the Render failure)"
    return "$docker_rc"
  fi
  peak=$(sed -n 's/.*peak_bytes=\([0-9]*\).*/\1/p' <<<"$line")
  rc=$(sed -n 's/.*rc=\([0-9]*\).*/\1/p' <<<"$line")
  secs=$(sed -n 's/.*seconds=\([0-9]*\).*/\1/p' <<<"$line")
  [[ "$rc" != "0" ]] && printf '%s\n' "$out" | tail -20

  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$scen" docker "$(human_mb "$peak")" "cgroup2 memory.peak (exact)" \
    "$secs" "$rc" "$(scenario_blurb "$scen")" >> "$RESULTS_TSV"
  echo "    RESULT: $(human_mb "$peak") MB peak (cgroup2 memory.peak, exact) in ${secs}s, build rc=$rc"
  return "$rc"
}

# -----------------------------------------------------------------------------
# HOST MODE — sampled cross-check
# -----------------------------------------------------------------------------
run_host() {
  local scen="$1" args env_pairs
  args="$(scenario_args "$scen")" || { echo "unknown scenario: $scen" >&2; return 2; }
  env_pairs="$(scenario_env "$scen")"

  local hook_args=""
  if [[ "$args" == *__NODESIGNER__* ]]; then
    args="${args//__NODESIGNER__/}"
    hook_args="-p:CustomAfterMicrosoftCommonTargets=$HOOK_HOST"
  fi

  local proj_dir="$SRC_DIR"
  local samples="$OUT_DIR/$scen.samples"
  local timelog="$OUT_DIR/$scen.time"
  : > "$samples"

  # Pin the SDK to the 8.0 band. Backend/Dockerfile builds on sdk:8.0; a dev box
  # with .NET 10 installed would otherwise measure a DIFFERENT Roslyn than the
  # one Render runs, and Roslyn's memory behaviour is exactly what is under test.
  # Written to the project dir (dotnet resolves global.json from the cwd) and
  # removed on any exit path, including Ctrl-C.
  local wrote_global_json=0
  if [[ -n "$PIN_SDK" ]]; then
    if [[ -e "$proj_dir/global.json" ]]; then
      echo "    NOTE: $proj_dir/global.json already exists; leaving it alone (no SDK pin applied)"
    else
      printf '{"sdk":{"version":"%s","rollForward":"latestFeature"}}\n' "$PIN_SDK" > "$proj_dir/global.json"
      wrote_global_json=1
      # Belt and braces: the trap covers Ctrl-C, the explicit rm at the end of
      # this function covers the multi-scenario case. Relying on the EXIT trap
      # alone leaves the file in place for scenario 2..N of a --all run.
      # shellcheck disable=SC2064
      trap "rm -f '$proj_dir/global.json'" EXIT INT TERM
    fi
  fi

  # Kill any VBCSCompiler left running by a previous build. Otherwise it is
  # counted as part of this build's footprint while holding the LAST build's
  # heap, which inflates every number after the first — a real trap in the
  # existing folklore.
  dotnet build-server shutdown >/dev/null 2>&1
  pkill -f VBCSCompiler >/dev/null 2>&1
  rm -rf "$proj_dir/obj" "$proj_dir/bin"

  echo "==> [host] $scen — $(scenario_blurb "$scen")"

  # -------------------------------------------------------------------------
  # AMBIENT-PROCESS QUARANTINE. This is the single biggest source of bogus
  # numbers on a developer machine and it is why the folklore figures are what
  # they are.
  #
  # A naive `ps -A | grep -i dotnet` matches VS Code's C# extension:
  # Microsoft.CodeAnalysis.LanguageServer, csdevkit's ServiceHost, Razor, and
  # every `dotnet` the IDE keeps warm. On the machine this was written on that
  # is ~4.9 GB resident AT IDLE, before a build starts. Add it to a real build
  # and you "measure" 12 GB for a compile that used 5.
  #
  # So: snapshot every PID on the box first, then count a process only if it is
  # (a) a descendant of THIS build, or (b) a dotnet/csc/VBCSCompiler/MSBuild
  # process that did not exist before we started. (b) exists because
  # VBCSCompiler can outlive the MSBuild node that spawned it and get
  # reparented to init, which breaks a pure descendant walk.
  # -------------------------------------------------------------------------
  local pre_pids="$OUT_DIR/$scen.pre-pids"
  ps -Ao pid= 2>/dev/null | tr -d ' ' > "$pre_pids"
  local ambient_kb
  ambient_kb=$(ps -Ao rss=,comm= 2>/dev/null | awk '{n=$2; sub(/.*\//,"",n); if(n=="dotnet"||n=="csc"||n=="VBCSCompiler"||n=="MSBuild"||n ~ /^Microsoft\.CodeAnalysis/) s+=$1} END{print s+0}')
  if (( ambient_kb > 512000 )); then
    echo "    NOTE: $(human_mb $((ambient_kb*1024))) MB of ambient dotnet/Roslyn processes are running"
    echo "          (VS Code C# extension etc). They are EXCLUDED by PID quarantine."
  fi

  local start=$(date +%s)
  # Build runs in the BACKGROUND so the sampler can walk its process tree.
  # -nodeReuse:false makes MSBuild nodes die with the build, which is what a
  # one-shot Docker build gets anyway, and stops a previous run's nodes being
  # silently reused (and therefore quarantined) by the next.
  (
    cd "$proj_dir" || exit 1
    if [[ -n "$env_pairs" ]]; then
      IFS=',' read -r -a _kvs <<< "$env_pairs"
      for kv in "${_kvs[@]}"; do export "${kv?}"; done
    fi
    export DOTNET_CLI_TELEMETRY_OPTOUT=1 NUGET_XMLDOC_MODE=skip
    dotnet restore ERP_RFQ_Automation.csproj >/dev/null 2>&1
    # /usr/bin/time -l reports getrusage maxrss. NOTE: RUSAGE_CHILDREN only
    # covers descendants this process actually reaped, so with MSBuild worker
    # nodes and VBCSCompiler it tends to report only the driver. Treat it as a
    # hard LOWER bound on the true peak, never as the answer.
    /usr/bin/time -l dotnet publish ERP_RFQ_Automation.csproj -c Release \
        -o "$OUT_DIR/out-$scen" /p:UseAppHost=false -nodeReuse:false $args $hook_args
  ) >"$OUT_DIR/$scen.log" 2>"$timelog" &
  local build_pid=$!

  # Sampler: ONE `ps` fork per sample, tree walk done in awk.
  while kill -0 "$build_pid" 2>/dev/null; do
    ps -Ao pid=,ppid=,rss=,comm= 2>/dev/null \
      | awk -v root="$build_pid" -v ts="$(date +%s)" -v pref="$pre_pids" '
          BEGIN { while ((getline p < pref) > 0) pre[p+0]=1 }
          { pid=$1+0; ppid=$2+0; PP[pid]=ppid; R[pid]=$3+0; C[pid]=$4 }
          END {
            for (p in PP) { q=p; d=0
              while (q>1 && d<64) { if (q==root) { desc[p]=1; break } q=PP[q]; d++ } }
            s=0
            for (p in PP) {
              n=C[p]; sub(/.*\//,"",n)
              isnet = (n=="dotnet" || n=="csc" || n=="VBCSCompiler" || n=="MSBuild")
              if (desc[p] || (isnet && !(p in pre))) { s+=R[p]; print "P", p, R[p] }
            }
            print "S", ts, s
          }' >> "$samples" 2>/dev/null
    sleep "$INTERVAL"
  done
  wait "$build_pid"; local rc=$?
  local secs=$(( $(date +%s) - start ))

  local peak_concurrent sum_of_maxima nsamples
  peak_concurrent=$(awk '$1=="S"{if($3>m)m=$3} END{printf "%d", m*1024}' "$samples")
  sum_of_maxima=$(awk '$1=="P"{if($3>m[$2])m[$2]=$3} END{t=0; for(p in m)t+=m[p]; printf "%d", t*1024}' "$samples")
  nsamples=$(grep -c '^S' "$samples")
  # macOS /usr/bin/time -l prints "maximum resident set size" in BYTES.
  local kernel_max
  kernel_max=$(awk '/maximum resident set size/{print $1}' "$timelog" | tail -1)
  kernel_max=${kernel_max:-0}

  local note="$(scenario_blurb "$scen")"
  if (( kernel_max > peak_concurrent )); then
    note="$note; SAMPLER MISSED A SPIKE (kernel_max_single $(human_mb "$kernel_max") MB > sampled peak)"
    echo "    !! sampler under-reported: raise --interval resolution"
  fi
  # Achieved interval, not requested interval — each sample costs a ps fork, so
  # the real spacing is always worse than --interval. This IS the error bound:
  # peak_concurrent can under-report by at most one interval of heap growth.
  local achieved="n/a"
  (( nsamples > 0 )) && achieved=$(awk -v s="$secs" -v n="$nsamples" 'BEGIN{printf "%.3f", s/n}')

  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$scen" host "$(human_mb "$peak_concurrent")" \
    "sampled ${achieved}s eff, n=$nsamples" "$secs" "$rc" "$note" >> "$RESULTS_TSV"
  echo "    peak_concurrent   $(human_mb "$peak_concurrent") MB   <- report this one"
  echo "    sum_of_maxima     $(human_mb "$sum_of_maxima") MB   (over-count; what naive harnesses print)"
  echo "    kernel_max_single $(human_mb "$kernel_max") MB   (exact, driver process only; lower bound)"
  echo "    samples=$nsamples over ${secs}s (${achieved}s effective interval), build rc=$rc"
  rm -rf "$OUT_DIR/out-$scen"
  (( wrote_global_json )) && rm -f "$proj_dir/global.json"
  trap - EXIT INT TERM
  return "$rc"
}

run_one() { case "$MODE" in docker) run_docker "$1" ;; host) run_host "$1" ;; *) echo "bad --mode: $MODE" >&2; exit 2 ;; esac; }

FINAL=0
if (( RUN_ALL )); then
  for s in "${ALL_SCENARIOS[@]}"; do run_one "$s" || FINAL=$?; done
elif [[ -n "$SCENARIO" ]]; then
  run_one "$SCENARIO" || FINAL=$?
else
  usage 1
fi

echo
echo "=== $RESULTS_TSV ==="
column -t -s $'\t' "$RESULTS_TSV"
exit "$FINAL"
