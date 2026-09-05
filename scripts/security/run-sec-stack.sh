#!/usr/bin/env bash
# Security-lane disposable stack. A fork of scripts/e2e/run-enterprise-commercial-journey.sh that
# (a) uses KNOWN credentials persisted to the run dir so the dynamic probe can log in on every
# rerun, and (b) LEAVES the backend + frontend RUNNING (the official runner stops the backend at
# the end of each suite). Same backend env/guards as the official runner: Development, DraftOnly
# outbound guard, loopback CORS. Ports are the security lane's own: 5204/5184/55444.
#
#   ./scripts/security/run-sec-stack.sh            # first run builds
#   SEC_SKIP_BUILD=1 ./scripts/security/run-sec-stack.sh
#   ./scripts/security/run-sec-stack.sh down       # tear the stack down
set -Eeuo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BACKEND_PROJECT="$REPO_ROOT/Backend/ERP_RFQ_Automation/ERP_RFQ_Automation.csproj"
FIXTURE_PROJECT="$REPO_ROOT/Backend/ERP_RFQ_Automation.AcceptanceFixture/ERP_RFQ_Automation.AcceptanceFixture.csproj"
FRONTEND_DIR="$REPO_ROOT/Frontend"
RUN_DIR="${SEC_RUN_DIR:-$REPO_ROOT/.security-e2e-run}"
PG_CONTAINER="${E2E_PG_CONTAINER:-nexora-e2e-sec}"
PG_PORT="${E2E_PG_PORT:-55444}"
BACKEND_PORT="${E2E_BACKEND_PORT:-5204}"
FRONTEND_PORT="${E2E_FRONTEND_PORT:-5184}"
BACKEND_URL="http://127.0.0.1:${BACKEND_PORT}"
FRONTEND_URL="http://127.0.0.1:${FRONTEND_PORT}"
DB_NAME="nexora_security"

log() { printf '\n\033[1m[sec-stack]\033[0m %s\n' "$*"; }
die() { printf '\n\033[31m[sec-stack] %s\033[0m\n' "$*" >&2; exit 1; }
listener_for() { lsof -ti "tcp:$1" -sTCP:LISTEN 2>/dev/null || true; }

teardown() {
  log "Tearing down security stack."
  for p in "$BACKEND_PORT" "$FRONTEND_PORT"; do
    l="$(listener_for "$p")"; [[ -n "$l" ]] && kill $l 2>/dev/null || true
  done
  docker rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
  log "Done."
}

if [[ "${1:-}" == "down" ]]; then teardown; exit 0; fi

mkdir -p "$RUN_DIR"
SECRETS_ENV="$RUN_DIR/secrets.env"

if [[ -f "$SECRETS_ENV" ]]; then
  # shellcheck disable=SC1090
  source "$SECRETS_ENV"
else
  PG_PASSWORD="$(python3 -c 'import secrets; print(secrets.token_urlsafe(24))')"
  APP_SECRET="$(python3 -c 'import secrets; print(secrets.token_urlsafe(48))')"
  PLATFORM_SECRET="$(python3 -c 'import secrets; print(secrets.token_urlsafe(48))')"
  ACCEPTANCE_PASSWORD="Sec!$(python3 -c 'import secrets; print(secrets.token_urlsafe(18))')"
  PROTECTION_KEY="$(python3 -c 'import base64,secrets; print(base64.b64encode(secrets.token_bytes(32)).decode())')"
  INTEGRATION_SECRET="$(python3 -c 'import secrets; print(secrets.token_urlsafe(40))')"
  {
    echo "PG_PASSWORD='$PG_PASSWORD'"
    echo "APP_SECRET='$APP_SECRET'"
    echo "PLATFORM_SECRET='$PLATFORM_SECRET'"
    echo "ACCEPTANCE_PASSWORD='$ACCEPTANCE_PASSWORD'"
    echo "PROTECTION_KEY='$PROTECTION_KEY'"
    echo "INTEGRATION_SECRET='$INTEGRATION_SECRET'"
  } > "$SECRETS_ENV"
  chmod 600 "$SECRETS_ENV"
fi

for tool in docker dotnet node npx curl python3 lsof; do
  command -v "$tool" >/dev/null 2>&1 || die "Required tool not found: $tool"
done
docker info >/dev/null 2>&1 || die "Docker engine unavailable."
[[ -d "$FRONTEND_DIR/node_modules" ]] || die "Run npm ci in Frontend first."

for pair in "backend:$BACKEND_PORT" "frontend:$FRONTEND_PORT" "postgres:$PG_PORT"; do
  port="${pair##*:}"; holder="$(listener_for "$port")"
  [[ -z "$holder" ]] || die "Port $port in use by $holder (run '$0 down' first)."
