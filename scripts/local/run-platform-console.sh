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

PG_PORT="${NEXORA_PG_PORT:-55433}"
BACKEND_PORT="${NEXORA_BACKEND_PORT:-5192}"
FRONTEND_PORT="${NEXORA_FRONTEND_PORT:-5173}"

# The container name is DERIVED FROM THE PORT, and must stay that way.
#
# It used to be the literal `nexora-local-pg` while NEXORA_PG_PORT was still honoured, which
# made the port look like an isolation knob when it was not: startup and teardown both run
# `docker rm -f` on that fixed name, so a second stack started on a different port destroyed
# the FIRST stack's database — and announced it as "Removing a stale container from a previous
# run", which reads like routine cleanup rather than the deletion of someone else's data.
# Two concurrent runs on different ports must not be able to touch each other's container.
PG_CONTAINER="nexora-local-pg-${PG_PORT}"
BACKEND_URL="http://127.0.0.1:${BACKEND_PORT}"
FRONTEND_URL="http://127.0.0.1:${FRONTEND_PORT}"

# Local-only credentials. Emails default; PASSWORDS DELIBERATELY DO NOT.
#
# SEC-G9: both passwords used to carry a literal `${VAR:-<default>}` fallback, so the operator
# and checker credentials for the platform control plane were published in a tracked file and
# were what every run that did not set the variable actually used. Loopback binding is not a
# mitigation for that — it decides who can reach the console, not what the password is, and the
# same literal is one `docker run -p` or one copied-into-a-shared-environment away from being a
# real credential. scripts/e2e/run-phase1-base-journey.sh already generates its secrets per run;
# this follows it, and refuses rather than inventing one silently.
OWNER_EMAIL="${NEXORA_OWNER_EMAIL:-owner@nexora.local}"
CHECKER_EMAIL="${NEXORA_CHECKER_EMAIL:-checker@nexora.local}"
OWNER_PASSWORD="${NEXORA_OWNER_PASSWORD:-}"
CHECKER_PASSWORD="${NEXORA_CHECKER_PASSWORD:-}"

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

# The database container is now per-port, so two stacks can run side by side without one
# deleting the other's data. Everything under .local-run/ is still SHARED, though: the logs and
# both MFA seed files are fixed paths, so a second concurrent stack overwrites the first's
# platform-owner-mfa-secret — and the PART C harness reads that fixed path, so the first stack's
# authenticator codes silently stop working. Nothing is destroyed and no restart is needed, but
# the operator should know which run last wrote those files. Said out loud rather than fixed by
# moving the paths, because the harness and the docs both address them by name.
if [[ "$PG_PORT" != "55433" || "$BACKEND_PORT" != "5192" || "$FRONTEND_PORT" != "5173" ]]; then
  warn "Non-default ports: this run owns container $PG_CONTAINER only."
  warn "Note .local-run/ logs and MFA seed files are shared paths — this run will overwrite them."
fi

