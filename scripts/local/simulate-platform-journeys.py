#!/usr/bin/env python3
"""Drive the Platform Admin control plane through real journeys and report what breaks.

WHY THIS EXISTS. Every environment here carries demo data and the point of running the
product is to surface defects before launch. A green unit-test suite has repeatedly failed
to see the defects that matter on this codebase — column-level grant violations, scaffolded
column-name mismatches, controls that fail open — because those only exist once real
PostgreSQL roles, real HTTP routing and real middleware are in the path. This exercises the
running stack instead of the model.

WHAT IT JUDGES. Not "did it return 200". Each probe declares the status it EXPECTS, so a
refusal that is supposed to happen counts as a pass and a 500 counts as a defect wherever it
appears. Anything unexpected is reported with the response body, because a swallowed error
that returns a plausible-looking payload is the failure mode this codebase actually has.

    ./scripts/local/simulate-platform-journeys.py            # against the local stack
    ./scripts/local/simulate-platform-journeys.py --api URL --email E --password P
"""

import argparse
import base64
import hashlib
import hmac
import json
from pathlib import Path
import struct
import sys
import time
import urllib.error
import urllib.request
import uuid

DEFECTS: list[dict] = []
PASSES: list[str] = []


def call(method, url, token=None, body=None, expect=(200, 201, 202, 204)):
    """One HTTP probe. Returns (status, parsed-body-or-text)."""
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(url, data=data, method=method)
    request.add_header("Content-Type", "application/json")
    if token:
        request.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            status, raw = response.status, response.read().decode(errors="replace")
    except urllib.error.HTTPError as error:
        status, raw = error.code, error.read().decode(errors="replace")
    except Exception as error:                      # noqa: BLE001 - a transport failure is a defect too
        return 0, str(error)
    try:
        parsed = json.loads(raw) if raw.strip() else None
    except json.JSONDecodeError:
        parsed = raw
    return status, parsed


def probe(name, method, url, token=None, body=None, expect=(200, 201, 202, 204), severity="high"):
    status, payload = call(method, url, token, body, expect)
    wanted = expect if isinstance(expect, tuple) else (expect,)
    if status in wanted:
        PASSES.append(name)
        return status, payload
    DEFECTS.append({
        "probe": name,
        "severity": "critical" if status in (0, 500) else severity,
        "expected": list(wanted),
        "actual": status,
        "url": url,
        "response": payload if isinstance(payload, (dict, list)) else str(payload)[:400],
    })
    return status, payload


def tenant_body(slug, **overrides):
    """A fully-specified, valid tenant. Overrides express each scenario as a delta."""
    body = {
        "name": f"Sim {slug}",
        "slug": slug,
        "legalName": f"Sim {slug} Trading Company LLC",
        "registrationNumber": "1010" + slug[-6:].rjust(6, "0")[:6],
        "taxNumber": "300" + slug[-9:].rjust(9, "0")[:9] + "00003",
        "countryCode": "SA",
        "industry": "Industrial supply",
        "addressLine1": "King Fahd Road",
        "city": "Riyadh",
        "postalCode": "12345",
        "phone": "+966 11 000 0000",
        "contactEmail": f"info@{slug}.example",
        "baseCurrencyCode": "SAR",
        "timeZoneId": "Asia/Riyadh",
        "locale": "en-GB",
        "dataRegion": "me-central-1",
        "billingMode": "Billable",
        "paymentTermsDays": 30,
        "billingContactName": "Finance",
        "billingContactEmail": f"ap@{slug}.example",
        "accountOwnerEmail": "owner@nexora.local",
        "adminFirstName": "Sim",
        "adminLastName": "Admin",
        "adminEmail": f"admin@{slug}.example",
        "adminJobTitle": "Operations Director",
    }
    body.update(overrides)
    return body


