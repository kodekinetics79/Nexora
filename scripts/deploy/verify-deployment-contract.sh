#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
render="$root/render.yaml"
dockerfile="$root/Backend/Dockerfile"
compose="$root/deploy/single-box/docker-compose.yml"
example="$root/deploy/single-box/.env.example"

require_line() {
    file=$1
    pattern=$2
    message=$3
    if ! grep -Eq "$pattern" "$file"; then
        printf 'deployment contract: %s\n' "$message" >&2
        exit 1
    fi
}

reject_line() {
    file=$1
    pattern=$2
    message=$3
    if grep -Eiq "$pattern" "$file"; then
        printf 'deployment contract: %s\n' "$message" >&2
        exit 1
    fi
}

# Last verified layout of existing Render service srv-d9csjhe1a83c739phue0.
require_line "$render" '^    name: Nexora$' 'Render service name drifted from the existing service.'
require_line "$render" '^    branch: main$' 'Render must deploy only main.'
require_line "$render" '^    dockerContext: Backend$' 'Render Docker context must remain Backend.'
require_line "$render" '^    dockerfilePath: Backend/Dockerfile$' 'Render Dockerfile must be repository-relative.'
require_line "$render" '^    autoDeployTrigger: checksPass$' 'Render must wait for required checks.'
require_line "$render" '^    healthCheckPath: /health$' 'Render liveness must use /health.'
reject_line "$render" '^    rootDir:' 'Render rootDir would change the existing repository-root service layout.'

# Compose may only call a probe client the runtime image explicitly installs.
require_line "$compose" 'wget -qO- http://127\.0\.0\.1:8080/health' 'Compose backend health probe changed unexpectedly.'
require_line "$dockerfile" 'apt-get install -y --no-install-recommends wget ' 'The backend image must install its wget health-probe dependency.'

# A distributable template must never carry an operator's or customer's real identity.
require_line "$example" '^Notifications__Smtp__Username=smtp-user@example\.com$' 'SMTP username must remain a non-routable example identity.'
require_line "$example" '^Notifications__FromAddress=noreply@example\.com$' 'From address must remain a non-routable example identity.'
require_line "$example" 'User buyer@example\.com$' 'Inbound mailbox documentation must use an example identity.'
reject_line "$example" '(kodekinetics|naspakinc|secureserver)' 'A real organization/provider identity leaked into the distributable example.'

# Both supported Vercel project-root layouts must publish the same browser boundary. Parse JSON
# instead of grepping it so malformed config and subtly different policy values both fail CI.
python3 - "$root/vercel.json" "$root/Frontend/vercel.json" <<'PY'
import json
import sys

paths = sys.argv[1:]
configs = []
for path in paths:
    with open(path, encoding="utf-8") as stream:
        configs.append(json.load(stream))

def policy(config):
    rules = config.get("headers", [])
    assert len(rules) == 1 and rules[0].get("source") == "/(.*)", \
        "security headers must cover every SPA route"
    return {header["key"]: header["value"] for header in rules[0].get("headers", [])}

root, frontend = map(policy, configs)
assert root == frontend, "root and Frontend Vercel security policies drifted"
required = {
    "Content-Security-Policy",
    "X-Content-Type-Options",
    "X-Frame-Options",
    "Referrer-Policy",
    "Permissions-Policy",
}
assert required <= root.keys(), "a required static-frontend security header is missing"
csp = root["Content-Security-Policy"]
for directive in (
    "default-src 'self'",
    "object-src 'none'",
    "frame-ancestors 'none'",
    "script-src 'self'",
    "connect-src 'self' https://nexora-fyjw.onrender.com",
):
    assert directive in csp, f"CSP is missing {directive}"
assert root["X-Content-Type-Options"] == "nosniff"
assert root["X-Frame-Options"] == "DENY"
assert "geolocation=(self)" in root["Permissions-Policy"], \
    "delivery confirmation uses same-origin geolocation"
assert "camera=()" in root["Permissions-Policy"] and "microphone=()" in root["Permissions-Policy"]
PY

printf 'deployment contract: ok\n'
