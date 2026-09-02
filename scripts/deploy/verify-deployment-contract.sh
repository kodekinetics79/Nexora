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

# Last verified layout of existing Render service srv-d9csjhe1a83c739phue0. Parse YAML rather
# than accepting individual matching lines: indentation can move a valid-looking key onto the
# wrong service, a duplicate env key can override the safe value, and greps proved neither the
# persistent-volume contract nor the separate migration credential.
command -v ruby >/dev/null 2>&1 || {
    printf 'deployment contract: Ruby is required to validate render.yaml structurally.\n' >&2
    exit 1
}
ruby - "$render" <<'RUBY'
require "yaml"

path = ARGV.fetch(0)
document = YAML.safe_load(
  File.read(path),
  permitted_classes: [],
  permitted_symbols: [],
  aliases: false
)
services = document.fetch("services")
nexora = services.select { |service| service["name"] == "Nexora" }
raise "Render contract must contain exactly one Nexora service" unless nexora.length == 1
service = nexora.first

expected_service = {
  "type" => "web",
  "runtime" => "docker",
  "branch" => "main",
  "dockerContext" => "Backend",
  "dockerfilePath" => "Backend/Dockerfile",
  "autoDeployTrigger" => "checksPass",
  "healthCheckPath" => "/health"
}
expected_service.each do |key, value|
  raise "Render Nexora #{key} must be #{value.inspect}" unless service[key] == value
end
raise "Render rootDir would change the repository-root service layout" if service.key?("rootDir")

disk = service.fetch("disk")
expected_disk = {
  "name" => "nexora-evidence",
  "mountPath" => "/var/data",
  "sizeGB" => 5
}
expected_disk.each do |key, value|
  raise "Render evidence disk #{key} must be #{value.inspect}" unless disk[key] == value
end

env_rows = service.fetch("envVars")
keys = env_rows.map { |row| row.fetch("key") }
duplicates = keys.group_by { |key| key }.select { |_key, values| values.length > 1 }.keys
raise "Render envVars contain duplicate keys: #{duplicates.join(', ')}" unless duplicates.empty?
env = env_rows.to_h { |row| [row.fetch("key"), row] }

# Reconciled 2026-09-02 against what production runs (render.yaml header). The disk block above
# is still asserted because the disk is still attached; when rollout step 3 of
# docs/design/evidence-object-store-cutover.md removes it, this contract changes with it.
expected_values = {
  "Storage__RootPath" => "/var/data/nexora/uploads",
  "Storage__RequiredMountPath" => "/var/data",
  "Storage__EnforcePersistentMount" => "true",
  # S3 on Backblaze B2. "Local" would silently move evidence back onto the disk, and the
  # bucket name is case-sensitive and embedded in every stored URI: NexoraBucket, verbatim.
  "EvidenceStorage__Provider" => "S3",
  "EvidenceStorage__ServiceUrl" => "https://s3.us-east-005.backblazeb2.com",
  "EvidenceStorage__Region" => "us-east-005",
  "EvidenceStorage__Bucket" => "NexoraBucket",
  # Real malware scanning. BuiltIn is a structural inspector plus the EICAR signature.
  "DocumentInspection__Scanner__Provider" => "ClamAV",
  "DocumentInspection__ClamAV__Port" => "3310",
  # Stream 4 switches, declared at their safe defaults; flipping them is a rollout step that
  # is made in the dashboard, not by editing the contract.
  "EvidenceStorage__RouteLegacyWritersToObjectStore" => "false",
  "EvidenceStorage__LegacyMigration__Enabled" => "false",
  "Auth__RequireSecurityStamp" => "false",
  "Database__ApplyMigrationsOnStartup" => "true",
  "Database__AllowManagedOwnerRoleMigrationCompatibility" => "true"
}
expected_values.each do |key, value|
  raise "Render #{key} must be #{value.inspect}" unless env.fetch(key)["value"] == value
end

# The scanner host is the private service's internal address, never a literal.
clamav_host = env.fetch("DocumentInspection__ClamAV__Host")
raise "Render DocumentInspection__ClamAV__Host must carry no literal value" if clamav_host.key?("value")
from_service = clamav_host.fetch("fromService")
raise "Render DocumentInspection__ClamAV__Host must reference the nexora-clamav private service host" unless
  from_service == { "name" => "nexora-clamav", "type" => "pserv", "property" => "host" }

clamav = services.select { |service| service["name"] == "nexora-clamav" }
raise "Render contract must contain exactly one nexora-clamav private service" unless clamav.length == 1
clamav = clamav.first
raise "nexora-clamav must be a pserv" unless clamav["type"] == "pserv"
raise "nexora-clamav must run the pinned clamav image" unless clamav.dig("image", "url") == "docker.io/clamav/clamav:1.5"
clamav_env = clamav.fetch("envVars").to_h { |row| [row.fetch("key"), row["value"]] }
raise "nexora-clamav must bind PORT 3310" unless clamav_env["PORT"] == "3310"
raise "nexora-clamav StreamMaxLength must exceed the 25 MB upload cap" unless clamav_env["CLAMD_CONF_StreamMaxLength"] == "64M"

# Every credential is dashboard-managed: sync:false and no repository value. The explicit list
# is the contract; the name-pattern sweep catches a credential added under a new name.
secrets = %w[
  ConnectionStrings__DefaultConnection ConnectionStrings__MigrationConnection
  EvidenceStorage__AccessKeyId EvidenceStorage__SecretAccessKey
  Jwt__Key Jwt__PlatformKey Security__SecretProtectionKey
  Platform__BootstrapOwnerEmail Platform__BootstrapOwnerPassword
  CommercialFinance__DunningProviderWebhookSecret CommercialFinance__ContactVerificationSecret
  CommercialFinance__AuditActorSecret Ollama__ApiKey
]
secrets.each do |key|
  row = env.fetch(key)
  raise "Render #{key} must remain a dashboard-managed secret" unless row["sync"] == false
  raise "Render #{key} must not carry a repository value" if row.key?("value")
end
env.each do |key, row|
  next unless key =~ /(Key|Secret|Password|Connection|Token)$/i || key =~ /__(AccessKeyId|SecretAccessKey|ApiKey)$/
  raise "Render #{key} looks like a credential and must be sync:false with no value" if row.key?("value") || row["sync"] != false
end
RUBY

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
