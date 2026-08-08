#!/usr/bin/env bash
#
# Stands up the whole product locally — PostgreSQL, the API (with migrations and the
# extraction worker), and the Vite frontend — and bootstraps a Platform Owner so the
# operator console at /platform can actually be signed into.
#
# WHY THIS EXISTS. There was no way to look at the platform console without a deployed
# environment and credentials somebody else held. Reviewing a change to tenant
# provisioning by reading a diff is exactly how the provisioning form reached production
# unable to satisfy its own API contract.
#
# WHAT IT SEEDS, AND WHY IT HAS TO. A fresh database contains no Plan rows, and a Billable
# tenant now REQUIRES a plan — so without seeding, the first thing an operator meets is a
# mandatory dropdown with nothing in it. Plans and a rate card are created through the real
# audited endpoints rather than by INSERT, so what you see locally went through the same
# validation and left the same audit trail as production.
#
#   ./scripts/local/run-platform-console.sh          # start, print the URL, follow logs
#   ./scripts/local/run-platform-console.sh --stop   # tear everything down
#
# Ctrl-C stops the API and the frontend and removes the database container.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BACKEND_DIR="$REPO_ROOT/Backend/ERP_RFQ_Automation"
FRONTEND_DIR="$REPO_ROOT/Frontend"
RUN_DIR="$REPO_ROOT/.local-run"

PG_CONTAINER="nexora-local-pg"
PG_PORT="${NEXORA_PG_PORT:-55433}"
BACKEND_PORT="${NEXORA_BACKEND_PORT:-5192}"
FRONTEND_PORT="${NEXORA_FRONTEND_PORT:-5173}"
BACKEND_URL="http://127.0.0.1:${BACKEND_PORT}"
FRONTEND_URL="http://127.0.0.1:${FRONTEND_PORT}"

# Local-only credentials. The owner password is 12+ characters because PlatformOwnerSeeder
# refuses anything shorter rather than silently creating a weak account.
OWNER_EMAIL="${NEXORA_OWNER_EMAIL:-owner@nexora.local}"
OWNER_PASSWORD="${NEXORA_OWNER_PASSWORD:-LocalOwner!2026}"

log()  { printf '\033[1;36m[nexora]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[nexora]\033[0m %s\n' "$*"; }
die()  { printf '\033[1;31m[nexora]\033[0m %s\n' "$*" >&2; exit 1; }

BACKEND_PID=""
FRONTEND_PID=""

teardown() {
  echo
  log "Shutting down."
  # `( ... ) &` gives the SUBSHELL pid and `dotnet run` forks the real application, so the
  # ports are the only reliable handle on what to kill.
  for port in "$FRONTEND_PORT" "$BACKEND_PORT"; do
    pids="$(lsof -ti "tcp:${port}" 2>/dev/null || true)"
    [[ -n "$pids" ]] && kill $pids 2>/dev/null || true
  done
  docker rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
  log "Stopped. Database container removed; nothing persists between runs."
}

if [[ "${1:-}" == "--stop" ]]; then
  teardown
  exit 0
fi
trap teardown EXIT INT TERM

# ---------------------------------------------------------------- preflight
for tool in docker dotnet node npx curl python3 lsof; do
  command -v "$tool" >/dev/null 2>&1 || die "Missing required tool: $tool"
done
docker info >/dev/null 2>&1 || die "Docker is installed but not running. Start Docker Desktop."

# macOS ships bash 3.2, which has no `${var^^}`. Using it here turned a clear "port is in
# use" diagnostic into "bad substitution" — the script failed to explain its own failure, on
# the only platform this script is run on. The override variable name is spelled out instead.
for pair in "API:$BACKEND_PORT:NEXORA_BACKEND_PORT" "frontend:$FRONTEND_PORT:NEXORA_FRONTEND_PORT"; do
  name="${pair%%:*}"; rest="${pair#*:}"; port="${rest%%:*}"; override="${rest##*:}"
  if lsof -ti "tcp:${port}" >/dev/null 2>&1; then
    holder="$(lsof -nP -iTCP:"${port}" -sTCP:LISTEN 2>/dev/null | awk 'NR==2 {print $1" (pid "$2")"}')"
    die "Port $port is already in use${holder:+ by $holder} (needed for the $name). Free it, or set $override."
  fi
