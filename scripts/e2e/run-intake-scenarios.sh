#!/usr/bin/env bash
# Intake scenario runner: builds one disposable stack (PostgreSQL container, migrated database,
# deterministic commercial acceptance fixture, real API, Vite) and runs
# Frontend/e2e/scenarios-intake.spec.ts N times against it, so a test that passes once and
# fails once is visible as its own class of finding ("non-deterministic").
#
# It is deliberately a sibling of run-enterprise-commercial-journey.sh rather than a mode of it:
# that runner stops the API after its suite even under E2E_KEEP_STACK=1, and its secrets are
# process-local by design. This one keeps everything running when asked and never prints a
# secret; the credentials the spec needs are written to $RUN_DIR/intake/credentials.env (0600)
# for the operator's own follow-up calls.
#
#   E2E_SKIP_BUILD=1 E2E_KEEP_STACK=1 E2E_PG_PORT=55441 E2E_BACKEND_PORT=5201 \
#   E2E_FRONTEND_PORT=5181 E2E_PG_CONTAINER=nexora-e2e-intake \
#   ./scripts/e2e/run-intake-scenarios.sh
#
# Environment contract (all optional):
#   E2E_INTAKE_RUNS          how many times to run the spec (default 3; 0 = bring the stack up only)
#   E2E_MAIL_CONTAINER       loopback GreenMail (IMAP+SMTP) container name (default nexora-intake-scn-mail)
#   E2E_SMTP_PORT/E2E_IMAP_PORT  host ports for it (default 33025 / 33143). The fixture's seeded
#                            mailbox row is repointed at it so POST /api/Email/fetch reads real mail.
#   E2E_TEST_GREP            focus the spec on a title pattern
#   E2E_RUN_DIR              retained logs/storage (default: .intake-scenarios-run)
#   E2E_PG_CONTAINER/PORT, E2E_BACKEND_PORT, E2E_FRONTEND_PORT, E2E_KEEP_STACK, E2E_SKIP_BUILD
#   Cors__AllowedOrigins__0  REQUIRED when the Vite port is not 5173/4173/3000 (the Development
#                            CORS allow-list is fixed); this script derives it from the port.
set -Eeuo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BACKEND_PROJECT="$REPO_ROOT/Backend/ERP_RFQ_Automation/ERP_RFQ_Automation.csproj"
FIXTURE_PROJECT="$REPO_ROOT/Backend/ERP_RFQ_Automation.AcceptanceFixture/ERP_RFQ_Automation.AcceptanceFixture.csproj"
FRONTEND_DIR="$REPO_ROOT/Frontend"
RUN_DIR="${E2E_RUN_DIR:-$REPO_ROOT/.intake-scenarios-run}"
PG_CONTAINER="${E2E_PG_CONTAINER:-nexora-intake-scn-pg}"
PG_PORT="${E2E_PG_PORT:-55441}"
BACKEND_PORT="${E2E_BACKEND_PORT:-5201}"
FRONTEND_PORT="${E2E_FRONTEND_PORT:-5181}"
BACKEND_URL="http://127.0.0.1:${BACKEND_PORT}"
FRONTEND_URL="http://127.0.0.1:${FRONTEND_PORT}"
RUNS="${E2E_INTAKE_RUNS:-3}"
MAIL_CONTAINER="${E2E_MAIL_CONTAINER:-nexora-intake-scn-mail}"
SMTP_PORT="${E2E_SMTP_PORT:-33025}"
IMAP_PORT="${E2E_IMAP_PORT:-33143}"
STARTED_MAIL=0
DATABASE="nexora_intake_scenarios"
SUITE_DIR="$RUN_DIR/intake"
STORAGE_ROOT="$SUITE_DIR/storage"

STARTED_PG=0
BACKEND_PID=""
FRONTEND_PID=""
CURRENT_BACKEND_LOG=""

log() { printf '\n\033[1m[intake-scenarios]\033[0m %s\n' "$*"; }
die() { printf '\n\033[31m[intake-scenarios] %s\033[0m\n' "$*" >&2; exit 1; }

listener_for() { lsof -ti "tcp:$1" -sTCP:LISTEN 2>/dev/null || true; }

