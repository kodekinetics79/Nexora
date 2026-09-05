#!/usr/bin/env bash
# Enterprise commercial pilot gate: tenant sales/RBAC, Customer 360, inventory and reservation,
# sourcing, supplier quote/award/PO, quote-to-cash and fulfilment against the real HTTP API.
#
# The two suites deliberately receive independent databases. They mutate quotes, orders,
# reservations and platform tenants, so sharing one fixture makes the second result depend on
# suite order and can conceal replay/idempotency defects.
#
# Prerequisites: Docker, .NET 8, Node, the Frontend dependencies and Playwright Chromium.
#
#   ./scripts/e2e/run-enterprise-commercial-journey.sh
#   E2E_ENTERPRISE_SUITE=commercial-v2 ./scripts/e2e/run-enterprise-commercial-journey.sh
#   E2E_ENTERPRISE_SUITE=core-commercial ./scripts/e2e/run-enterprise-commercial-journey.sh
#
# Environment contract (all optional runner controls):
#   E2E_ENTERPRISE_SUITE     all | commercial-v2 | core-commercial (default: all)
#   E2E_RUN_DIR              retained logs/storage (default: .enterprise-e2e-run)
#   E2E_PG_CONTAINER/PORT    disposable PostgreSQL identity/address
#   E2E_BACKEND_PORT         real API port (default: 5192)
#   E2E_FRONTEND_PORT        Vite port (default: 5173)
#   E2E_KEEP_STACK=1         preserve the disposable stack for diagnosis
#   E2E_SKIP_BUILD=1         local rerun only; CI should retain the default full build
#
# Fixture output is parsed as data, never sourced as shell. Several values contain spaces.
set -Eeuo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BACKEND_PROJECT="$REPO_ROOT/Backend/ERP_RFQ_Automation/ERP_RFQ_Automation.csproj"
FIXTURE_PROJECT="$REPO_ROOT/Backend/ERP_RFQ_Automation.AcceptanceFixture/ERP_RFQ_Automation.AcceptanceFixture.csproj"
FRONTEND_DIR="$REPO_ROOT/Frontend"
RUN_DIR="${E2E_RUN_DIR:-$REPO_ROOT/.enterprise-e2e-run}"
PG_CONTAINER="${E2E_PG_CONTAINER:-nexora-enterprise-e2e-pg}"
PG_PORT="${E2E_PG_PORT:-55435}"
BACKEND_PORT="${E2E_BACKEND_PORT:-5192}"
FRONTEND_PORT="${E2E_FRONTEND_PORT:-5173}"
BACKEND_URL="http://127.0.0.1:${BACKEND_PORT}"
FRONTEND_URL="http://127.0.0.1:${FRONTEND_PORT}"
SELECTED_SUITE="${E2E_ENTERPRISE_SUITE:-all}"

STARTED_PG=0
BACKEND_PID=""
FRONTEND_PID=""
CURRENT_BACKEND_LOG=""

log() { printf '\n\033[1m[enterprise-e2e]\033[0m %s\n' "$*"; }
die() { printf '\n\033[31m[enterprise-e2e] %s\033[0m\n' "$*" >&2; exit 1; }

listener_for() {
  lsof -ti "tcp:$1" -sTCP:LISTEN 2>/dev/null || true
}

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

stop_backend() {
  stop_port_process "$BACKEND_PORT" "$BACKEND_PID"
  BACKEND_PID=""
}

cleanup() {
  local code=$?
  if [[ "${E2E_KEEP_STACK:-0}" == "1" ]]; then
    log "E2E_KEEP_STACK=1 — preserving services and $PG_CONTAINER for diagnosis."
    return "$code"
  fi
  log "Tearing down only the processes and database container created by this runner."
  stop_port_process "$FRONTEND_PORT" "$FRONTEND_PID"
  stop_backend
  [[ "$STARTED_PG" == "1" ]] && docker rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
  return "$code"
}
trap cleanup EXIT

