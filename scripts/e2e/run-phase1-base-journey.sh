#!/usr/bin/env bash
# Phase 1 base journey: Lead -> RFQ -> Customer Quote Draft, end to end, locally.
#
# One command. Starts PostgreSQL, migrates, starts the backend (which also hosts the extraction
# worker), seeds the deterministic golden scenario, starts the frontend, resolves every id the
# browser test needs, runs Playwright, and tears down only what it started.
#
# Nothing here is a production facility: the seeders it enables refuse to run under Production
# and every credential is a throwaway value generated or supplied locally.
#
#   ./scripts/e2e/run-phase1-base-journey.sh
#   KEEP_E2E_STACK=1 ./scripts/e2e/run-phase1-base-journey.sh    # leave services running
set -Eeuo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BACKEND_DIR="$REPO_ROOT/Backend/ERP_RFQ_Automation"
FRONTEND_DIR="$REPO_ROOT/Frontend"
RUN_DIR="${E2E_RUN_DIR:-$REPO_ROOT/.e2e-run}"
PG_CONTAINER="${E2E_PG_CONTAINER:-nexora-e2e-pg}"
PG_PORT="${E2E_PG_PORT:-55432}"
BACKEND_PORT="${E2E_BACKEND_PORT:-5192}"
FRONTEND_PORT="${E2E_FRONTEND_PORT:-5173}"
BACKEND_URL="http://127.0.0.1:${BACKEND_PORT}"
FRONTEND_URL="http://127.0.0.1:${FRONTEND_PORT}"
MANIFEST="$RUN_DIR/golden-manifest.json"
ENV_FILE="$RUN_DIR/e2e.env"

STARTED_PG=0; BACKEND_PID=""; FRONTEND_PID=""

log() { printf '\n\033[1m[e2e]\033[0m %s\n' "$*"; }
die() { printf '\n\033[31m[e2e] %s\033[0m\n' "$*" >&2; exit 1; }

cleanup() {
  local code=$?
  if [[ "${KEEP_E2E_STACK:-0}" == "1" ]]; then
    log "KEEP_E2E_STACK=1 — leaving services running (backend $BACKEND_URL, frontend $FRONTEND_URL)."
    return $code
  fi
  log "Tearing down services this script started."
  # `( ... ) &` yields the SUBSHELL pid. `dotnet run` then forks the actual application, so
  # killing $! terminated the wrapper and left the real backend holding the port. The next run
  # found that stale process still answering /health while it held the PREVIOUS run's database
  # credentials — which surfaced as 28P01 on the first login and nothing before it. Kill the
  # process actually listening on the port.
  for pair in "$FRONTEND_PORT:$FRONTEND_PID" "$BACKEND_PORT:$BACKEND_PID"; do
    local port="${pair%%:*}" wrapper="${pair##*:}"
    [[ -n "$wrapper" ]] && kill "$wrapper" 2>/dev/null || true
    local listener
    listener="$(lsof -ti "tcp:${port}" -sTCP:LISTEN 2>/dev/null || true)"
    if [[ -n "$listener" ]]; then
      kill $listener 2>/dev/null || true
      sleep 1
      listener="$(lsof -ti "tcp:${port}" -sTCP:LISTEN 2>/dev/null || true)"
      [[ -n "$listener" ]] && kill -9 $listener 2>/dev/null || true
    fi
  done
  # Only remove the database if THIS run created it — never someone else's container.
  [[ "$STARTED_PG" == "1" ]] && docker rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
  return $code
}
trap cleanup EXIT

wait_for() { # url, label, attempts
  # Readiness means "the service answers", not "the service is 2xx". /health aggregates
  # component checks (evidence storage, scanner, OCR) that are legitimately degraded in a local
  # E2E stack, so `curl -fsS` — which fails on any non-2xx — reported a perfectly serving API as
  # never ready. Any HTTP status proves Kestrel is bound and routing.
  local url="$1" label="$2" attempts="${3:-90}" code
  for ((i = 1; i <= attempts; i++)); do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$url" 2>/dev/null || true)"
    if [[ -n "$code" && "$code" != "000" ]]; then
      log "$label is up (HTTP $code at $url)."
      return 0
    fi
    sleep 2
  done
  die "$label did not answer at $url. See $RUN_DIR for logs."
}

# ---------------------------------------------------------------- 1. tooling
log "Validating local tooling."
for tool in docker dotnet node npx curl python3; do
  command -v "$tool" >/dev/null 2>&1 || die "Required tool not found: $tool"
done
docker info >/dev/null 2>&1 || die "Docker is installed but not running."
mkdir -p "$RUN_DIR"

