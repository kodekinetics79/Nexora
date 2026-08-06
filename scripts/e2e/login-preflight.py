#!/usr/bin/env python3
"""Real POST /api/Auth/login against the running E2E backend.

Catches the failure the health check cannot: a backend that starts, answers /health, and still
cannot authenticate a single request — for example because a stale process from an earlier run
holds the port with the previous run's database credentials, which surfaces only as 28P01 on the
first login.

Exits 0 only on HTTP 200 carrying a JWT. On failure it prints the stage, the context, the
configuration key, sanitized host/database/username and the PostgreSQL SQLSTATE if one is
present. It never prints the password or a connection string.

    login-preflight.py <api-url> <email> <password> <pg-port>
"""
import json
import re
import sys
import urllib.error
import urllib.request


def main() -> int:
    if len(sys.argv) != 5:
        print("usage: login-preflight.py <api-url> <email> <password> <pg-port>", file=sys.stderr)
        return 2
    api_url, email, password, pg_port = sys.argv[1:5]

    payload = json.dumps({"email": email, "password": password}).encode()
    request = urllib.request.Request(
        f"{api_url}/api/Auth/login",
        data=payload,
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    status, body = 0, ""
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            status, body = response.status, response.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        status, body = error.code, error.read().decode("utf-8", "replace")
    except Exception as error:                      # connection refused, DNS, timeout
        status, body = 0, f"{type(error).__name__}: {error}"

    if status == 200:
        try:
            if json.loads(body).get("token"):
                print("Login preflight OK (HTTP 200, JWT issued).")
                return 0
        except json.JSONDecodeError:
            pass
        fail(status, body, pg_port, "HTTP 200 but the response carried no JWT.")
        return 1

    fail(status, body, pg_port, "Login did not return 200.")
    return 1


def fail(status: int, body: str, pg_port: str, headline: str) -> None:
    sqlstate = re.search(r"\b(\d{2}[A-Z0-9]{3})\b", body)
    # The body can echo a driver error; never let a connection string through it.
    safe_body = re.sub(r"(?i)password=[^;\"'\s]*", "password=<redacted>", body)[:400]
    print(f"\n\033[31m[e2e] Login preflight FAILED — {headline}\033[0m", file=sys.stderr)
    print("  stage      : login preflight", file=sys.stderr)
    print("  context    : ErpRfqAutomationContext (request path)", file=sys.stderr)
    print("  config key : ConnectionStrings:DefaultConnection "
          "(env ConnectionStrings__DefaultConnection)", file=sys.stderr)
    print(f"  host/db    : 127.0.0.1:{pg_port} / nexora_e2e / postgres", file=sys.stderr)
    print(f"  http       : {status}", file=sys.stderr)
    print(f"  sqlstate   : {sqlstate.group(1) if sqlstate else 'n/a'}", file=sys.stderr)
    print(f"  response   : {safe_body}", file=sys.stderr)


if __name__ == "__main__":
    sys.exit(main())