wait_for_http() {
  local url="$1" label="$2" attempts="${3:-180}" code
  for ((i = 1; i <= attempts; i++)); do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$url" 2>/dev/null || true)"
    if [[ -n "$code" && "$code" != "000" ]]; then
      log "$label is up (HTTP $code at $url)."
      return 0
    fi
    sleep 2
  done
  if [[ "$label" == "Backend" && -n "$CURRENT_BACKEND_LOG" && -f "$CURRENT_BACKEND_LOG" ]]; then
    log "Backend startup log (last 200 lines):"
    tail -200 "$CURRENT_BACKEND_LOG" >&2
  elif [[ "$label" == "Frontend" && -f "$RUN_DIR/frontend.log" ]]; then
    log "Frontend startup log (last 100 lines):"
    tail -100 "$RUN_DIR/frontend.log" >&2
  fi
  die "$label did not answer at $url."
}

case "$SELECTED_SUITE" in
  all|commercial-v2|core-commercial) ;;
  *) die "E2E_ENTERPRISE_SUITE must be all, commercial-v2, or core-commercial (got '$SELECTED_SUITE')." ;;
esac

log "Validating local tooling and exclusive ports."
for tool in docker dotnet node npx curl python3 lsof; do
  command -v "$tool" >/dev/null 2>&1 || die "Required tool not found: $tool"
done
docker info >/dev/null 2>&1 || die "Docker is installed but its engine is unavailable to this process."
[[ -d "$FRONTEND_DIR/node_modules" ]] || die "Frontend dependencies are absent. Run 'npm ci' in Frontend first."
mkdir -p "$RUN_DIR"

for pair in "backend:$BACKEND_PORT" "frontend:$FRONTEND_PORT" "postgres:$PG_PORT"; do
  service="${pair%%:*}"
  port="${pair##*:}"
  holder="$(listener_for "$port")"
  [[ -z "$holder" ]] || die "Port $port ($service) is already in use by PID(s) $holder."
done
if docker ps -a --format '{{.Names}}' | grep -qx "$PG_CONTAINER"; then
  die "Container $PG_CONTAINER already exists. Refusing to reuse credentials or data this runner did not create."
fi

PG_PASSWORD="$(python3 -c 'import secrets; print(secrets.token_urlsafe(24))')"
APP_SECRET="$(python3 -c 'import secrets; print(secrets.token_urlsafe(48))')"
PLATFORM_SECRET="$(python3 -c 'import secrets; print(secrets.token_urlsafe(48))')"
ACCEPTANCE_PASSWORD="$(python3 -c 'import secrets; print("E2e!" + secrets.token_urlsafe(18))')"
PROTECTION_KEY="$(python3 -c 'import base64,secrets; print(base64.b64encode(secrets.token_bytes(32)).decode())')"
INTEGRATION_SECRET="$(python3 -c 'import secrets; print(secrets.token_urlsafe(40))')"

log "Starting one disposable PostgreSQL container on port $PG_PORT."
docker run -d --name "$PG_CONTAINER" \
  -e POSTGRES_PASSWORD="$PG_PASSWORD" \
  -p "${PG_PORT}:5432" postgres:16-alpine >/dev/null
STARTED_PG=1

pg_ready=0
for ((i = 1; i <= 60; i++)); do
  if python3 - "$PG_PORT" <<'PY' >/dev/null 2>&1
import socket, sys
with socket.create_connection(("127.0.0.1", int(sys.argv[1])), timeout=2):
    pass
PY
  then
    if docker exec "$PG_CONTAINER" psql -U postgres -d postgres -c 'select 1' >/dev/null 2>&1; then
      pg_ready=1
      break
    fi
  fi
  sleep 2
done
[[ "$pg_ready" == "1" ]] || die "PostgreSQL did not become ready on port $PG_PORT."

if [[ "${E2E_SKIP_BUILD:-0}" != "1" ]]; then
  log "Building backend, deterministic acceptance fixture, and production frontend."
  dotnet build "$FIXTURE_PROJECT" --nologo -v minimal >"$RUN_DIR/dotnet-build.log" 2>&1 || {
    tail -80 "$RUN_DIR/dotnet-build.log" >&2
    die "Backend/fixture build failed."
  }
  (
    cd "$FRONTEND_DIR"
    npm run build
  ) >"$RUN_DIR/frontend-build.log" 2>&1 || {
    tail -120 "$RUN_DIR/frontend-build.log" >&2
    die "Frontend production build failed."
  }