stop_port_process() {
  local port="$1" wrapper_pid="${2:-}" listener
  [[ -n "$wrapper_pid" ]] && kill "$wrapper_pid" 2>/dev/null || true
  listener="$(listener_for "$port")"
  if [[ -n "$listener" ]]; then
    kill $listener 2>/dev/null || true
    sleep 1
    listener="$(listener_for "$port")"
    [[ -n "$listener" ]] && kill -9 $listener 2>/dev/null || true
  fi
}

stop_backend() { stop_port_process "$BACKEND_PORT" "$BACKEND_PID"; BACKEND_PID=""; }

cleanup() {
  local code=$?
  if [[ "${E2E_KEEP_STACK:-0}" == "1" ]]; then
    log "E2E_KEEP_STACK=1 — API, Vite, $PG_CONTAINER and $MAIL_CONTAINER stay up. Tear down with: docker rm -f $PG_CONTAINER $MAIL_CONTAINER; kill \$(lsof -ti tcp:$BACKEND_PORT -sTCP:LISTEN) \$(lsof -ti tcp:$FRONTEND_PORT -sTCP:LISTEN)"
    return "$code"
  fi
  log "Tearing down the processes and containers this runner created."
  stop_port_process "$FRONTEND_PORT" "$FRONTEND_PID"
  stop_backend
  [[ "$STARTED_PG" == "1" ]] && docker rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
  [[ "$STARTED_MAIL" == "1" ]] && docker rm -f "$MAIL_CONTAINER" >/dev/null 2>&1 || true
  return "$code"
}
trap cleanup EXIT

wait_for_http() {
  local url="$1" label="$2" attempts="${3:-180}" code
  for ((i = 1; i <= attempts; i++)); do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$url" 2>/dev/null || true)"
    if [[ -n "$code" && "$code" != "000" ]]; then log "$label is up (HTTP $code at $url)."; return 0; fi
    sleep 2
  done
  [[ -n "$CURRENT_BACKEND_LOG" && -f "$CURRENT_BACKEND_LOG" ]] && tail -120 "$CURRENT_BACKEND_LOG" >&2
  die "$label did not answer at $url."
}

log "Validating tooling and exclusive ports."
for tool in docker dotnet node npx curl python3 lsof; do
  command -v "$tool" >/dev/null 2>&1 || die "Required tool not found: $tool"
done
docker info >/dev/null 2>&1 || die "Docker engine is unavailable."
[[ -d "$FRONTEND_DIR/node_modules" ]] || die "Run 'npm ci' in Frontend first."
mkdir -p "$SUITE_DIR" "$STORAGE_ROOT"