done

mkdir -p "$RUN_DIR"
# Secrets exist only for the lifetime of this run; nothing here is reused or persisted.
APP_SECRET="$(python3 -c 'import secrets;print(secrets.token_urlsafe(48))')"
PG_PASSWORD="$(python3 -c 'import secrets;print(secrets.token_urlsafe(24))')"

# ---------------------------------------------------------------- postgres
if docker ps -a --format '{{.Names}}' | grep -qx "$PG_CONTAINER"; then
  log "Removing a stale $PG_CONTAINER container from a previous run."
  docker rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
fi

log "Starting PostgreSQL on port $PG_PORT."
docker run -d --name "$PG_CONTAINER" \
  -e POSTGRES_PASSWORD="$PG_PASSWORD" -e POSTGRES_DB=nexora_local \
  -p "${PG_PORT}:5432" postgres:16-alpine >/dev/null

# The official image briefly runs a temporary server on a unix socket during
# initialisation, so `pg_isready` alone reports ready too early. Probe the TCP port too.
ready=0
for _ in $(seq 1 60); do
  if python3 - "$PG_PORT" <<'PROBE' >/dev/null 2>&1
import socket, sys
s = socket.create_connection(("127.0.0.1", int(sys.argv[1])), timeout=1); s.close()
PROBE
  then
    if docker exec "$PG_CONTAINER" psql -U postgres -d nexora_local -c 'select 1' >/dev/null 2>&1; then
      ready=1; break
    fi
  fi
  sleep 1
done
[[ "$ready" == "1" ]] || die "PostgreSQL never became ready on port $PG_PORT."

CONNECTION="Host=127.0.0.1;Port=${PG_PORT};Database=nexora_local;Username=postgres;Password=${PG_PASSWORD}"

# ---------------------------------------------------------------- backend
log "Building the API."
dotnet build "$BACKEND_DIR" --nologo -v quiet >"$RUN_DIR/build.log" 2>&1 || {
  tail -40 "$RUN_DIR/build.log"; die "Backend build failed — see $RUN_DIR/build.log"; }

log "Starting the API on $BACKEND_URL (applying ~200 migrations on first run)."
(
  cd "$BACKEND_DIR"
  ConnectionStrings__DefaultConnection="$CONNECTION" \
  Database__ApplyMigrationsOnStartup=true \
  Jwt__Key="$APP_SECRET" \
  CommercialFinance__ContactVerificationSecret="$APP_SECRET" \
  CommercialFinance__DunningProviderWebhookSecret="$APP_SECRET" \
  CommercialFinance__AuditActorSecret="$APP_SECRET" \
  Platform__BootstrapOwnerEmail="$OWNER_EMAIL" \
  Platform__BootstrapOwnerPassword="$OWNER_PASSWORD" \
  Notifications__AppBaseUrl="$FRONTEND_URL" \
  Notifications__OutboundGuard__Mode=DraftOnly \
  ASPNETCORE_ENVIRONMENT=Development \
  ASPNETCORE_URLS="$BACKEND_URL" \
  dotnet run --no-build --no-launch-profile
) >"$RUN_DIR/backend.log" 2>&1 &
BACKEND_PID=$!

log "Waiting for the API to become healthy."
healthy=0
for _ in $(seq 1 180); do
  if curl -fsS "$BACKEND_URL/health" >/dev/null 2>&1; then healthy=1; break; fi
  kill -0 "$BACKEND_PID" 2>/dev/null || { tail -40 "$RUN_DIR/backend.log"; die "API exited during startup."; }
  sleep 1