else
  log "E2E_SKIP_BUILD=1 — using existing build outputs."
fi

start_frontend() {
  local code
  code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "$FRONTEND_URL" 2>/dev/null || true)"
  if [[ -n "$code" && "$code" != "000" ]]; then
    return 0
  fi
  log "Starting the frontend on $FRONTEND_URL."
  (
    cd "$FRONTEND_DIR"
    VITE_API_BASE_URL="$BACKEND_URL" npx vite --port "$FRONTEND_PORT" --strictPort --host 127.0.0.1
  ) >>"$RUN_DIR/frontend.log" 2>&1 &
  FRONTEND_PID=$!
  wait_for_http "$FRONTEND_URL" "Frontend" 90
}

start_frontend

start_backend() {
  local database="$1" storage_root="$2" phase="$3" apply_migrations="$4"
  local connection="Host=127.0.0.1;Port=${PG_PORT};Database=${database};Username=postgres;Password=${PG_PASSWORD}"
  CURRENT_BACKEND_LOG="$RUN_DIR/${phase}-backend.log"
  (
    cd "$(dirname "$BACKEND_PROJECT")"
    ConnectionStrings__DefaultConnection="$connection" \
    Database__ApplyMigrationsOnStartup="$apply_migrations" \
    Database__ValidateRequestPathOnStartup=true \
    Jwt__Key="$APP_SECRET" \
    Jwt__PlatformKey="$PLATFORM_SECRET" \
    Security__SecretProtectionKey="$PROTECTION_KEY" \
    Storage__RootPath="$storage_root" \
    CommercialFinance__ContactVerificationSecret="$APP_SECRET" \
    CommercialFinance__DunningProviderWebhookSecret="$APP_SECRET" \
    CommercialFinance__AuditActorSecret="$APP_SECRET" \
    Observability__Prometheus__ScrapeKey="$APP_SECRET" \
    ProcurementIntegration__Tenants__80101__SourceSystem="Disposable ERP" \
    ProcurementIntegration__Tenants__80101__SharedSecret="$INTEGRATION_SECRET" \
    Notifications__OutboundGuard__Mode=DraftOnly \
    ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS="$BACKEND_URL" \
    dotnet run --project "$BACKEND_PROJECT" --no-build --no-launch-profile
  ) >"$CURRENT_BACKEND_LOG" 2>&1 &
  BACKEND_PID=$!
  wait_for_http "$BACKEND_URL/health" "Backend" 210
}

load_fixture_environment() {
  local fixture_log="$1" line key value
  while IFS= read -r line; do
    [[ "$line" == *=* ]] || continue
    key="${line%%=*}"
    value="${line#*=}"
    [[ "$key" =~ ^E2E_[A-Z0-9_]+$ ]] || continue
    printf -v "$key" '%s' "$value"
    export "$key"
  done < "$fixture_log"

  export E2E_FIXTURE_MODE=false
  export E2E_BASE_URL="$FRONTEND_URL"
  export E2E_API_URL="$BACKEND_URL"
  export E2E_MANAGER_EMAIL=manager@release01c1.local
  export E2E_MANAGER_PASSWORD="$ACCEPTANCE_PASSWORD"
  export E2E_MANAGER_BUSINESS_UNIT_ID=80101
  export E2E_FINANCE_EMAIL=finance@release01c1.local
  export E2E_FINANCE_PASSWORD="$ACCEPTANCE_PASSWORD"
  export E2E_FINANCE_BUSINESS_UNIT_ID=80101
  export E2E_EDITOR_EMAIL=editor@release01c1.local
  export E2E_EDITOR_PASSWORD="$ACCEPTANCE_PASSWORD"
  export E2E_EDITOR_BUSINESS_UNIT_ID=80101
  export E2E_DENIED_EMAIL=denied@release01c1.local
  export E2E_DENIED_PASSWORD="$ACCEPTANCE_PASSWORD"
  export E2E_DENIED_BUSINESS_UNIT_ID=80101
  export E2E_OTHER_EMAIL=other@release01c1.local
  export E2E_OTHER_PASSWORD="$ACCEPTANCE_PASSWORD"
  export E2E_OTHER_BUSINESS_UNIT_ID=80102
  export E2E_PLATFORM_PASSWORD="$ACCEPTANCE_PASSWORD"
  export E2E_PROCUREMENT_INTEGRATION_SECRET="$INTEGRATION_SECRET"
  export E2E_SKIP_WEB_SERVER=true
}