for pair in "backend:$BACKEND_PORT" "postgres:$PG_PORT" "smtp:$SMTP_PORT" "imap:$IMAP_PORT"; do
  holder="$(listener_for "${pair##*:}")"
  [[ -z "$holder" ]] || die "Port ${pair##*:} (${pair%%:*}) is already in use by PID(s) $holder."
done
for existing in "$PG_CONTAINER" "$MAIL_CONTAINER"; do
  if docker ps -a --format '{{.Names}}' | grep -qx "$existing"; then
    die "Container $existing already exists. Refusing to reuse credentials or data this runner did not create."
  fi
done

PG_PASSWORD="$(python3 -c 'import secrets; print(secrets.token_urlsafe(24))')"
APP_SECRET="$(python3 -c 'import secrets; print(secrets.token_urlsafe(48))')"
PLATFORM_SECRET="$(python3 -c 'import secrets; print(secrets.token_urlsafe(48))')"
ACCEPTANCE_PASSWORD="$(python3 -c 'import secrets; print("E2e!" + secrets.token_urlsafe(18))')"
PROTECTION_KEY="$(python3 -c 'import base64,secrets; print(base64.b64encode(secrets.token_bytes(32)).decode())')"
INTEGRATION_SECRET="$(python3 -c 'import secrets; print(secrets.token_urlsafe(40))')"

log "Starting one disposable PostgreSQL container on port $PG_PORT."
docker run -d --name "$PG_CONTAINER" -e POSTGRES_PASSWORD="$PG_PASSWORD" \
  -p "${PG_PORT}:5432" postgres:16-alpine >/dev/null
STARTED_PG=1
pg_ready=0
for ((i = 1; i <= 60; i++)); do
  if docker exec "$PG_CONTAINER" psql -U postgres -d postgres -c 'select 1' >/dev/null 2>&1; then pg_ready=1; break; fi
  sleep 2
done
[[ "$pg_ready" == "1" ]] || die "PostgreSQL did not become ready on port $PG_PORT."

# A loopback mail sink, so the e-mail door (triage → capture → assembly → Lead) is exercised for
# real: the API polls it over IMAP and the spec posts messages to it over SMTP. Auth is disabled
# on the sink because it is a throwaway on 127.0.0.1; the API side is still the product's own
# mailbox row and MailEndpointPolicy (Development loopback allowance, see the backend env below).
log "Starting one disposable GreenMail (SMTP $SMTP_PORT / IMAP $IMAP_PORT) container."
docker run -d --name "$MAIL_CONTAINER" -p "127.0.0.1:${SMTP_PORT}:3025" -p "127.0.0.1:${IMAP_PORT}:3143" \
  -e GREENMAIL_OPTS="-Dgreenmail.setup.test.smtp -Dgreenmail.setup.test.imap -Dgreenmail.hostname=0.0.0.0 -Dgreenmail.auth.disabled" \
  greenmail/standalone:2.1.0 >/dev/null
STARTED_MAIL=1
mail_ready=0
for ((i = 1; i <= 45; i++)); do
  if python3 -c 'import socket,sys; s=socket.create_connection(("127.0.0.1", int(sys.argv[1])), timeout=2); s.recv(64); s.close()' "$IMAP_PORT" >/dev/null 2>&1; then
    mail_ready=1; break
  fi
  sleep 2
done
[[ "$mail_ready" == "1" ]] || die "GreenMail did not answer on IMAP port $IMAP_PORT."

if [[ "${E2E_SKIP_BUILD:-0}" != "1" ]]; then
  log "Building backend, acceptance fixture and production frontend."
  dotnet build "$FIXTURE_PROJECT" --nologo -v minimal >"$RUN_DIR/dotnet-build.log" 2>&1 || { tail -80 "$RUN_DIR/dotnet-build.log" >&2; die "Backend/fixture build failed."; }
  (cd "$FRONTEND_DIR" && npm run build) >"$RUN_DIR/frontend-build.log" 2>&1 || { tail -80 "$RUN_DIR/frontend-build.log" >&2; die "Frontend build failed."; }
else
  log "E2E_SKIP_BUILD=1 — using existing build outputs."
fi

start_frontend() {
  local code
  code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "$FRONTEND_URL" 2>/dev/null || true)"
  [[ -n "$code" && "$code" != "000" ]] && return 0
  log "Starting Vite on $FRONTEND_URL."
  (cd "$FRONTEND_DIR" && VITE_API_BASE_URL="$BACKEND_URL" npx vite --port "$FRONTEND_PORT" --strictPort --host 127.0.0.1) >>"$RUN_DIR/frontend.log" 2>&1 &
  FRONTEND_PID=$!
  wait_for_http "$FRONTEND_URL" "Frontend" 90
}
start_frontend

CONNECTION="Host=127.0.0.1;Port=${PG_PORT};Database=${DATABASE};Username=postgres;Password=${PG_PASSWORD}"

# The API's own environment, written once (0600) so the API can be rebuilt and restarted against
# the SAME database mid-investigation — a fix has to be proved on the running stack, not only in
# the unit suite. Restart with:
#   set -a; . "$RUN_DIR/intake/backend.env"; set +a
#   dotnet run --project Backend/ERP_RFQ_Automation/ERP_RFQ_Automation.csproj --no-build --no-launch-profile
write_backend_env() {
  (
    umask 077
    cat >"$SUITE_DIR/backend.env" <<ENV
ConnectionStrings__DefaultConnection="$CONNECTION"
Database__ApplyMigrationsOnStartup=false
Database__ValidateRequestPathOnStartup=true
Jwt__Key="$APP_SECRET"
Jwt__PlatformKey="$PLATFORM_SECRET"
Security__SecretProtectionKey="$PROTECTION_KEY"
Storage__RootPath="$STORAGE_ROOT"
CommercialFinance__ContactVerificationSecret="$APP_SECRET"
CommercialFinance__DunningProviderWebhookSecret="$APP_SECRET"
CommercialFinance__AuditActorSecret="$APP_SECRET"
Observability__Prometheus__ScrapeKey="$APP_SECRET"
ProcurementIntegration__Tenants__80101__SourceSystem="Disposable ERP"
ProcurementIntegration__Tenants__80101__SharedSecret="$INTEGRATION_SECRET"
Notifications__OutboundGuard__Mode=DraftOnly
Mail__AllowLoopbackForLocalDevelopment=true
Cors__AllowedOrigins__0="${Cors__AllowedOrigins__0:-$FRONTEND_URL}"
ASPNETCORE_ENVIRONMENT=Development
ASPNETCORE_URLS="$BACKEND_URL"
ENV
  )
  chmod 600 "$SUITE_DIR/backend.env"
}