def totp(secret: str, now: float) -> str:
    normalized = secret.strip().upper()
    key = base64.b32decode(normalized + "=" * ((8 - len(normalized) % 8) % 8))
    digest = hmac.new(key, struct.pack(">Q", int(now) // 30), hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    value = (struct.unpack(">I", digest[offset:offset + 4])[0] & 0x7FFFFFFF) % 1_000_000
    return f"{value:06d}"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--api", default="http://127.0.0.1:5192")
    parser.add_argument("--email", default="owner@nexora.local")
    parser.add_argument("--password", default="LocalOwner!2026")
    parser.add_argument("--totp-secret-file", default=".local-run/platform-owner-mfa-secret")
    args = parser.parse_args()
    api = args.api.rstrip("/")

    status, login = call("POST", f"{api}/api/platform/auth/login",
                         body={"email": args.email, "password": args.password})
    if status == 200 and isinstance(login, dict) and login.get("mfaRequired"):
        secret_path = Path(args.totp_secret_file)
        if not secret_path.is_file():
            print(f"FATAL: MFA is required and the local seed file is absent: {secret_path}")
            return 2
        # A watched browser may just have consumed this step. Wait for a genuinely new step;
        # never weaken the server replay fence and never print the seed.
        time.sleep(30 - (time.time() % 30) + 0.5)
        status, login = call("POST", f"{api}/api/platform/auth/mfa/challenge", body={
            "challengeId": login.get("mfaChallengeId"),
            "totpCode": totp(secret_path.read_text(encoding="utf-8"), time.time()),
        })
    if status != 200 or not isinstance(login, dict) or "token" not in login:
        print(f"FATAL: platform login failed ({status}): {login}")
        return 2
    token = login["token"]

    _, plans = probe("catalogue: list plans", "GET", f"{api}/api/platform/plans", token)
    _, cards = probe("catalogue: list rate cards", "GET", f"{api}/api/platform/billing/rate-cards", token)
    plan_id = next((p["id"] for p in (plans or []) if p.get("code") == "growth"), None)
    card_id = (cards or [{}])[0].get("id")
    if plan_id is None:
        print("FATAL: no plans seeded — run scripts/local/run-platform-console.sh first")
        return 2

    run = uuid.uuid4().hex[:6]

    # ---- 1. the refusals that protect revenue and identity ----------------------------------
    # Each of these SHOULD fail. A 201 here is the defect; so is a 500.
    refusals = [
        ("billable tenant with no plan", tenant_body(f"sim-noplan-{run}"), 400),
        ("non-billable with no reason",
         tenant_body(f"sim-noreason-{run}", billingMode="Internal"), 400),
        ("non-billable with a one-word reason",
         tenant_body(f"sim-thinreason-{run}", billingMode="Internal", billingModeReason="x"), 400),
        ("open-ended trial",
         tenant_body(f"sim-opentrial-{run}", billingMode="Trial",
                     billingModeReason="Evaluation agreed with the buyer"), 400),
        ("trial that expired before it began",
         tenant_body(f"sim-pasttrial-{run}", billingMode="Trial",
                     billingModeReason="Evaluation agreed with the buyer",
                     trialEndsOn="2020-01-01T00:00:00Z"), 400),
        ("reserved slug 'admin'", tenant_body("admin", planId=plan_id), 400),
        ("reserved slug 'api'", tenant_body("api", planId=plan_id), 400),
        ("vendor-impersonating slug", tenant_body(f"nexora-support-{run}", planId=plan_id), 400),
        ("all-digit slug", tenant_body("12345678", planId=plan_id), 400),
        ("slug longer than BusinessUnitCode allows",
         tenant_body("s" + "u" * 58, planId=plan_id), 400),
        ("malformed country code", tenant_body(f"sim-badcc-{run}", planId=plan_id, countryCode="SAU"), 400),
        ("malformed currency code",
         tenant_body(f"sim-badccy-{run}", planId=plan_id, baseCurrencyCode="RIYAL"), 400),
        ("missing base currency",
         tenant_body(f"sim-nocur-{run}", planId=plan_id, baseCurrencyCode=None), 400),
        ("unknown time zone", tenant_body(f"sim-badtz-{run}", planId=plan_id, timeZoneId="Asia/Riyad"), 400),
        ("password supplied on the invite path",
         tenant_body(f"sim-pwinvite-{run}", planId=plan_id, adminActivation="invite",
                     adminPassword="ShouldNotBeAccepted1!"), 400),
        ("plan that does not exist", tenant_body(f"sim-ghostplan-{run}", planId=999_999), 400),
        ("rate card that does not exist",
         tenant_body(f"sim-ghostcard-{run}", planId=plan_id, rateCardId=999_999), 400),
    ]
    for label, body, expected in refusals:
        probe(f"refusal: {label}", "POST", f"{api}/api/platform/tenants", token, body, expect=expected)

    # ---- 2. the configurations that must succeed --------------------------------------------
    created = {}
    accepted = [
        ("billable + pinned card", f"sim-billable-{run}",
         {"planId": plan_id, "rateCardId": card_id}),
        ("bounded trial", f"sim-trial-{run}",
         {"planId": plan_id, "billingMode": "Trial",
          "billingModeReason": "Thirty-day evaluation agreed with the buyer",
          "trialEndsOn": "2026-12-31T00:00:00Z"}),
        ("internal workspace", f"sim-internal-{run}",
         {"billingMode": "Internal",
          "billingModeReason": "Internal support workspace, never invoiced"}),
        ("operator-set password", f"sim-password-{run}",
         {"planId": plan_id, "adminActivation": "password",
          "adminPassword": "Operator-Chose-This-2026!"}),
        ("minimal required fields only", f"sim-minimal-{run}", None),
    ]
    for label, slug, extra in accepted:
        body = tenant_body(slug, planId=plan_id) if extra is None else tenant_body(slug, **extra)
        status, payload = probe(f"provision: {label}", "POST", f"{api}/api/platform/tenants",
                                token, body, expect=201)
        if status == 201 and isinstance(payload, dict):
            created[slug] = payload
            baseline = payload.get("baseline") or {}
            # A tenant that provisions but lands empty is the defect this programme exists to
            # close, so the workspace is asserted rather than assumed.
            missing = [k for k, v in {
                "quote template": baseline.get("quoteConfiguration"),
                "base currency": baseline.get("baseCurrency"),
                "units of measure": (baseline.get("unitsOfMeasure") or 0) > 0,
                "starter roles": (baseline.get("roles") or 0) > 0,
                "permission grants": (baseline.get("permissionGrants") or 0) > 0,
            }.items() if not v]
            if missing:
                DEFECTS.append({"probe": f"workspace: {label}", "severity": "critical",
                                "expected": ["a usable workspace"], "actual": "starved",
                                "url": slug, "response": f"missing: {', '.join(missing)}"})
            else:
                PASSES.append(f"workspace: {label}")

    # duplicate slug + duplicate admin email must both be named conflicts, not 500s
    if f"sim-billable-{run}" in created:
        probe("refusal: duplicate slug", "POST", f"{api}/api/platform/tenants", token,
              tenant_body(f"sim-billable-{run}", planId=plan_id,
                          adminEmail=f"other@sim-billable-{run}.example"), expect=409)
        probe("refusal: duplicate admin email", "POST", f"{api}/api/platform/tenants", token,
              tenant_body(f"sim-dupemail-{run}", planId=plan_id,
                          adminEmail=f"admin@sim-billable-{run}.example"), expect=409)

    # ---- 3. the customer's half of the journey ----------------------------------------------
    invited = created.get(f"sim-billable-{run}", {}).get("foundingAdmin", {}).get("invitation")
    if invited and invited.get("activationUrl"):
        activation_token = invited["activationUrl"].rsplit("/", 1)[-1]
        probe("activation: preview an unknown token", "GET",
              f"{api}/api/tenant-activation/{'z' * 43}", expect=(404, 410, 409, 403))
        probe("activation: preview a live token", "GET",
              f"{api}/api/tenant-activation/{activation_token}", expect=200)
        probe("activation: password containing the email local part", "POST",
              f"{api}/api/tenant-activation/{activation_token}",
              body={"password": f"admin-sim-billable-{run}-A1!"}, expect=400)
        probe("activation: redeem", "POST", f"{api}/api/tenant-activation/{activation_token}",
              body={"password": "Riyadh-Steel-Trading-2026!"}, expect=200)
        probe("activation: the admin can now sign in", "POST", f"{api}/api/Auth/Login",
              body={"email": f"admin@sim-billable-{run}.example",
                    "password": "Riyadh-Steel-Trading-2026!"}, expect=200)
        probe("activation: a spent link is refused", "POST",
              f"{api}/api/tenant-activation/{activation_token}",
              body={"password": "Riyadh-Steel-Trading-2026!"}, expect=(409, 410, 400))
    else:
        DEFECTS.append({"probe": "activation: invitation issued", "severity": "critical",
                        "expected": ["an activation link"], "actual": "none returned",
                        "url": "provision response", "response": str(invited)[:300]})

    # ---- 4. operator control over a live tenant ---------------------------------------------
    subject = created.get(f"sim-minimal-{run}", {}).get("tenant", {}).get("id")
    if subject:
        base = f"{api}/api/platform/tenants/{subject}"
        probe("lifecycle: suspend requires a reason", "POST", f"{base}/suspend", token, {}, expect=400)
        probe("lifecycle: suspend", "POST", f"{base}/suspend", token,
              {"reason": "Simulation: non-payment"}, expect=200)
        probe("lifecycle: resume", "POST", f"{base}/resume", token,
              {"reason": "Simulation: payment received"}, expect=200)
        probe("lifecycle: archive from Active is refused", "POST", f"{base}/archive", token,
              {"reason": "Simulation"}, expect=409)
        probe("ops: tenant operations summary", "GET", f"{base}/operations-summary", token)
        probe("ops: offboarding status", "GET", f"{base}/offboarding", token)
        probe("ops: purge with no scheduled deletion is refused", "POST",
              f"{base}/offboarding/purge", token,
              {"reason": "Simulation", "confirmation": "wrong"}, expect=(400, 409))
        probe("ops: AI policy readable", "GET", f"{base}/ai-policy", token)
        probe("billing: tenant billing profile", "GET",
              f"{api}/api/platform/billing/tenants/{subject}", token)
        probe("support: raise a ticket", "POST", f"{api}/api/platform/support/tickets", token,
              {"tenantId": int(subject), "subject": "Simulation probe",
               "body": "Raised by the journey simulation.", "severity": "Normal"}, expect=(200, 201))
        probe("audit: tenant timeline", "GET",
              f"{api}/api/platform/audit/tenants/{subject}/timeline", token)

    # ---- 5. the fleet-wide operator surfaces ------------------------------------------------
    probe("board: revenue risk", "GET", f"{api}/api/platform/billing/revenue-risk", token)
    probe("board: commercial configuration required", "GET",
          f"{api}/api/platform/billing/revenue-risk?onlyCommercialConfigurationRequired=true", token)
    probe("board: pending deletions", "GET", f"{api}/api/platform/tenants/offboarding/pending", token)
    probe("board: support queue", "GET", f"{api}/api/platform/support/tickets", token)
    probe("board: audit query", "GET", f"{api}/api/platform/audit/query?pageSize=25", token)
    probe("board: audit action vocabulary", "GET", f"{api}/api/platform/audit/actions", token)
    probe("board: provisioning executions", "GET",
          f"{api}/api/platform/provisioning/executions", token)
    probe("board: reserved slugs", "GET", f"{api}/api/platform/provisioning/reserved-slugs", token)
    probe("board: slug availability", "GET",
          f"{api}/api/platform/provisioning/slug-check?slug=admin", token)
    probe("board: tenant list", "GET", f"{api}/api/platform/tenants", token)

    # ---- 6. authorization: the control plane must refuse anonymous callers -------------------
    for label, path in [
        ("tenant list", "/api/platform/tenants"),
        ("revenue risk", "/api/platform/billing/revenue-risk"),
        ("support queue", "/api/platform/support/tickets"),
        ("audit query", "/api/platform/audit/query"),
        ("provisioning executions", "/api/platform/provisioning/executions"),
    ]:
        probe(f"authz: anonymous is refused on {label}", "GET", f"{api}{path}", expect=(401, 403))

    # ---- report -----------------------------------------------------------------------------
    print(f"\n{'=' * 74}\n  PLATFORM JOURNEY SIMULATION — run {run}\n{'=' * 74}")
    print(f"  probes passed : {len(PASSES)}")
    print(f"  defects       : {len(DEFECTS)}")
    if DEFECTS:
        by_severity = {"critical": [], "high": [], "medium": []}
        for defect in DEFECTS:
            by_severity.setdefault(defect["severity"], []).append(defect)
        for severity in ("critical", "high", "medium"):
            for defect in by_severity.get(severity, []):
                print(f"\n  [{severity.upper()}] {defect['probe']}")
                print(f"     expected {defect['expected']}, got {defect['actual']}")
                print(f"     {defect['url']}")
                body = defect["response"]
                print(f"     {json.dumps(body)[:300] if isinstance(body, (dict, list)) else body}")
    else:
        print("\n  No defects. Every refusal refused and every journey completed.")
    print()
    return 1 if any(d["severity"] == "critical" for d in DEFECTS) else 0


if __name__ == "__main__":
    sys.exit(main())