enroll_disposable_platform_owner() {
  local suite_dir="$1" login_json token secret code
  login_json="$(curl -fsS -X POST "$BACKEND_URL/api/platform/auth/login" \
    -H 'Content-Type: application/json' \
    -d "{\"email\":\"$E2E_PLATFORM_EMAIL\",\"password\":\"$E2E_PLATFORM_PASSWORD\"}")"
  token="$(printf '%s' "$login_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')"
  secret="$(curl -fsS -X POST "$BACKEND_URL/api/platform/auth/mfa/enrollment" \
    -H "Authorization: Bearer $token" \
    | python3 -c 'import json,sys; print(json.load(sys.stdin)["secret"])')"
  code="$(python3 - "$secret" <<'PY'
import base64, hashlib, hmac, struct, sys, time
raw = sys.argv[1]
secret = base64.b32decode(raw + '=' * ((8 - len(raw) % 8) % 8))
counter = struct.pack('>Q', int(time.time()) // 30)
digest = hmac.new(secret, counter, hashlib.sha1).digest()
offset = digest[-1] & 15
print(f'{((struct.unpack(">I", digest[offset:offset + 4])[0] & 0x7fffffff) % 1000000):06d}')
PY
)"
  curl -fsS -o /dev/null -X POST "$BACKEND_URL/api/platform/auth/mfa/enrollment/confirm" \
    -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
    -d "{\"totpCode\":\"$code\"}"
  umask 077
  printf '%s' "$secret" >"$suite_dir/platform-owner-mfa-secret"
  chmod 600 "$suite_dir/platform-owner-mfa-secret"
  export E2E_PLATFORM_TOTP_SECRET="$secret"
  unset login_json token secret code
}

persist_kept_stack_environment() {
  local suite_dir="$1" name
  (
    umask 077
    {
      for name in $(compgen -e | grep -E '^E2E_'); do
        printf 'export %s=%q\n' "$name" "${!name}"
      done
      printf 'export E2E_PG_PASSWORD=%q\n' "$PG_PASSWORD"
    } >"$suite_dir/stack.env"
  )
  chmod 600 "$suite_dir/stack.env"
}