start_backend() {
  local phase="$1" apply_migrations="$2"
  CURRENT_BACKEND_LOG="$SUITE_DIR/${phase}-backend.log"
  (
    cd "$(dirname "$BACKEND_PROJECT")"
    ConnectionStrings__DefaultConnection="$CONNECTION" \
    Database__ApplyMigrationsOnStartup="$apply_migrations" \
    Database__ValidateRequestPathOnStartup=true \
    Jwt__Key="$APP_SECRET" \
    Jwt__PlatformKey="$PLATFORM_SECRET" \
    Security__SecretProtectionKey="$PROTECTION_KEY" \
    Storage__RootPath="$STORAGE_ROOT" \
    CommercialFinance__ContactVerificationSecret="$APP_SECRET" \
    CommercialFinance__DunningProviderWebhookSecret="$APP_SECRET" \
    CommercialFinance__AuditActorSecret="$APP_SECRET" \
    Observability__Prometheus__ScrapeKey="$APP_SECRET" \
    ProcurementIntegration__Tenants__80101__SourceSystem="Disposable ERP" \
    ProcurementIntegration__Tenants__80101__SharedSecret="$INTEGRATION_SECRET" \
    Notifications__OutboundGuard__Mode=DraftOnly \
    Mail__AllowLoopbackForLocalDevelopment=true \
    Cors__AllowedOrigins__0="${Cors__AllowedOrigins__0:-$FRONTEND_URL}" \
    ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS="$BACKEND_URL" \
    dotnet run --project "$BACKEND_PROJECT" --no-build --no-launch-profile
  ) >"$CURRENT_BACKEND_LOG" 2>&1 &
  BACKEND_PID=$!
  wait_for_http "$BACKEND_URL/health" "Backend" 210
}

write_backend_env
log "Creating $DATABASE and applying the full migration chain."
docker exec "$PG_CONTAINER" psql -U postgres -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE \"${DATABASE}\"" >"$SUITE_DIR/database-create.log" 2>&1
start_backend migrate true
stop_backend

log "Loading the deterministic commercial acceptance fixture."
FIXTURE_LOG="$SUITE_DIR/fixture.log"
NEXORA_ACCEPTANCE_CONNECTION="$CONNECTION" \
NEXORA_ACCEPTANCE_PASSWORD="$ACCEPTANCE_PASSWORD" \
NEXORA_ACCEPTANCE_SECRET_PROTECTION_KEY="$PROTECTION_KEY" \
NEXORA_ACCEPTANCE_STORAGE_ROOT="$STORAGE_ROOT" \
dotnet run --project "$FIXTURE_PROJECT" --no-build --no-launch-profile >"$FIXTURE_LOG" 2>&1 || { tail -120 "$FIXTURE_LOG" >&2; die "Acceptance fixture failed."; }

# The fixture seeds one active IMAP mailbox at localhost:993 that nothing answers. Point it at the
# loopback sink instead (host/port/TLS only — the encrypted password column is left alone, the
# sink does not check it) so the poller and POST /api/Email/fetch read real mail.
docker exec "$PG_CONTAINER" psql -U postgres -d "$DATABASE" -v ON_ERROR_STOP=1 -c \
  "UPDATE \"Email_Configurations\" SET \"Host\"='127.0.0.1', \"Port\"=${IMAP_PORT}, \"UseSSL\"=false, \"PollingInterval\"=1, \"Username\"='intake@release01c1.local' WHERE \"Protocol\"='IMAP'" \
  >"$SUITE_DIR/mailbox-repoint.log" 2>&1 || { cat "$SUITE_DIR/mailbox-repoint.log" >&2; die "Could not repoint the fixture mailbox at GreenMail."; }