# A port already in use means someone else's process — very likely a leaked backend from an
# earlier run — would answer our health check and serve the browser test against the wrong
# database. Refuse rather than silently test the wrong process.
for pair in "backend:$BACKEND_PORT" "frontend:$FRONTEND_PORT"; do
  svc="${pair%%:*}"; port="${pair##*:}"
  holder="$(lsof -ti "tcp:${port}" -sTCP:LISTEN 2>/dev/null || true)"
  [[ -z "$holder" ]] || die "Port $port ($svc) is already in use by PID(s) $holder. Stop it first: kill $holder"
done

# Local-only throwaway credentials. Generated per run, written to an ignored file with
# restrictive permissions, never printed and never committed.
PG_PASSWORD="$(python3 -c 'import secrets;print(secrets.token_urlsafe(24))')"
APP_SECRET="$(python3 -c 'import secrets;print(secrets.token_urlsafe(48))')"
E2E_PASSWORD="${E2E_GOLDEN_PASSWORD:-$(python3 -c 'import secrets;print("E2e!"+secrets.token_urlsafe(18))')}"

# ---------------------------------------------------------------- 2. postgres
if docker ps -a --format '{{.Names}}' | grep -qx "$PG_CONTAINER"; then
  log "Reusing existing container $PG_CONTAINER."
  docker start "$PG_CONTAINER" >/dev/null 2>&1 || true
  die "A container named $PG_CONTAINER already exists with unknown credentials. Remove it first: docker rm -f $PG_CONTAINER"
else
  log "Starting PostgreSQL on port $PG_PORT."
  docker run -d --name "$PG_CONTAINER" \
    -e POSTGRES_PASSWORD="$PG_PASSWORD" -e POSTGRES_DB=nexora_e2e \
    -p "${PG_PORT}:5432" postgres:16-alpine >/dev/null
  STARTED_PG=1
fi
# Readiness is probed from the HOST over the mapped port, and with a real query.
#
# `docker exec pg_isready` is not sufficient: the official image starts a temporary server on a
# unix socket to run initdb, and pg_isready answers "ready" against THAT before the real server
# has bound 0.0.0.0. The backend then connects immediately and gets ECONNREFUSED, which the
# earlier version of this script reported as "backend did not become ready" — a misleading
# symptom for a database race.
pg_ready=0
for ((i = 1; i <= 60; i++)); do
  if python3 - "$PG_PORT" <<'PROBE' >/dev/null 2>&1
import socket, sys
with socket.create_connection(("127.0.0.1", int(sys.argv[1])), timeout=2):
    pass
PROBE
  then
    if docker exec "$PG_CONTAINER" psql -U postgres -d nexora_e2e -c 'select 1' >/dev/null 2>&1; then
      pg_ready=1; break
    fi
  fi
  sleep 2
done
[[ "$pg_ready" == "1" ]] || die "PostgreSQL never became ready on port $PG_PORT."
log "PostgreSQL ready."

CONNECTION="Host=127.0.0.1;Port=${PG_PORT};Database=nexora_e2e;Username=postgres;Password=${PG_PASSWORD}"

# ---------------------------------------------------------------- 3. backend + worker + migrations
#
# Migrations are applied BY THE APPLICATION, not by `dotnet ef database update`. The runtime
# DbContext carries TenantRlsCommandInterceptor, which issues SET ROLE nexora_pipeline_app before
# the migration that CREATES that role has run — a chicken-and-egg failure (SqlState 22023).
# Program.cs uses a separate options builder without that interceptor for migrations.
#
# The extraction worker is a hosted service inside this same process, so starting the API starts
# the worker; there is no second process to launch.
log "Building backend."
dotnet build "$BACKEND_DIR" --nologo -v quiet >"$RUN_DIR/build.log" 2>&1 || {
  tail -30 "$RUN_DIR/build.log"; die "Backend build failed."; }

log "Starting backend (migrations + golden seed) on $BACKEND_URL."
(
  cd "$BACKEND_DIR"
  ConnectionStrings__DefaultConnection="$CONNECTION" \
  Database__ApplyMigrationsOnStartup=true \
  Database__ValidateRequestPathOnStartup=true \
  Jwt__Key="$APP_SECRET" \
  CommercialFinance__ContactVerificationSecret="$APP_SECRET" \
  CommercialFinance__DunningProviderWebhookSecret="$APP_SECRET" \
  CommercialFinance__AuditActorSecret="$APP_SECRET" \
  GoldenJourneySeed__Enabled=true \
  GoldenJourneySeed__Password="$E2E_PASSWORD" \
  GoldenJourneySeed__ManifestPath="$MANIFEST" \
  Notifications__OutboundGuard__Mode=DraftOnly \
  ASPNETCORE_ENVIRONMENT=Development \
  ASPNETCORE_URLS="$BACKEND_URL" \
  dotnet run --no-build --no-launch-profile
) >"$RUN_DIR/backend.log" 2>&1 &
BACKEND_PID=$!