done
[[ "$healthy" == "1" ]] || { tail -40 "$RUN_DIR/backend.log"; die "API never became healthy."; }

# ---------------------------------------------------------------- commercial catalogue
# Through the audited endpoints, not INSERT: a plan that appears in the wizard should have
# passed the same validation and written the same audit row as one created in production.
log "Signing in as the platform owner."
TOKEN="$(curl -fsS -X POST "$BACKEND_URL/api/platform/auth/login" \
  -H 'Content-Type: application/json' \
  -d "{\"email\":\"$OWNER_EMAIL\",\"password\":\"$OWNER_PASSWORD\"}" \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')" \
  || die "Platform owner login failed — check Platform:BootstrapOwner* in $RUN_DIR/backend.log"

# Privileged platform policies require a server-bound MFA session. A fresh local database has
# a bootstrap Owner but no enrolled authenticator, so complete the real enrollment contract
# before using Owner-only catalogue endpoints. The Base32 seed is written only to the ignored
# run directory with owner-only permissions so the watched Chrome lane can derive a current TOTP;
# it is never printed or placed in Playwright artifacts.
MFA_ENABLED="$(curl -fsS "$BACKEND_URL/api/platform/auth/mfa" \
  -H "Authorization: Bearer $TOKEN" | python3 -c 'import json,sys; print(str(json.load(sys.stdin)["enabled"]).lower())')"
if [[ "$MFA_ENABLED" != "true" ]]; then
  log "Enrolling the disposable local Owner in privileged MFA."
  MFA_SECRET="$(curl -fsS -X POST "$BACKEND_URL/api/platform/auth/mfa/enrollment" \
    -H "Authorization: Bearer $TOKEN" | python3 -c 'import json,sys; print(json.load(sys.stdin)["secret"])')"
  umask 077
  printf '%s' "$MFA_SECRET" >"$RUN_DIR/platform-owner-mfa-secret"
  chmod 600 "$RUN_DIR/platform-owner-mfa-secret"
  MFA_CODE="$(python3 - "$MFA_SECRET" <<'PY'
import base64, hashlib, hmac, struct, sys, time
secret = base64.b32decode(sys.argv[1] + '=' * ((8 - len(sys.argv[1]) % 8) % 8))
step = int(time.time()) // 30
digest = hmac.new(secret, struct.pack('>Q', step), hashlib.sha1).digest()
offset = digest[-1] & 15
value = (struct.unpack('>I', digest[offset:offset + 4])[0] & 0x7fffffff) % 1000000
print(f'{value:06d}')
PY
)"
  RECOVERY_CODE="$(curl -fsS -X POST "$BACKEND_URL/api/platform/auth/mfa/enrollment/confirm" \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d "{\"totpCode\":\"$MFA_CODE\"}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["recoveryCodes"][0])')" \
    || die "Platform Owner MFA enrollment failed — see $RUN_DIR/backend.log"
  CHALLENGE_ID="$(curl -fsS -X POST "$BACKEND_URL/api/platform/auth/login" \
    -H 'Content-Type: application/json' \
    -d "{\"email\":\"$OWNER_EMAIL\",\"password\":\"$OWNER_PASSWORD\"}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["mfaChallengeId"])')"
  TOKEN="$(curl -fsS -X POST "$BACKEND_URL/api/platform/auth/mfa/challenge" \
    -H 'Content-Type: application/json' \
    -d "{\"challengeId\":\"$CHALLENGE_ID\",\"recoveryCode\":\"$RECOVERY_CODE\"}" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')" \
    || die "Platform Owner MFA challenge failed — see $RUN_DIR/backend.log"
  unset MFA_SECRET MFA_CODE RECOVERY_CODE CHALLENGE_ID
fi

create_plan() {  # code name weight concurrent docs seats priceUsd
  if curl -fsS -o /dev/null -X POST "$BACKEND_URL/api/platform/plans" \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d "{\"code\":\"$1\",\"name\":\"$2\",\"weight\":$3,\"maxConcurrentExtractionJobs\":$4,
         \"maxDocsPerMonth\":$5,\"maxSeats\":$6,\"monthlyPriceUsd\":$7,\"isActive\":true}"
  then
    log "  plan: $2 (\$$7/month)"
  else
    warn "  plan '$1' was not created (see $RUN_DIR/backend.log)."
  fi
}

log "Seeding the commercial catalogue."
create_plan starter    "Starter"    1  2   1000  5   499
create_plan growth     "Growth"     5  6   10000 25  1999
create_plan enterprise "Enterprise" 10 20  100000 250 7999

# Deliberately priced on every meter the statement engine knows about, so a provisioned
# tenant has somewhere real to pin and the revenue-risk warnings can be seen resolving.
# datetime.timezone.utc rather than datetime.UTC: the alias only exists on Python 3.11+, and
# this script has to run on whatever interpreter the machine ships with.
EFFECTIVE_FROM="$(python3 -c 'import datetime;print((datetime.datetime.now(datetime.timezone.utc).replace(day=1)-datetime.timedelta(days=365)).strftime("%Y-%m-%dT00:00:00Z"))')"
if curl -fsS -o /dev/null -X POST "$BACKEND_URL/api/platform/billing/rate-cards" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d "{\"code\":\"standard-2026\",\"currency\":\"USD\",\"effectiveFromUtc\":\"$EFFECTIVE_FROM\",
       \"effectiveToUtc\":null,\"isActive\":true,\"lines\":[
        {\"meterKey\":\"documents\",\"includedQuantity\":1000,\"unitPrice\":0.25,\"unit\":\"document\",\"tierNote\":null},
        {\"meterKey\":\"ai.tokens.external\",\"includedQuantity\":500000,\"unitPrice\":0.02,\"unit\":\"1K tokens\",\"tierNote\":null},
        {\"meterKey\":\"seats\",\"includedQuantity\":5,\"unitPrice\":25,\"unit\":\"seat\",\"tierNote\":null},
        {\"meterKey\":\"storage.gb\",\"includedQuantity\":10,\"unitPrice\":0.10,\"unit\":\"GiB\",\"tierNote\":null}]}"
then
  log "  rate card: standard-2026 (USD)"
else
  # Never fatal. The catalogue is a convenience for the first run; a stack that is up and
  # signed-in is worth more than one that tore itself down over an optional price list.
  warn "  rate card was not created — provisioning still works, it just warns that pricing floats."
fi

# ---------------------------------------------------------------- frontend
log "Starting the frontend on $FRONTEND_URL."
(
  cd "$FRONTEND_DIR"
  [[ -d node_modules ]] || npm ci
  VITE_API_BASE_URL="$BACKEND_URL" npx vite --port "$FRONTEND_PORT" --strictPort --host 127.0.0.1
) >"$RUN_DIR/frontend.log" 2>&1 &
FRONTEND_PID=$!

for _ in $(seq 1 60); do
  curl -fsS "$FRONTEND_URL" >/dev/null 2>&1 && break
  sleep 1
done

cat <<BANNER

  ────────────────────────────────────────────────────────────────
   Operator console   ${FRONTEND_URL}/platform/tenants
   Email              ${OWNER_EMAIL}
   Password           ${OWNER_PASSWORD}
   MFA seed file      ${RUN_DIR}/platform-owner-mfa-secret (mode 600; never printed)
  ────────────────────────────────────────────────────────────────

   Click "Provision Tenant" for the four-step wizard.

   Invitation emails are NOT sent — the notifications provider is the
   console logger — so the handover screen shows the activation link
   directly. Paste it into the browser to finish as the customer would.

   Logs: .local-run/backend.log  .local-run/frontend.log
   Stop: Ctrl-C, or ./scripts/local/run-platform-console.sh --stop

BANNER

wait "$BACKEND_PID" "$FRONTEND_PID"