export E2E_SMTP_PORT="$SMTP_PORT" E2E_IMAP_PORT="$IMAP_PORT" E2E_MAILBOX_ADDRESS="intake@release01c1.local"

# Fixture output is parsed as data, never sourced as shell: several values contain spaces.
while IFS= read -r line; do
  [[ "$line" == *=* ]] || continue
  key="${line%%=*}"; value="${line#*=}"
  [[ "$key" =~ ^E2E_[A-Z0-9_]+$ ]] || continue
  printf -v "$key" '%s' "$value"; export "$key"
done < "$FIXTURE_LOG"
export E2E_FIXTURE_MODE=false E2E_BASE_URL="$FRONTEND_URL" E2E_API_URL="$BACKEND_URL" E2E_SKIP_WEB_SERVER=true
for role in MANAGER FINANCE EDITOR DENIED; do
  lower="$(printf '%s' "$role" | tr '[:upper:]' '[:lower:]')"
  export "E2E_${role}_EMAIL=${lower}@release01c1.local" "E2E_${role}_PASSWORD=$ACCEPTANCE_PASSWORD" "E2E_${role}_BUSINESS_UNIT_ID=80101"
done
export E2E_OTHER_EMAIL=other@release01c1.local E2E_OTHER_PASSWORD="$ACCEPTANCE_PASSWORD" E2E_OTHER_BUSINESS_UNIT_ID=80102

(
  umask 077
  {
    env | grep -E '^E2E_[A-Z0-9_]+=' | sed -E 's/^([^=]+)=(.*)$/export \1="\2"/'
  } >"$SUITE_DIR/credentials.env"
)
chmod 600 "$SUITE_DIR/credentials.env"

start_backend run false

log "Running the intake scenario spec $RUNS time(s)."
overall=0
for ((run = 1; run <= RUNS; run++)); do
  args=(test --config playwright.intake-scenarios.config.ts --workers=1 --retries=0)
  [[ -n "${E2E_TEST_GREP:-}" ]] && args+=(--grep "$E2E_TEST_GREP")
  set +e
  (cd "$FRONTEND_DIR" && E2E_INTAKE_RUN="$run" E2E_INTAKE_JSON="$SUITE_DIR/run-$run.json" npx playwright "${args[@]}") 2>&1 | tee "$SUITE_DIR/run-$run.log"
  status=${PIPESTATUS[0]}
  set -e
  log "Run $run finished with exit $status."
  [[ "$status" == "0" ]] || overall=1
done

log "Scenario × run matrix (P pass / S soft product finding only / F fail / — not run):"
python3 - "$SUITE_DIR" "$RUNS" <<'PY'
import json, os, re, sys
suite_dir, runs = sys.argv[1], int(sys.argv[2])
results, order = {}, []
soft = re.compile(r'\bF\d+:')   # expect.soft messages are labelled with the finding they record
for run in range(1, runs + 1):
    path = os.path.join(suite_dir, f'run-{run}.json')
    if not os.path.exists(path):
        continue
    report = json.load(open(path))
    def walk(suite):
        for spec in suite.get('specs', []):
            title = spec['title']
            if title not in results:
                results[title] = {}
                order.append(title)
            errors = [e.get('message', '') for t in spec.get('tests', []) for r in t.get('results', []) for e in r.get('errors', [])]
            if spec.get('ok'):
                outcome = 'P'
            elif errors and all(soft.search(m) for m in errors):
                outcome = 'S'
            else:
                outcome = 'F'
            results[title][run] = outcome
        for child in suite.get('suites', []):
            walk(child)
    for suite in report.get('suites', []):
        walk(suite)
width = max((len(t) for t in order), default=10)
print(f"{'scenario'.ljust(width)}  " + '  '.join(f'run{r}' for r in range(1, runs + 1)) + '  verdict')
for title in order:
    cells = [results[title].get(r, '—') for r in range(1, runs + 1)]
    seen = {c for c in cells if c != '—'}
    verdict = 'pass' if seen == {'P'} else 'finding' if seen == {'S'} else 'FAIL' if seen == {'F'} else 'FLAKY' if seen else 'not run'
    print(f"{title.ljust(width)}  " + '  '.join(c.ljust(4) for c in cells) + f'  {verdict}')
PY

[[ "$overall" == "0" ]] && log "ALL RUNS GREEN." || log "One or more runs had failures. Logs: $SUITE_DIR"
exit "$overall"