# Migrating ~200 tables from empty takes a while on first run.
wait_for "$BACKEND_URL/health" "Backend" 150
[[ -f "$MANIFEST" ]] || { tail -40 "$RUN_DIR/backend.log"; die "Golden seed did not produce $MANIFEST."; }
log "Golden scenario seeded."

# ---------------------------------------------------------------- 4. ids for the browser test
read -r TENANT_A TENANT_B ADMIN_EMAIL SALES_EMAIL OUTSIDER_EMAIL LEAD_ID FOREIGN_LEAD_ID <<<"$(
  python3 -c "
import json,sys
m=json.load(open('$MANIFEST'))
print(m['tenantA'], m['tenantB'], m['adminEmail'], m['salespersonEmail'],
      m['outsiderEmail'], m['leadId'], m['foreignLeadId'])"
)"

export SALES_EMAIL="$SALES_EMAIL"
export E2E_PASSWORD="$E2E_PASSWORD"

umask 077
cat >"$ENV_FILE" <<ENV
E2E_BASE_URL=$FRONTEND_URL
E2E_API_URL=$BACKEND_URL
E2E_GOLDEN_TENANT_A=$TENANT_A
E2E_GOLDEN_TENANT_B=$TENANT_B
E2E_GOLDEN_ADMIN_EMAIL=$ADMIN_EMAIL
E2E_GOLDEN_SALES_EMAIL=$SALES_EMAIL
E2E_GOLDEN_OUTSIDER_EMAIL=$OUTSIDER_EMAIL
E2E_GOLDEN_PASSWORD=$E2E_PASSWORD
E2E_GOLDEN_LEAD_ID=$LEAD_ID
E2E_GOLDEN_FOREIGN_LEAD_ID=$FOREIGN_LEAD_ID
ENV
chmod 600 "$ENV_FILE"
log "Resolved ids: tenantA=$TENANT_A lead=$LEAD_ID foreignLead=$FOREIGN_LEAD_ID (credentials in $ENV_FILE, mode 600)."
# ---------------------------------------------------------------- 4b. login preflight
#
# A real POST /api/Auth/login that must return 200 AND a JWT. This is the check that catches a
# backend which starts, answers /health, and still cannot authenticate a single request — the
# exact failure that previously reached Playwright and was misreported as a browser-test failure.
# Implemented in its own helper so the credential never passes through a shell command line.
log "Login preflight."
python3 "$REPO_ROOT/scripts/e2e/login-preflight.py" \
  "$BACKEND_URL" "$SALES_EMAIL" "$E2E_PASSWORD" "$PG_PORT" \
  || die "Login preflight failed. Playwright not started."

# ---------------------------------------------------------------- 5. frontend
log "Starting frontend on $FRONTEND_URL."
(
  cd "$FRONTEND_DIR"
  VITE_API_BASE_URL="$BACKEND_URL" npx vite --port "$FRONTEND_PORT" --strictPort --host 127.0.0.1
) >"$RUN_DIR/frontend.log" 2>&1 &
FRONTEND_PID=$!
wait_for "$FRONTEND_URL" "Frontend" 90

# ---------------------------------------------------------------- 6. browser journey
log "Running the Phase 1 base journey in a real browser."
set +e
(
  cd "$FRONTEND_DIR"
  set -a; . "$ENV_FILE"; set +a
  # E2E_SKIP_WEB_SERVER: this script owns the frontend lifecycle, so Playwright must not
  # start a second one on the same port.
  E2E_SKIP_WEB_SERVER=true \
  npx playwright test --config playwright.phase1-base-journey.config.ts --workers=1 --retries=0
)
PLAYWRIGHT_STATUS=$?
set -e

log "Artifacts: $FRONTEND_DIR/playwright-report (HTML), $FRONTEND_DIR/test-results (traces, screenshots, video), $RUN_DIR (service logs)."
if [[ $PLAYWRIGHT_STATUS -ne 0 ]]; then
  die "Base journey FAILED (exit $PLAYWRIGHT_STATUS). Open $FRONTEND_DIR/playwright-report/index.html"
fi
log "BASE LEAD -> RFQ -> CUSTOMER QUOTE DRAFT: browser journey PASSED."