run_suite() {
  local suite="$1" database="$2" config="$3" expected_count="$4"
  local suite_dir="$RUN_DIR/$suite" storage_root="$RUN_DIR/$suite/storage"
  local connection fixture_log status discovered
  mkdir -p "$suite_dir" "$storage_root"
  connection="Host=127.0.0.1;Port=${PG_PORT};Database=${database};Username=postgres;Password=${PG_PASSWORD}"

  log "$suite: creating a fresh database and applying the full migration chain."
  docker exec "$PG_CONTAINER" psql -U postgres -d postgres -v ON_ERROR_STOP=1 \
    -c "CREATE DATABASE \"${database}\"" >"$suite_dir/database-create.log" 2>&1
  start_backend "$database" "$storage_root" "$suite-migrate" true
  stop_backend

  log "$suite: loading the deterministic commercial acceptance fixture."
  fixture_log="$suite_dir/fixture.log"
  NEXORA_ACCEPTANCE_CONNECTION="$connection" \
  NEXORA_ACCEPTANCE_PASSWORD="$ACCEPTANCE_PASSWORD" \
  NEXORA_ACCEPTANCE_SECRET_PROTECTION_KEY="$PROTECTION_KEY" \
  NEXORA_ACCEPTANCE_STORAGE_ROOT="$storage_root" \
  dotnet run --project "$FIXTURE_PROJECT" --no-build --no-launch-profile >"$fixture_log" 2>&1 || {
    tail -160 "$fixture_log" >&2
    die "$suite acceptance fixture failed."
  }
  load_fixture_environment "$fixture_log"

  start_backend "$database" "$storage_root" "$suite-run" false

  if [[ "$suite" == "commercial-v2" ]]; then
    log "$suite: enrolling the disposable Platform Owner in mandatory MFA."
    enroll_disposable_platform_owner "$suite_dir"
  fi
  if [[ "${E2E_KEEP_STACK:-0}" == "1" ]]; then
    # Persist BEFORE the suite runs: a kept stack whose smoke test failed is exactly the stack
    # someone wants to poke at, and the generated secrets would otherwise die with this script.
    persist_kept_stack_environment "$suite_dir"
    log "$suite: E2E_KEEP_STACK=1 — fixture environment written to $suite_dir/stack.env."
  fi

  log "$suite: verifying test discovery ($expected_count expected) before execution."
  set +e
  (
    cd "$FRONTEND_DIR"
    E2E_FULL_ACCEPTANCE=false npx playwright test --config "$config" --list
  ) >"$suite_dir/discovery.log" 2>&1
  status=$?
  set -e
  [[ "$status" == "0" ]] || {
    tail -120 "$suite_dir/discovery.log" >&2
    die "$suite test discovery failed."
  }
  discovered="$(sed -nE 's/^Total: ([0-9]+) tests?.*/\1/p' "$suite_dir/discovery.log" | tail -1)"
  [[ "$discovered" == "$expected_count" ]] || {
    tail -80 "$suite_dir/discovery.log" >&2
    die "$suite discovered ${discovered:-an unknown number of} tests; expected $expected_count."
  }

  # The backend is deliberately restarted after migrations so the fixture is loaded into a clean
  # application process. On some shells that process handoff can also reap the sibling Vite
  # wrapper even though the frontend listener is on a different port. Re-probe here and recover
  # the disposable frontend before opening a browser; a 273 ms connection-refused result is runner
  # failure, not product evidence.
  start_frontend

  local -a playwright_args=(test --config "$config" --workers=1 --retries=0)
  if [[ -n "${E2E_TEST_GREP:-}" ]]; then
    playwright_args+=(--grep "$E2E_TEST_GREP")
    log "$suite: running the focused real-browser selection '$E2E_TEST_GREP' against its isolated database."
  else
    log "$suite: running $expected_count real-browser tests against its isolated database."
  fi
  set +e
  (
    cd "$FRONTEND_DIR"
    if [[ "$suite" == "commercial-v2" ]]; then
      if [[ -n "${E2E_TEST_GREP:-}" ]]; then
        E2E_FULL_ACCEPTANCE=false npx playwright "${playwright_args[@]}"
      else
        E2E_FULL_ACCEPTANCE=true npx playwright "${playwright_args[@]}"
      fi
    else
      E2E_FULL_ACCEPTANCE=false npx playwright "${playwright_args[@]}"
    fi
  ) 2>&1 | tee "$suite_dir/playwright.log"
  status=${PIPESTATUS[0]}
  set -e

  if [[ "$status" != "0" ]]; then
    log "$suite backend error summary:"
    grep -nE 'fail:|Unhandled|Exception|PostgresException|DbUpdateException|permission denied|violates' \
      "$CURRENT_BACKEND_LOG" | tail -160 >&2 || true
    die "$suite failed (exit $status). Artifacts: $suite_dir and $FRONTEND_DIR/test-results."
  fi
  if [[ "${E2E_KEEP_STACK:-0}" == "1" ]]; then
    # A kept stack is only useful if the backend is still answering.
    log "$suite: E2E_KEEP_STACK=1 — backend left running on $BACKEND_URL; environment in $suite_dir/stack.env."
  else
    stop_backend
  fi
  if [[ -n "${E2E_TEST_GREP:-}" ]]; then
    log "$suite focused selection PASSED: '$E2E_TEST_GREP'."
  else
    log "$suite PASSED: $expected_count tests, no skips."
  fi
}

if [[ "$SELECTED_SUITE" == "all" || "$SELECTED_SUITE" == "commercial-v2" ]]; then
  run_suite commercial-v2 nexora_commercial_v2 playwright.commercial-journey-v2.config.ts 41
fi
if [[ "$SELECTED_SUITE" == "all" || "$SELECTED_SUITE" == "core-commercial" ]]; then
  run_suite core-commercial nexora_core_commercial playwright.core-commercial.config.ts 40
fi

log "ENTERPRISE COMMERCIAL JOURNEY PASSED ($SELECTED_SUITE). Logs: $RUN_DIR"
