#!/usr/bin/env python3
"""Seed a tenant's governed-artifact registries with real, content-bearing versions.

WHY THIS EXISTS. Taxonomy & Document Skills, Integration Hub and Model & Rule Lifecycle each
open on "No governed artifacts yet", and the only way in is a dialog asking a tenant
administrator to hand-author a Definition JSON blob. The scaffold that dialog pre-fills is
structurally valid and semantically empty — `fieldGroups: []`, `actions: []`, `lineSchema: []`
— so the fastest honest path from an empty registry to something an operator can read,
version, promote and roll back is to write the first versions from here.

WHAT THESE ARTIFACTS DO. Read this before assuming a seeded taxonomy changes extraction.
Of the ten governed artifact types, exactly ONE is consumed by running code:
QualityMetricSet, which QualityAnalyticsService reads for its thresholds (see
`ThresholdsAsync`). CommercialTaxonomy, DocumentSkill, Model, Rule, Dataset, Connector,
TestSuite, ReleaseCandidate and ArchivePolicy are stored, versioned, audited and promoted,
and nothing in the pipeline reads them back. They are a governance RECORD — the tenant's
declared position on what it reads, what it calls and what it connects to, with an approval
trail over each change. Seeding one documents a decision; it does not configure a behaviour.
The definitions below are therefore written to describe what this deployment ACTUALLY does,
because a record that disagrees with the system is worse than an empty registry.

    ./scripts/local/seed-governed-artifacts.py --api URL --email E --password P
    ./scripts/local/seed-governed-artifacts.py --dry-run     # print the definitions, call nothing
    ./scripts/local/seed-governed-artifacts.py --promote     # draft -> test -> production

Idempotent: an artifact key that already exists is reported and skipped, never overwritten.
"""

import argparse
import json
import os
from pathlib import Path
import sys
import urllib.error
import urllib.request
import uuid

RESULTS: list[tuple[str, str]] = []


def call(method, url, token=None, body=None):
    """One HTTP call. Returns (status, parsed-body-or-text)."""
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(url, data=data, method=method)
    request.add_header("Content-Type", "application/json")
    # Every governed write is idempotency-keyed server side; a fresh key per attempt is
    # correct here because a retry of this script is a new intent, not a replay.
    request.add_header("Idempotency-Key", str(uuid.uuid4()))
    if token:
        request.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            status, raw = response.status, response.read().decode(errors="replace")
    except urllib.error.HTTPError as error:
        status, raw = error.code, error.read().decode(errors="replace")
    except Exception as error:                      # noqa: BLE001 - transport failure is a result too
        return 0, str(error)
    try:
        return status, json.loads(raw) if raw else None
    except json.JSONDecodeError:
        return status, raw


# --- the definitions -------------------------------------------------------------------
#
# Kept as data in governed-artifact-seed.json beside this script, because
# GovernedArtifactSeedTests runs the same file through the real
# PlatformGovernanceService.ValidateDefinition and a real create-and-promote. One copy, and
# a definition that drifts out of contract fails the build instead of failing here.

SEED_FILE = Path(__file__).with_name("governed-artifact-seed.json")


def load_seeds():
    with SEED_FILE.open() as handle:
        return json.load(handle)["artifacts"]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--api", default="http://127.0.0.1:5192")
    parser.add_argument("--email", default=os.environ.get("NEXORA_TENANT_EMAIL"))
    # No default, for the same reason simulate-platform-journeys.py has none: a literal
    # password in git is a published credential, not a convenience.
    parser.add_argument("--password", default=os.environ.get("NEXORA_TENANT_PASSWORD"))
    parser.add_argument("--dry-run", action="store_true",
                        help="Print the definitions and exit without calling anything.")
    parser.add_argument("--promote", action="store_true",
                        help="Advance each new artifact Draft -> Test -> Production. Only a "
                             "Production QualityMetricSet is read at runtime; for every other "
                             "type this just completes the approval trail.")
    args = parser.parse_args()

    seeds = load_seeds()

    if args.dry_run:
        for seed in seeds:
            print(f"\n--- {seed['artifactType']} / {seed['artifactKey']} ({seed['name']}) ---")
            print(json.dumps(seed["definition"], indent=2))
        return 0

    if not args.email or not args.password:
        print("FATAL: tenant credentials required. Set NEXORA_TENANT_EMAIL and "
              "NEXORA_TENANT_PASSWORD, or pass --email/--password. The account needs "
              "Users/Edit — that is what PlatformGovernanceController gates artifact "
              "creation on.")
        return 2

    api = args.api.rstrip("/")
    status, login = call("POST", f"{api}/api/Auth/Login",
                         body={"email": args.email, "password": args.password})
    if status != 200 or not isinstance(login, dict) or not login.get("token"):
        print(f"FATAL: tenant login failed ({status}): {login}")
        return 2
    token = login["token"]
    print(f"Signed in as {login.get('email')} "
          f"(business unit {login.get('businessUnitId')}, role {login.get('roleName')})")

    for seed in seeds:
        artifact_type, key = seed["artifactType"], seed["artifactKey"]
        status, body = call("POST", f"{api}/api/platform-governance/artifacts", token, {
            "artifactType": artifact_type,
            "artifactKey": key,
            "name": seed["name"],
            "description": seed["description"],
            "definitionJson": json.dumps(seed["definition"]),
            "changeSummary": "Seeded first governed version",
        })
        if status == 409:
            RESULTS.append(("skipped", f"{artifact_type}/{key} already exists"))
            continue
        if status not in (200, 201):
            RESULTS.append(("FAILED", f"{artifact_type}/{key} -> {status}: {body}"))
            continue
        artifact = (body or {}).get("artifact") or {}
        artifact_id, version = artifact.get("id"), artifact.get("version")
        RESULTS.append(("created", f"{artifact_type}/{key} (id {artifact_id}, Draft)"))

        if not args.promote or artifact_id is None:
            continue
        # TEST then PUBLISH — the only path to Production, and each step consumes the
        # artifact's optimistic-concurrency version, so the next expectedVersion comes from
        # the response rather than from a counter kept here.
        for action in ("TEST", "PUBLISH"):
            status, body = call(
                "POST", f"{api}/api/platform-governance/artifacts/{artifact_id}/transition",
                token, {"expectedVersion": version, "action": action,
                        "reason": f"Seeded promotion: {action}"})
            if status not in (200, 201):
                RESULTS.append(("FAILED", f"{artifact_type}/{key} {action} -> {status}: {body}"))
                break
            artifact = (body or {}).get("artifact") or {}
            version = artifact.get("version")
            RESULTS.append(("promoted", f"{artifact_type}/{key} -> {artifact.get('status')}"))

    print()
    for outcome, detail in RESULTS:
        print(f"  {outcome:9} {detail}")
    failures = [x for x in RESULTS if x[0] == "FAILED"]
    print(f"\n{len(RESULTS) - len(failures)} ok, {len(failures)} failed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