# SEC-G9: fail here rather than fall back to a committed literal. PlatformOwnerSeeder refuses
# anything under 12 characters rather than silently creating a weak account, so the check
# mirrors it — a run that dies at the seeder five steps later, with the database already up, is
# a worse version of the same message.
for pair in "NEXORA_OWNER_PASSWORD:$OWNER_PASSWORD" "NEXORA_CHECKER_PASSWORD:$CHECKER_PASSWORD"; do
  var="${pair%%:*}"; value="${pair#*:}"
  if [[ -z "$value" ]]; then
    die "$var is not set. This script no longer ships a default password for the platform
     control plane. Choose one for this machine, e.g.:

       export $var=\"\$(python3 -c 'import secrets;print(\"Local!\"+secrets.token_urlsafe(18))')\"

     Both NEXORA_OWNER_PASSWORD and NEXORA_CHECKER_PASSWORD are required."
  fi
  [[ ${#value} -ge 12 ]] || die "$var must be at least 12 characters; PlatformOwnerSeeder refuses anything shorter."
done

mkdir -p "$RUN_DIR"
# Secrets exist only for the lifetime of this run; nothing here is reused or persisted.
APP_SECRET="$(python3 -c 'import secrets;print(secrets.token_urlsafe(48))')"
PG_PASSWORD="$(python3 -c 'import secrets;print(secrets.token_urlsafe(24))')"

# ---- the one secret that must NOT be per-run -------------------------------------------------
# Security:SecretProtectionKey encrypts stored mail credentials at rest (AES-256-GCM). With no key
# configured the API generates a PROCESS-LIFETIME one, so anything saved on the platform email
# screen becomes undecryptable the moment the API restarts — which, on a script that restarts the
# API every run, is always. The operator's symptom is "I configured SMTP, and it forgot it again",
# which is indistinguishable from the settings never saving at all.
#
# So this one is generated once and kept, in the same gitignored run directory as the MFA seeds and
# with the same 0600. It is a DEVELOPMENT key: production supplies its own and refuses to boot
# without it (SecretProtection.CreateFromConfiguration).
SECRET_KEY_FILE="$RUN_DIR/secret-protection-key"
if [[ ! -s "$SECRET_KEY_FILE" ]]; then
  python3 -c 'import base64,os;print(base64.b64encode(os.urandom(32)).decode())' >"$SECRET_KEY_FILE"
  chmod 600 "$SECRET_KEY_FILE"
  log "Generated a persistent local secret-protection key at $SECRET_KEY_FILE."
fi
SECRET_PROTECTION_KEY="$(tr -d '\n' <"$SECRET_KEY_FILE")"

# ---- outbound containment ---------------------------------------------------------------------
# DraftOnly by default: a local database full of plausible-looking addresses must not be one saved
# setting away from mailing real customers. It is a default rather than a hard-code, because with
# it hard-coded there was NO way to prove outbound mail works locally — every test send returned
# "nothing was transmitted", and an operator verifying an SMTP fix could not tell a correct
# configuration from a broken one.
#
#   NEXORA_OUTBOUND_GUARD=Live ./scripts/local/run-platform-console.sh
#
# Anything other than DraftOnly is announced loudly below, because the difference is whether real
# people receive what this box sends.
OUTBOUND_GUARD="${NEXORA_OUTBOUND_GUARD:-DraftOnly}"
case "$OUTBOUND_GUARD" in
  Live|AllowListOnly|Redirect|DraftOnly) ;;
  *) die "NEXORA_OUTBOUND_GUARD must be Live, AllowListOnly, Redirect or DraftOnly (got '$OUTBOUND_GUARD')." ;;
esac
if [[ "$OUTBOUND_GUARD" != "DraftOnly" ]]; then
  log "OUTBOUND GUARD IS '$OUTBOUND_GUARD' — this run can transmit real email to real addresses."
fi

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

# Cors__AllowedOrigins__0 is passed because NEXORA_FRONTEND_PORT must reach the BACKEND too.
# TransportSecurityPolicy.ResolveCorsOrigins only adds a fixed loopback set in Development, and
# that set is the DEFAULT port. A run on any other frontend port therefore started cleanly,
# served the SPA, and then failed every single API call with a CORS error — the console rendered
# its sign-in form and answered "Unable to reach the platform control plane", which reads like a
# backend outage rather than a misconfigured origin. The port override is only honest if the
# origin it produces is allowed.
log "Starting the API on $BACKEND_URL (applying ~200 migrations on first run)."
(
  cd "$BACKEND_DIR"
  ConnectionStrings__DefaultConnection="$CONNECTION" \
  Database__ApplyMigrationsOnStartup=true \
  Jwt__Key="$APP_SECRET" \
  CommercialFinance__ContactVerificationSecret="$APP_SECRET" \
  CommercialFinance__DunningProviderWebhookSecret="$APP_SECRET" \
  CommercialFinance__AuditActorSecret="$APP_SECRET" \
  Observability__Prometheus__ScrapeKey="$APP_SECRET" \
  Platform__BootstrapOwnerEmail="$OWNER_EMAIL" \
  Platform__BootstrapOwnerPassword="$OWNER_PASSWORD" \
  Notifications__AppBaseUrl="$FRONTEND_URL" \
  Notifications__OutboundGuard__Mode="$OUTBOUND_GUARD" \
  Security__SecretProtectionKey="$SECRET_PROTECTION_KEY" \
  Cors__AllowedOrigins__0="$FRONTEND_URL" \
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

# Paid billing and cutover approval are maker/checker operations. Seed a second,
# independently authenticated disposable Owner through the real audited IAM API,
# then enroll its own authenticator. The two secrets are never interchangeable.
log "Creating the independent disposable billing checker."
curl -fsS -o /dev/null -X POST "$BACKEND_URL/api/platform/users" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d "{\"email\":\"$CHECKER_EMAIL\",\"password\":\"$CHECKER_PASSWORD\",\"role\":\"Owner\",\"displayName\":\"Local Billing Checker\"}" \
  || die "Independent checker creation failed — see $RUN_DIR/backend.log"
CHECKER_TOKEN="$(curl -fsS -X POST "$BACKEND_URL/api/platform/auth/login" \
  -H 'Content-Type: application/json' \
  -d "{\"email\":\"$CHECKER_EMAIL\",\"password\":\"$CHECKER_PASSWORD\"}" \
  | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')" \
  || die "Independent checker login failed — see $RUN_DIR/backend.log"
CHECKER_MFA_SECRET="$(curl -fsS -X POST "$BACKEND_URL/api/platform/auth/mfa/enrollment" \
  -H "Authorization: Bearer $CHECKER_TOKEN" | python3 -c 'import json,sys; print(json.load(sys.stdin)["secret"])')"
umask 077
printf '%s' "$CHECKER_MFA_SECRET" >"$RUN_DIR/platform-checker-mfa-secret"
chmod 600 "$RUN_DIR/platform-checker-mfa-secret"
CHECKER_MFA_CODE="$(python3 - "$CHECKER_MFA_SECRET" <<'PY'
import base64, hashlib, hmac, struct, sys, time
secret = base64.b32decode(sys.argv[1] + '=' * ((8 - len(sys.argv[1]) % 8) % 8))
step = int(time.time()) // 30
digest = hmac.new(secret, struct.pack('>Q', step), hashlib.sha1).digest()
offset = digest[-1] & 15
value = (struct.unpack('>I', digest[offset:offset + 4])[0] & 0x7fffffff) % 1000000
print(f'{value:06d}')
PY
)"
curl -fsS -o /dev/null -X POST "$BACKEND_URL/api/platform/auth/mfa/enrollment/confirm" \
  -H "Authorization: Bearer $CHECKER_TOKEN" -H 'Content-Type: application/json' \
  -d "{\"totpCode\":\"$CHECKER_MFA_CODE\"}" \
  || die "Independent checker MFA enrollment failed — see $RUN_DIR/backend.log"
unset CHECKER_TOKEN CHECKER_MFA_SECRET CHECKER_MFA_CODE

PLAN_FEATURES='{\"module.rfq\":true,\"module.quotes\":true,\"module.orders\":true,\"module.procurement\":true,\"module.inventory\":true,\"capability.ai\":true,\"capability.ocr\":true,\"capability.api\":false,\"capability.email-intake\":true,\"capability.supplier-search\":true,\"capability.integrations\":true,\"capability.exports\":true,\"capability.automation\":false,\"capability.sso\":false,\"capability.scim\":false,\"capability.dedicated-resources\":false}'
create_plan() {  # code name weight concurrent docs seats priceUsd
  if curl -fsS -o /dev/null -X POST "$BACKEND_URL/api/platform/plans" \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d "{\"code\":\"$1\",\"name\":\"$2\",\"weight\":$3,\"maxConcurrentExtractionJobs\":$4,
         \"maxDocsPerMonth\":$5,\"maxSeats\":$6,\"monthlyPriceUsd\":$7,
         \"features\":\"$PLAN_FEATURES\",\"isActive\":true}"
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

# Price only the closed catalogue's BILLING_CERTIFIED meters. Page/OCR/storage
# remain observable or blocked until their source and reconciliation contracts are certified.
# datetime.timezone.utc rather than datetime.UTC: the alias only exists on Python 3.11+, and
# this script has to run on whatever interpreter the machine ships with.
EFFECTIVE_FROM="$(python3 -c 'import datetime;print((datetime.datetime.now(datetime.timezone.utc).replace(day=1)-datetime.timedelta(days=365)).strftime("%Y-%m-%dT00:00:00Z"))')"
if curl -fsS -o /dev/null -X POST "$BACKEND_URL/api/platform/billing/rate-cards" \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d "{\"code\":\"standard-2026\",\"currency\":\"USD\",\"effectiveFromUtc\":\"$EFFECTIVE_FROM\",
       \"effectiveToUtc\":null,\"isActive\":true,\"lines\":[
        {\"meterKey\":\"documents\",\"includedQuantity\":1000,\"unitPrice\":0.25,\"unit\":\"document\",\"tierNote\":null},
        {\"meterKey\":\"ai.tokens.external\",\"includedQuantity\":500000,\"unitPrice\":0.02,\"unit\":\"1K tokens\",\"tierNote\":null},
        {\"meterKey\":\"seats\",\"includedQuantity\":5,\"unitPrice\":25,\"unit\":\"seat\",\"tierNote\":null}]}"
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
   Password           the value of \$NEXORA_OWNER_PASSWORD (not printed)
   MFA seed file      ${RUN_DIR}/platform-owner-mfa-secret (mode 600; never printed)
   Checker email      ${CHECKER_EMAIL}
   Checker password   the value of \$NEXORA_CHECKER_PASSWORD (not printed)
   Checker MFA file   ${RUN_DIR}/platform-checker-mfa-secret (mode 600; never printed)
   Outbound guard     ${OUTBOUND_GUARD}
   DB container       ${PG_CONTAINER} (port ${PG_PORT}) — this run owns it
                      and removes ONLY this container on shutdown.
  ────────────────────────────────────────────────────────────────

   Click "Provision Tenant" for the four-step wizard.

   Invitation emails are NOT sent — the notifications provider starts as
   the console logger — so the handover screen shows the activation link
   directly. Paste it into the browser to finish as the customer would.

   To make mail actually leave this box: configure SMTP at
   ${FRONTEND_URL}/platform/email, and re-run with
   NEXORA_OUTBOUND_GUARD=Live. With the guard on DraftOnly (the default)
   nothing is transmitted however correct the SMTP settings are, and a
   test send will say so rather than appearing to succeed.

   Logs: .local-run/backend.log  .local-run/frontend.log
   Stop: Ctrl-C, or ./scripts/local/run-platform-console.sh --stop

BANNER

wait "$BACKEND_PID" "$FRONTEND_PID"