done

if ! docker ps -a --format '{{.Names}}' | grep -qx "$PG_CONTAINER"; then
  log "Starting disposable PostgreSQL '$PG_CONTAINER' on $PG_PORT."
  docker run -d --name "$PG_CONTAINER" -e POSTGRES_PASSWORD="$PG_PASSWORD" \
    -p "${PG_PORT}:5432" postgres:16-alpine >/dev/null
  for ((i=1;i<=60;i++)); do
    docker exec "$PG_CONTAINER" psql -U postgres -d postgres -c 'select 1' >/dev/null 2>&1 && break
    sleep 2
  done
fi

if [[ "${SEC_SKIP_BUILD:-0}" != "1" ]]; then
  log "Building backend + fixture."
  dotnet build "$FIXTURE_PROJECT" --nologo -v minimal >"$RUN_DIR/dotnet-build.log" 2>&1 || {
    tail -80 "$RUN_DIR/dotnet-build.log" >&2; die "Backend/fixture build failed."; }
  log "Building frontend."
  ( cd "$FRONTEND_DIR" && npm run build ) >"$RUN_DIR/frontend-build.log" 2>&1 || {
    tail -120 "$RUN_DIR/frontend-build.log" >&2; die "Frontend build failed."; }
fi

CONN="Host=127.0.0.1;Port=${PG_PORT};Database=${DB_NAME};Username=postgres;Password=${PG_PASSWORD}"
STORAGE_ROOT="$RUN_DIR/storage"; mkdir -p "$STORAGE_ROOT"

start_backend() {
  local apply="$1" logf="$2"
  ( cd "$(dirname "$BACKEND_PROJECT")"
    ConnectionStrings__DefaultConnection="$CONN" \
    Database__ApplyMigrationsOnStartup="$apply" \
    Database__ValidateRequestPathOnStartup=true \
    Jwt__Key="$APP_SECRET" Jwt__PlatformKey="$PLATFORM_SECRET" \
    Security__SecretProtectionKey="$PROTECTION_KEY" \
    Storage__RootPath="$STORAGE_ROOT" \
    CommercialFinance__ContactVerificationSecret="$APP_SECRET" \
    CommercialFinance__DunningProviderWebhookSecret="$APP_SECRET" \
    CommercialFinance__AuditActorSecret="$APP_SECRET" \
    Observability__Prometheus__ScrapeKey="$APP_SECRET" \
    ProcurementIntegration__Tenants__80101__SourceSystem="Disposable ERP" \
    ProcurementIntegration__Tenants__80101__SharedSecret="$INTEGRATION_SECRET" \
    Notifications__OutboundGuard__Mode=DraftOnly \
    Cors__AllowedOrigins__0=http://127.0.0.1:5184 \
    ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS="$BACKEND_URL" \
    dotnet run --project "$BACKEND_PROJECT" --no-build --no-launch-profile
  ) >"$logf" 2>&1 &
  echo $!
}

wait_http() {
  local url="$1" label="$2" n="${3:-120}"
  for ((i=1;i<=n;i++)); do
    c="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$url" 2>/dev/null || true)"
    [[ -n "$c" && "$c" != "000" ]] && { log "$label up (HTTP $c)"; return 0; }
    sleep 2
  done
  die "$label did not answer at $url"
}

# Fresh DB unless it already exists (idempotent-ish; teardown drops container).
if ! docker exec "$PG_CONTAINER" psql -U postgres -lqt | cut -d'|' -f1 | grep -qw "$DB_NAME"; then
  log "Creating database $DB_NAME and applying migrations."
  docker exec "$PG_CONTAINER" psql -U postgres -d postgres -v ON_ERROR_STOP=1 \
    -c "CREATE DATABASE \"${DB_NAME}\"" >"$RUN_DIR/db-create.log" 2>&1
  MPID=$(start_backend true "$RUN_DIR/migrate-backend.log")
  wait_http "$BACKEND_URL/health" "Backend(migrate)" 210
  kill "$MPID" 2>/dev/null || true; sleep 3
  l="$(listener_for "$BACKEND_PORT")"; [[ -n "$l" ]] && kill -9 $l 2>/dev/null || true

  log "Loading acceptance fixture."
  NEXORA_ACCEPTANCE_CONNECTION="$CONN" \
  NEXORA_ACCEPTANCE_PASSWORD="$ACCEPTANCE_PASSWORD" \
  NEXORA_ACCEPTANCE_SECRET_PROTECTION_KEY="$PROTECTION_KEY" \
  NEXORA_ACCEPTANCE_STORAGE_ROOT="$STORAGE_ROOT" \
  dotnet run --project "$FIXTURE_PROJECT" --no-build --no-launch-profile >"$RUN_DIR/fixture.log" 2>&1 || {
    tail -160 "$RUN_DIR/fixture.log" >&2; die "Fixture load failed."; }
fi

log "Starting backend on $BACKEND_URL (left running)."
BPID=$(start_backend false "$RUN_DIR/run-backend.log")
echo "$BPID" > "$RUN_DIR/backend.pid"
wait_http "$BACKEND_URL/health" "Backend" 210

# Frontend (vite dev, matches official runner's dev-server behaviour)
if [[ -z "$(listener_for "$FRONTEND_PORT")" ]]; then
  log "Starting frontend on $FRONTEND_URL (left running)."
  ( cd "$FRONTEND_DIR" && VITE_API_BASE_URL="$BACKEND_URL" npx vite --port "$FRONTEND_PORT" --strictPort --host 127.0.0.1 ) \
    >"$RUN_DIR/frontend.log" 2>&1 &
  echo $! > "$RUN_DIR/frontend.pid"
  wait_http "$FRONTEND_URL" "Frontend" 90
fi

# Enroll disposable platform owner in MFA (mirrors official runner).
log "Enrolling platform owner in MFA."
PLAT_EMAIL="owner@acceptance.local"
LOGIN_JSON="$(curl -fsS -X POST "$BACKEND_URL/api/platform/auth/login" -H 'Content-Type: application/json' \
  -d "{\"email\":\"$PLAT_EMAIL\",\"password\":\"$ACCEPTANCE_PASSWORD\"}" 2>/dev/null || true)"
if [[ -n "$LOGIN_JSON" ]]; then
  PTOK="$(printf '%s' "$LOGIN_JSON" | python3 -c 'import json,sys;print(json.load(sys.stdin).get("token",""))' 2>/dev/null || true)"
  if [[ -n "$PTOK" ]]; then
    SECRET="$(curl -fsS -X POST "$BACKEND_URL/api/platform/auth/mfa/enrollment" -H "Authorization: Bearer $PTOK" 2>/dev/null \
      | python3 -c 'import json,sys;print(json.load(sys.stdin).get("secret",""))' 2>/dev/null || true)"
    if [[ -n "$SECRET" ]]; then
      CODE="$(python3 - "$SECRET" <<'PY'
import base64,hashlib,hmac,struct,sys,time
raw=sys.argv[1]; secret=base64.b32decode(raw+'='*((8-len(raw)%8)%8))
c=struct.pack('>Q',int(time.time())//30); d=hmac.new(secret,c,hashlib.sha1).digest()
o=d[-1]&15; print(f'{((struct.unpack(">I",d[o:o+4])[0]&0x7fffffff)%1000000):06d}')
PY
)"
      curl -fsS -o /dev/null -X POST "$BACKEND_URL/api/platform/auth/mfa/enrollment/confirm" \
        -H "Authorization: Bearer $PTOK" -H 'Content-Type: application/json' -d "{\"totpCode\":\"$CODE\"}" 2>/dev/null || true
      echo "PLATFORM_TOTP_SECRET='$SECRET'" >> "$SECRETS_ENV"
    fi
  fi
fi

# Emit the probe env file: identities + fixture ids.
{
  echo "# generated by run-sec-stack.sh"
  echo "SEC_API_URL=$BACKEND_URL"
  echo "SEC_FRONTEND_URL=$FRONTEND_URL"
  echo "SEC_ACCEPTANCE_PASSWORD=$ACCEPTANCE_PASSWORD"
  echo "SEC_MANAGER_EMAIL=manager@release01c1.local"
  echo "SEC_FINANCE_EMAIL=finance@release01c1.local"
  echo "SEC_EDITOR_EMAIL=editor@release01c1.local"
  echo "SEC_DENIED_EMAIL=denied@release01c1.local"
  echo "SEC_OTHER_EMAIL=other@release01c1.local"
  echo "SEC_OWNER_EMAIL=owner@release01c1.local"
  echo "SEC_PLATFORM_EMAIL=owner@acceptance.local"
  echo "SEC_TENANT_ID=80101"
  echo "SEC_OTHER_TENANT_ID=80102"
  grep -E '^(E2E_|ABC_|SARAH_|AHMED_|SALES_|PRODUCT_|WAREHOUSE_|INVENTORY_|CUSTOMER_|ORIGINAL_|BUSINESS_UNIT|OTHER_BUSINESS)' "$RUN_DIR/fixture.log" 2>/dev/null || true
} > "$RUN_DIR/probe.env"

log "STACK UP. Probe env: $RUN_DIR/probe.env"
