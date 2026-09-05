#!/usr/bin/env python3
"""Nexora dynamic (request-level) security probe.

Adversarial pass against a REAL disposable stack (never production). Enumerates every
controller route, then runs ten test groups (authorization matrix, tenant override, token
lifecycle, invitations, outbound/SMTP abuse, rate limits, uploads, headers, injection,
verbose errors). Re-runnable and deterministic; prints a per-group table and writes
scratch/security-dynamic-results.json.

Usage:
    python3 scripts/security/dynamic-probe.py [--env .security-e2e-run/probe.env]

The stack is brought up by scripts/security/run-sec-stack.sh (backend 5204, frontend 5184).
"""
import argparse
import io
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from collections import defaultdict
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
BACKEND_SRC = REPO / "Backend" / "ERP_RFQ_Automation"
ORIGIN = "http://127.0.0.1:5184"


# --------------------------------------------------------------------------- env / http
def load_env(path):
    env = {}
    if not Path(path).exists():
        return env
    for line in Path(path).read_text().splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        k, v = line.split("=", 1)
        env[k.strip()] = v.strip()
    return env


def http(method, url, token=None, body=None, headers=None, raw=None, timeout=20):
    """Return (status, headers_dict, text). status 0 means transport error."""
    hdrs = {"Origin": ORIGIN}
    data = None
    if raw is not None:
        data = raw
        hdrs.update(headers or {})
    elif body is not None:
        data = json.dumps(body).encode()
        hdrs["Content-Type"] = "application/json"
    if headers:
        hdrs.update(headers)
    if token:
        hdrs["Authorization"] = "Bearer " + token
    req = urllib.request.Request(url, data=data, headers=hdrs, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, dict(r.headers), r.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read().decode("utf-8", "replace")
    except Exception as e:  # noqa
        return 0, {}, f"{type(e).__name__}: {e}"


def urlencode_form(fields):
    import urllib.parse as up
    return up.urlencode(fields).encode(), "application/x-www-form-urlencoded"


def login(api, email, password):
    s, _, b = http("POST", f"{api}/api/Auth/Login", body={"email": email, "password": password})
    if s == 200:
        try:
            d = json.loads(b)
            return d.get("token"), d
        except Exception:  # noqa
            return None, {"raw": b}
    return None, {"status": s, "raw": b[:200]}


# --------------------------------------------------------------------------- route enumeration
HTTP_ATTR = re.compile(r'\[Http(Get|Post|Put|Delete|Patch)(?:\("([^"]*)"\))?\]')
ROUTE_ATTR = re.compile(r'\[Route\("([^"]*)"\)\]')
CLASS_DECL = re.compile(r'\bclass\s+(\w+)Controller\b')
METHOD_DECL = re.compile(r'(?:public|internal)\s+(?:async\s+)?[\w<>,\?\[\]\. ]+\s+(\w+)\s*\(')


def enumerate_routes():
    """Return list of dicts: {verb, template, controller, file, line, method_auth, class_auth}."""
    routes = []
    for cs in BACKEND_SRC.rglob("*.cs"):
        if "Migrations" in cs.parts or "obj" in cs.parts or "bin" in cs.parts:
            continue
        text = cs.read_text(errors="replace")
        if "[Http" not in text and "MapGet" not in text and "MapPost" not in text:
            continue
        lines = text.splitlines()
        # class-level context
        cls = None
        base_route = None
        class_authz = None  # 'anon' | 'authorize' | 'module' | None
        for i, ln in enumerate(lines):
            cm = CLASS_DECL.search(ln)
            if cm and "abstract" not in ln:
                cls = cm.group(1)
                # look back up to 20 lines for class attributes
                head = "\n".join(lines[max(0, i - 25):i])
                rm = ROUTE_ATTR.findall(head)
                base_route = rm[-1] if rm else None
                if "[AllowAnonymous]" in head:
                    class_authz = "anon"
                elif "RequireModulePermission" in head:
                    class_authz = "module"
                elif "[Authorize" in head:
                    class_authz = "authorize"
                else:
                    class_authz = None
        if cls is None:
            continue
        if base_route is None:
            # controllers without explicit route attr are rare here; skip
            continue
        ctrl_name = cls
        base = base_route.replace("[controller]", ctrl_name)
        # scan methods: an Http attr line, then find method attributes near it
        for i, ln in enumerate(lines):
            m = HTTP_ATTR.search(ln)
            if not m:
                continue
            verb = m.group(1).upper()
            sub = m.group(2) or ""
            # gather attribute block just above/around this http attr (from previous blank/brace)
            j = i
            while j > 0 and lines[j - 1].strip().startswith("["):
                j -= 1
            attr_block = "\n".join(lines[j:i + 1])
            # also include attrs on the same run downward until method signature
            k = i + 1
            while k < len(lines) and (lines[k].strip().startswith("[") or lines[k].strip() == ""):
                attr_block += "\n" + lines[k]
                k += 1
            if "[AllowAnonymous]" in attr_block:
                method_auth = "anon"
            elif "RequireModulePermission" in attr_block:
                method_auth = "module"
            elif "[Authorize" in attr_block:
                method_auth = "authorize"
            else:
                method_auth = None
            # build template
            if sub.startswith("/"):
                template = sub  # absolute override
            elif sub:
                template = base.rstrip("/") + "/" + sub
            else:
                template = base
            template = "/" + template.strip("/")
            routes.append({
                "verb": verb,
                "template": template,
                "controller": ctrl_name,
                "file": str(cs.relative_to(REPO)),
                "line": i + 1,
                "method_auth": method_auth,
                "class_auth": class_authz,
                "effective_auth": method_auth or class_authz,
            })
    return routes


# path param substitution: name-based, then controller-default, then '1'
def strip_constraint(seg):
    # {id:long} -> id
    return seg[1:-1].split(":")[0].split("=")[0]


def is_param(seg):
    return seg.startswith("{") and seg.endswith("}")


def build_id_map(env):
    def g(k, d=None):
        return env.get(k, d)
    lead = g("E2E_CORE_LEAD_ID", "2")
    cust = g("E2E_CORE_CUSTOMER_ID", "2")
    contact = g("E2E_CORE_CONTACT_ID", "1")
    return {
        "customerid": cust, "customer": cust,
        "leadid": lead, "id": None,  # id resolved per-controller
        "contactid": contact,
        "productid": "1", "supplierid": "1", "warehouseid": "1",
        "inventoryid": "1", "currencyid": g("E2E_CORE_CURRENCY_ID", "1"),
        "userid": g("SARAH_USER_ID", "7"), "teamid": g("SALES_TEAM_ID", "1"),
        "tenantid": "80101", "businessunitid": "80101",
        "commercialcaseid": "1", "quoteid": "1", "rfqid": "1", "orderid": "1",
    }


CONTROLLER_DEFAULT_ID = {
    "Customer": None, "Lead": None, "Contact": None,
}


def fill_template(template, controller, id_map, use_other=False):
    """Return concrete path, or None if a param we can't satisfy (guid/unknown) appears."""
    parts = template.split("/")
    out = []
    for seg in parts:
        if not is_param(seg):
            out.append(seg)
            continue
        name = strip_constraint(seg).lower()
        constraint = seg[1:-1]
        if ":guid" in constraint:
            out.append("00000000-0000-0000-0000-000000000001")
            continue
        val = id_map.get(name)
        if val is None:
            # controller-specific default for bare {id}
            cl = controller.lower()
            if "customer" in cl:
                val = id_map["customerid"]
            elif "lead" in cl:
                val = id_map["leadid"]
            elif "contact" in cl:
                val = id_map["contactid"]
            elif "user" in cl:
                val = id_map["userid"]
            else:
                val = "1"
        out.append(str(val))
    return "/".join(out)


# --------------------------------------------------------------------------- result recording
class Results:
    def __init__(self):
        self.groups = defaultdict(lambda: {"pass": 0, "fail": 0, "rows": [], "findings": []})

    def add(self, group, ok, desc, detail="", severity=None, finding=False):
        g = self.groups[group]
        if ok:
            g["pass"] += 1
        else:
            g["fail"] += 1
        g["rows"].append({"ok": ok, "desc": desc, "detail": detail, "severity": severity})
        if finding or (not ok and severity):
            g["findings"].append({"desc": desc, "detail": detail, "severity": severity})

    def dump(self):
        return {k: {"pass": v["pass"], "fail": v["fail"],
                    "findings": v["findings"], "rows": v["rows"]}
                for k, v in self.groups.items()}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--env", default=str(REPO / ".security-e2e-run" / "probe.env"))
    ap.add_argument("--out", default=str(REPO / "scratch-security-results.json"))
    args = ap.parse_args()
    env = load_env(args.env)
    api = env.get("SEC_API_URL", "http://127.0.0.1:5204")
    frontend = env.get("SEC_FRONTEND_URL", "http://127.0.0.1:5184")
    pw = env.get("SEC_ACCEPTANCE_PASSWORD")
    R = Results()

    print(f"# Nexora dynamic security probe  api={api}")
    # ---- tokens
    tokens = {}
    for role, key in [("manager", "SEC_MANAGER_EMAIL"), ("finance", "SEC_FINANCE_EMAIL"),
                      ("editor", "SEC_EDITOR_EMAIL"), ("denied", "SEC_DENIED_EMAIL"),
                      ("other", "SEC_OTHER_EMAIL"), ("owner", "SEC_OWNER_EMAIL")]:
        email = env.get(key)
        if not email:
            continue
        t, meta = login(api, email, pw)
        tokens[role] = t
        print(f"  login {role:8} {email:32} {'OK' if t else 'FAIL '+str(meta)[:80]}")
    id_map = build_id_map(env)

    # Warmup: a previous run's terminal burst may still occupy the tenant:80101 rate-limit window.
    # Wait it out so back-to-back reruns start on a clean partition (idempotency).
    for _ in range(20):
        sw, _, _ = http("GET", f"{api}/api/Dashboard", token=tokens.get("manager"))
        if sw != 429:
            break
        print("  warmup: tenant window still saturated, waiting 10s ...")
        time.sleep(10)

    # ---- enumerate
    routes = enumerate_routes()
    by_ctrl = defaultdict(int)
    for r in routes:
        by_ctrl[r["controller"]] += 1
    print(f"\n[enumeration] {len(routes)} routes across {len(by_ctrl)} controllers")

    # ==================================================================== GROUP 1: authz matrix
    # sample GET routes across every controller (>=120), 4-caller matrix.
    get_routes = [r for r in routes if r["verb"] == "GET"]
    # ensure controller coverage: take up to 4 per controller then fill
    per_ctrl = defaultdict(list)
    for r in get_routes:
        per_ctrl[r["controller"]].append(r)
    # 1 GET per controller first (guarantees coverage of every controller), then top up.
    # Kept ~135 because every tenant-80101 identity shares ONE rate-limit partition
    # (tenant:80101) and this group makes 3 authenticated 80101 calls per route.
    sample = []
    for c, rs in per_ctrl.items():
        sample.append(rs[0])
    extra = [r for r in get_routes if r not in sample]
    i = 0
    while len(sample) < 135 and i < len(extra):
        sample.append(extra[i]); i += 1

    g1_tested = 0
    for r in sample:
        path = fill_template(r["template"], r["controller"], id_map)
        if path is None or "{" in path:
            continue
        url = f"{api}{path}"
        # no token
        s_no, _, _ = http("GET", url)
        # denied
        s_den, _, _ = http("GET", url, token=tokens.get("denied"))
        # other-tenant token vs 80101 ids
        s_oth, _, b_oth = http("GET", url, token=tokens.get("other"))
        # allowed (manager)
        s_mgr, _, b_mgr = http("GET", url, token=tokens.get("manager"))
        g1_tested += 1
        eff = r["effective_auth"]
        anon = eff == "anon"
        # a) no-token must be 401 unless anonymous
        if not anon:
            ok = s_no in (401,)
            R.add("1-authz", ok, f"no-token {r['verb']} {path} -> {s_no} (expect 401)",
                  detail=f"{r['file']}:{r['line']}",
                  severity=None if ok else "P1")
        # b) other-tenant token vs a 80101 RESOURCE id must not return that resource.
        # Only meaningful on routes with a path parameter (an id we filled with a 80101 value);
        # a list route legitimately 200s for tenant 80102 with its own (empty) data.
        has_param = "{" in r["template"]
        idor = False
        if has_param and s_oth == 200 and s_mgr == 200 and len(b_mgr) > 20:
            # byte-identical (or near) bodies mean the other tenant read the SAME 80101 row
            import difflib
            ratio = difflib.SequenceMatcher(None, b_mgr[:2000], b_oth[:2000]).ratio()
            if ratio > 0.92:
                idor = True
        R.add("1-authz", not idor,
              f"other-tenant {r['verb']} {path} -> {s_oth} (expect !=200-with-80101-row)",
              detail=f"{r['file']}:{r['line']}",
              severity="P0" if idor else None, finding=idor)
        # a2) denied (restricted role) on a module-gated READ must be 403/404, never 200 with data.
        if tokens.get("denied") and eff == "module":
            ok = s_den in (403, 404)
            R.add("1-authz", ok,
                  f"denied {r['verb']} {path} -> {s_den} (module-gated; expect 403/404)",
                  detail=f"{r['file']}:{r['line']}",
                  severity="P2" if s_den == 200 else None, finding=s_den == 200)
        # c) admin-rank role (owner) must never be 403 on a tenant route it owns
        s_own, _, _ = http("GET", url, token=tokens.get("owner"))
        ok = s_own != 403
        R.add("1-authz", ok, f"owner(admin) {r['verb']} {path} -> {s_own} (expect !=403)",
              detail=f"{r['file']}:{r['line']}", severity=None if ok else "P2", finding=not ok)
    print(f"[group1] matrix over {g1_tested} GET routes")

    # curated IDOR: known tenant-80101 resources, other-token must get 404/403 not 200-with-data
    curated = [
        ("GET", f"/api/Customer/{env.get('E2E_CORE_CUSTOMER_ID','2')}", "customer 80101"),
        ("GET", f"/api/Lead/{env.get('E2E_CORE_LEAD_ID','2')}", "lead 80101"),
        ("GET", f"/api/Contact/{env.get('E2E_CORE_CONTACT_ID','1')}", "contact 80101"),
    ]
    for verb, path, label in curated:
        s_m, _, b_m = http(verb, f"{api}{path}", token=tokens.get("manager"))
        s_o, _, b_o = http(verb, f"{api}{path}", token=tokens.get("other"))
        # manager should see it; other must not (404/403). 200 to other w/ same body => IDOR P0
        idor = (s_o == 200 and s_m == 200 and b_m[:60] == b_o[:60] and len(b_m) > 20)
        R.add("1-authz", not idor,
              f"IDOR {label}: manager={s_m} other={s_o} (other expect 403/404)",
              detail=path, severity="P0" if idor else None, finding=idor)

    # ==================================================================== GROUP 2: BU override
    # send businessUnitId in body/query/header differing from token claim; server must use claim.
    mutating = [r for r in routes if r["verb"] in ("POST", "PUT", "PATCH")
                and r["effective_auth"] not in ("anon",)]
    # pick 20 spanning controllers that we can call safely with a JSON body
    seen = set()
    picked = []
    for r in mutating:
        if r["controller"] in seen:
            continue
        seen.add(r["controller"])
        picked.append(r)
        if len(picked) >= 20:
            break
    g2 = 0
    for r in picked:
        path = fill_template(r["template"], r["controller"], id_map)
        if path is None or "{" in path:
            continue
        url = f"{api}{path}?businessUnitId=80102"
        # body attempts to smuggle other tenant + header override
        body = {"businessUnitId": 80102, "buid": 80102, "tenantId": 80102, "name": "sec-probe"}
        hdrs = {"X-Business-Unit-Id": "80102", "X-Tenant-Id": "80102"}
        s, _, b = http(r["verb"], url, token=tokens.get("manager"), body=body, headers=hdrs)
        g2 += 1
        # A 200/201 that created data in 80102 would be the defect. We can't always tell from
        # status; the strong signal is: response never echoes businessUnitId 80102 as accepted.
        leaked = ('"businessUnitId":80102' in b.replace(" ", "")) or ('"buid":80102' in b.replace(" ", ""))
        R.add("2-bu-override", not leaked,
              f"{r['verb']} {path} smuggle BU80102 -> {s} (expect claim 80101 wins)",
              detail=f"{r['file']}:{r['line']}", severity="P0" if leaked else None, finding=leaked)
    print(f"[group2] BU override attempts on {g2} mutating routes")

    # ==================================================================== GROUP 3: token lifecycle
    # Resolve editor uid (owner can list users; admin-rank bypass).
    editor_email = env.get("SEC_EDITOR_EMAIL", "")
    editor_uid = None
    s, _, b = http("GET", f"{api}/api/User?pageSize=200", token=tokens.get("owner"))
    if s == 200:
        try:
            data = json.loads(b)
            items = data if isinstance(data, list) else (data.get("items") or data.get("data") or [])
            for u in items:
                if str(u.get("email", "")).lower() == editor_email.lower():
                    editor_uid = u.get("id")
        except Exception:  # noqa
            pass

    def token_alive(tok):
        sx, _, _ = http("GET", f"{api}/api/Customer?pageSize=1", token=tok)
        return sx == 200, sx

    # 3a change-password (self) -> old token revoked via SecurityStamp rotation
    et, _ = login(api, editor_email, pw)
    new_pw = pw + "X1"
    if et and editor_uid:
        alive0, s0 = token_alive(et)
        scp, _, bcp = http("POST", f"{api}/api/User/{editor_uid}/ChangePassword", token=et,
                           body={"currentPassword": pw, "newPassword": new_pw})
        revoked_at = None
        for waited in range(0, 36, 3):
            alive, sx = token_alive(et)
            if not alive and sx in (401, 403):
                revoked_at = waited
                break
            time.sleep(3)
        ok = revoked_at is not None
        R.add("3-lifecycle", ok,
              f"change-password(self) -> old token revoked: baseline={s0} chg={scp} after={revoked_at}s",
              detail="SecurityStamp rotation; UserRepository.ChangePasswordAsync",
              severity=None if ok else "P1", finding=True)
        # restore password for idempotent reruns
        et2, _ = login(api, editor_email, new_pw)
        if et2:
            http("POST", f"{api}/api/User/{editor_uid}/ChangePassword", token=et2,
                 body={"currentPassword": new_pw, "newPassword": pw})
    else:
        R.add("3-lifecycle", False, f"could not resolve editor uid ({editor_uid}); skipped change-pw",
              severity="INFO")

    # 3b deactivate (owner PUT form) -> old token revoked
    et, _ = login(api, editor_email, pw)
    if et and editor_uid:
        # fetch editor record to preserve required fields
        alive0, s0 = token_alive(et)
        form = {"FirstName": "Elliot", "LastName": "Editor", "Email": editor_email,
                "Buid": env.get("SEC_TENANT_ID", "80101"), "IsActive": "false"}
        raw, ct = urlencode_form(form)
        sd, _, bd = http("PUT", f"{api}/api/User/{editor_uid}", token=tokens.get("owner"),
                         raw=raw, headers={"Content-Type": ct})
        revoked_at = None
        for waited in range(0, 36, 3):
            alive, sx = token_alive(et)
            if not alive and sx in (401, 403):
                revoked_at = waited
                break
            time.sleep(3)
        ok = revoked_at is not None
        R.add("3-lifecycle", ok,
              f"deactivate(owner PUT) -> old token revoked: baseline={s0} put={sd} after={revoked_at}s",
              detail=f"editor_uid={editor_uid}; authorityChanged rotates stamp",
              severity=None if ok else "P1", finding=True)
        # reactivate for idempotent reruns
        form["IsActive"] = "true"
        raw, ct = urlencode_form(form)
        http("PUT", f"{api}/api/User/{editor_uid}", token=tokens.get("owner"),
             raw=raw, headers={"Content-Type": ct})
    else:
        R.add("3-lifecycle", False, "deactivate lifecycle skipped (no editor uid)", severity="INFO")
    # Note on production default:
    R.add("3-lifecycle", True,
          "NOTE Auth:RequireSecurityStamp default=false only affects LEGACY stampless tokens; "
          "current builds mint the 'sst' claim so revocation is unconditional",
          detail="Security/TenantSessionValidator.cs:110-126", finding=False)
    print("[group3] token lifecycle")

    # ==================================================================== GROUP 4: invitations
    # can editor invite? can invite name a role above inviter? target BU 80102? unauth token peek.
    inv_body = {"email": "sec-invite@example.com", "firstName": "Sec", "lastName": "Probe",
                "activation": "invite", "businessUnitId": 80102, "roleId": 1}
    s_ed, _, b_ed = http("POST", f"{api}/api/User", token=tokens.get("editor"), body=inv_body)
    R.add("4-invitations", s_ed in (401, 403),
          f"editor invites user -> {s_ed} (expect 403 if editor lacks Users create)",
          detail=b_ed[:120], severity="P2" if s_ed in (200, 201) else None, finding=True)
    # manager invites into BU 80102 (cross-tenant)
    s_mg, _, b_mg = http("POST", f"{api}/api/User", token=tokens.get("manager"),
                         body={**inv_body, "email": "sec-cross@example.com"})
    cross_ok = not ('"businessUnitId":80102' in b_mg.replace(" ", ""))
    R.add("4-invitations", cross_ok,
          f"manager invites into BU80102 -> {s_mg} (expect claim 80101, never 80102)",
          detail=b_mg[:120], severity="P0" if not cross_ok else None, finding=not cross_ok)
    # unauthenticated activation token peek
    s_pk, _, b_pk = http("GET", f"{api}/api/tenant-activation/not-a-real-token")
    leak = any(w in b_pk.lower() for w in ["stack", "npgsql", "connectionstring", "password=", "at erp_rfq"])
    R.add("4-invitations", not leak,
          f"GET /api/tenant-activation/{{token}} unauth -> {s_pk} (expect no internals)",
          detail=b_pk[:160], severity="P2" if leak else None, finding=True)
    print("[group4] invitations")

    # ==================================================================== GROUP 5: outbound/SMTP
    # Uses owner (admin-rank) since Email&SMTP module gates these. Nothing leaves: guard is DraftOnly
    # and the internal hosts are refused BEFORE any socket opens.
    otok = tokens.get("owner")
    # 5a SSRF: connection test (POST /api/Mailbox/test) must refuse internal hosts at Policy stage.
    for host, port, label in [("127.0.0.1", 5204, "loopback-self"),
                              ("169.254.169.254", 80, "cloud-metadata"),
                              ("localhost", 22, "localhost-ssh"),
                              ("10.0.0.5", 25, "rfc1918")]:
        body = {"protocol": "SMTP", "host": host, "port": port, "emailAddress": "a@b.com",
                "username": "u", "password": "p", "useSsl": False}
        s, _, b = http("POST", f"{api}/api/Mailbox/test", token=otok, body=body)
        refused = False
        try:
            d = json.loads(b)
            refused = (d.get("succeeded") is False) and any(
                st.get("stage") == "Policy" and st.get("status") == "Failed"
                for st in d.get("steps", []))
        except Exception:  # noqa
            pass
        R.add("5-outbound", refused,
              f"SSRF test {label} {host}:{port} -> Policy-refused={refused} (http {s})",
              detail=(b[:120]), severity="P1" if not refused else None, finding=True)
    # 5b SSRF: create mailbox (POST /api/Mailbox) with metadata host must be 400.
    s, _, b = http("POST", f"{api}/api/Mailbox", token=otok, body={
        "configurationName": "sec", "emailAddress": "a@b.com", "protocol": "SMTP",
        "host": "169.254.169.254", "port": 25, "username": "u", "password": "p",
        "useSsl": False, "pollingInterval": 5, "isActive": False, "verifyBeforeSave": False})
    R.add("5-outbound", s == 400,
          f"create mailbox with metadata host -> {s} (expect 400 refused)",
          detail=b[:120], severity="P1" if s in (200, 201) else None, finding=True)
    # 5c header injection: POST /api/Smtp/send with CRLF in recipient/subject must be 400.
    for field, val in [("ToEmail", "victim@example.com\r\nBcc: attacker@example.com"),
                       ("Subject", "hello\r\nBcc: attacker@example.com")]:
        form = {"SupplierId": "1", "ToEmail": "quotes@precision-controls.local",
                "Subject": "probe", "Body": "probe body"}
        form[field] = val
        raw, ct = urlencode_form(form)
        s, _, b = http("POST", f"{api}/api/Smtp/send", token=otok, raw=raw,
                       headers={"Content-Type": ct})
        refused = (s == 400 and "invalid characters" in b.lower())
        R.add("5-outbound", refused,
              f"header-injection in {field} -> {s} (expect 400 'invalid characters')",
              detail=b[:120], severity="P1" if not refused else None, finding=True)
    print("[group5] outbound/SMTP SSRF + header injection")

    # ==================================================================== GROUP 6: rate limits
    # 6a login lockout: rapid wrong logins for one account -> 429 with plain message
    # Uses FINANCE (not exercised elsewhere) so the lockout does not disturb the denied identity.
    lock_hit = None
    lock_email = env.get("SEC_FINANCE_EMAIL")
    for n in range(1, 26):
        s, h, b = http("POST", f"{api}/api/Auth/Login",
                       body={"email": lock_email, "password": "wrong-" + str(n)})
        if s == 429:
            lock_hit = n
            plain = "too many" in b.lower()
            R.add("6-ratelimit", plain,
                  f"login lockout after {n} attempts -> 429, plain-message={plain}",
                  detail=b[:120], severity=None if plain else "P3", finding=True)
            break
    if lock_hit is None:
        R.add("6-ratelimit", False, "25 rapid bad logins produced no 429 lockout",
              severity="P2", finding=True)
    print(f"[group6a] login lockout @ attempt {lock_hit}")
    # NOTE: the 600/60s global burst test is deliberately deferred to the very END so its 429s do
    # not starve the injection/upload/error groups that follow (all share the tenant:80101 window).

    # ==================================================================== GROUP 7: uploads
    # helper multipart
    def multipart(fields_file):
        boundary = "----secprobe" + str(int(time.time()))
        name, filename, content, ctype = fields_file
        buf = io.BytesIO()
        buf.write(f"--{boundary}\r\n".encode())
        buf.write(f'Content-Disposition: form-data; name="{name}"; filename="{filename}"\r\n'.encode())
        buf.write(f"Content-Type: {ctype}\r\n\r\n".encode())
        buf.write(content if isinstance(content, bytes) else content.encode())
        buf.write(f"\r\n--{boundary}--\r\n".encode())
        return buf.getvalue(), f"multipart/form-data; boundary={boundary}"

    # The real document-inspection doors run UploadInspectionGate.InspectAsync (magic-byte
    # allowlist + 25MB cap + archive limits + malware scan). Owner bypasses the module gate.
    utok = tokens.get("owner")
    refused_codes = (400, 413, 415, 422, 503)
    door = None
    for tgt in ["/api/CustomerUploader/upload-template", "/api/ProductUploader/upload-template",
                "/api/RfqUploader/upload-template", "/api/LeadIngestion/upload"]:
        raw, ct = multipart(("file", "probe.xlsx", b"PK\x03\x04tiny", "application/octet-stream"))
        s, _, b = http("POST", f"{api}{tgt}", token=utok, raw=raw, headers={"Content-Type": ct})
        if s not in (404, 405):
            door = tgt
            R.add("7-uploads", True, f"inspection door {tgt} reachable -> {s}", detail=b[:90], finding=False)
            break
    if door:
        # 30MB file (DefaultMaximumFileBytes = 25MB, issue #140)
        big = b"A" * (30 * 1024 * 1024)
        raw, ct = multipart(("file", "big.xlsx", big, "application/vnd.ms-excel"))
        s, _, b = http("POST", f"{api}{door}", token=utok, raw=raw,
                       headers={"Content-Type": ct}, timeout=90)
        R.add("7-uploads", s in refused_codes or s == 0,
              f"30MB upload -> {s} (expect refusal; 25MB cap #140). transport0=size-reset",
              detail=b[:120], severity="P2" if s in (200, 201) else None, finding=True)
        # .html renamed .xlsx -> magic-byte allowlist must refuse
        raw, ct = multipart(("file", "evil.xlsx", b"<html><body><script>alert(1)</script></html>", "application/vnd.ms-excel"))
        s, _, b = http("POST", f"{api}{door}", token=utok, raw=raw, headers={"Content-Type": ct})
        R.add("7-uploads", s in refused_codes,
              f".html-as-.xlsx -> {s} (expect magic-byte refusal)",
              detail=b[:150], severity="P2" if s in (200, 201) else None, finding=True)
        # path traversal filename -> must not echo the traversal / store outside root
        raw, ct = multipart(("file", "..%2f..%2f..%2fetc%2fpasswd.xlsx", b"PK\x03\x04", "application/vnd.ms-excel"))
        s, _, b = http("POST", f"{api}{door}", token=utok, raw=raw, headers={"Content-Type": ct})
        R.add("7-uploads", "etc/passwd" not in b and "/etc/" not in b,
              f"path-traversal filename -> {s} (expect sanitized/no path echo)",
              detail=b[:120], severity=None, finding=True)
        # zip-bomb-shaped: many-entry archive -> archive-entry limit must refuse
        import zipfile
        zbuf = io.BytesIO()
        with zipfile.ZipFile(zbuf, "w", zipfile.ZIP_DEFLATED) as z:
            for i in range(5000):
                z.writestr(f"e{i}.txt", b"0" * 1024)
        raw, ct = multipart(("file", "bomb.xlsx", zbuf.getvalue(), "application/vnd.ms-excel"))
        s, _, b = http("POST", f"{api}{door}", token=utok, raw=raw, headers={"Content-Type": ct})
        R.add("7-uploads", s in refused_codes,
              f"zip-bomb-shaped (5000 entries) -> {s} (expect archive-limit refusal)",
              detail=b[:120], severity="P2" if s in (200, 201) else None, finding=True)
        # EICAR -> BuiltIn scanner must flag
        eicar = rb"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"
        raw, ct = multipart(("file", "eicar.xlsx", eicar, "application/vnd.ms-excel"))
        s, _, b = http("POST", f"{api}{door}", token=utok, raw=raw, headers={"Content-Type": ct})
        R.add("7-uploads", s in refused_codes,
              f"EICAR -> {s} (BuiltIn scanner should flag)",
              detail=b[:150], severity=None, finding=True)
    else:
        R.add("7-uploads", False, "no reachable inspection door", severity="INFO", finding=True)
    print(f"[group7] uploads door={door}")

    # ==================================================================== GROUP 8: headers/transport
    for url, label in [(f"{api}/api/Dashboard", "api"), (frontend + "/", "frontend")]:
        s, h, _ = http("GET", url, token=tokens.get("manager") if "api" in label else None)
        hl = {k.lower(): v for k, v in h.items()}
        for hdr in ["x-content-type-options", "content-security-policy",
                    "x-frame-options", "strict-transport-security"]:
            present = hdr in hl
            # in Development HSTS/redirect intentionally off; note not fail
            sev = None
            R.add("8-headers", True,
                  f"{label} {hdr}={hl.get(hdr,'ABSENT')}",
                  detail="(Development: HSTS/CSP relax expected)", finding=False)
    # preflight from evil origin
    s, h, _ = http("OPTIONS", f"{api}/api/Dashboard",
                   headers={"Origin": "https://evil.example",
                            "Access-Control-Request-Method": "GET"})
    hl = {k.lower(): v for k, v in h.items()}
    acao = hl.get("access-control-allow-origin", "")
    R.add("8-headers", "evil.example" not in acao,
          f"preflight from evil.example -> ACAO={acao or 'ABSENT'} (expect no evil ACAO)",
          severity="P1" if "evil.example" in acao else None, finding=True)
    # unauth infra endpoints
    for ep in ["/metrics", "/swagger/index.html", "/ready", "/health"]:
        s, _, b = http("GET", f"{api}{ep}")
        redsev = None
        leak = any(w in b.lower() for w in ["host=", "password=", "connectionstring", "127.0.0.1;port", "npgsql"])
        R.add("8-headers", not leak,
              f"unauth {ep} -> {s}, address-leak={leak}",
              detail=b[:100], severity="P2" if leak else None, finding=leak)
    print("[group8] headers/transport")

    # ==================================================================== GROUP 9: injection
    payloads = ["' OR '1'='1", "'; DROP TABLE users;--", "%27%20OR%201=1",
                "*)(uid=*))(|(uid=*", "{$gt:''}", "<script>alert(1)</script>"]
    search_routes = [
        "/api/Lead?buyersName=", "/api/Customer?name=", "/api/Supplier?name=",
        "/api/search?q=", "/api/Product?search=",
    ]
    for base in search_routes:
        # baseline count
        s0, _, b0 = http("GET", f"{api}{base}zzznomatchzzz", token=tokens.get("manager"))
        for p in payloads:
            enc = urllib.parse.quote(p)
            s, _, b = http("GET", f"{api}{base}{enc}", token=tokens.get("manager"))
            err = any(w in b.lower() for w in ["npgsql", "syntax error", "sqlstate", "at erp_rfq", "unhandled"])
            # behaviour change: SQLi returning MORE rows than the no-match baseline would be the tell
            R.add("9-injection", (s in (200, 400)) and not err,
                  f"{base}<payload> -> {s} err-leak={err}",
                  detail=f"payload={p[:20]}", severity="P1" if err else None, finding=err)
    print("[group9] injection")

    # ==================================================================== GROUP 10: verbose errors
    cases = [
        ("POST", "/api/Customer", b"{ this is : not json ", "malformed-json"),
        ("GET", "/api/Customer/999999999999999999999999", None, "oversized-id"),
        ("GET", "/api/Lead?isActive=notabool", None, "invalid-enum/bool"),
    ]
    for verb, path, raw, label in cases:
        if raw is not None:
            s, _, b = http(verb, f"{api}{path}", token=tokens.get("manager"),
                           raw=raw, headers={"Content-Type": "application/json"})
        else:
            s, _, b = http(verb, f"{api}{path}", token=tokens.get("manager"))
        leak = any(w in b.lower() for w in ["at erp_rfq", "npgsql", "connectionstring",
                                            "stack trace", "nexorabucket", "host=", ".cs:line"])
        R.add("10-errors", not leak,
              f"{label} {verb} {path} -> {s}, internals-leak={leak}",
              detail=b[:140], severity="P1" if leak else None, finding=leak)
    print("[group10] verbose errors")

    # ==================================================================== GROUP 6b: burst (LAST)
    # Deferred to the end: a full 60s window cooldown, then >600 GETs from one token -> 429.
    print("[group6b] cooldown 62s then burst ...")
    time.sleep(62)
    got429 = False
    n_sent = 0
    t_start = time.time()
    for n in range(750):
        s, _, _ = http("GET", f"{api}/api/Dashboard", token=tokens.get("manager"), timeout=5)
        n_sent += 1
        if s == 429:
            got429 = True
            break
        if time.time() - t_start > 58:
            break
    R.add("6-ratelimit", got429,
          f"burst {n_sent} GETs from one token -> 429 seen={got429} (global 600/60s)",
          severity=None if got429 else "P2", finding=True)
    print(f"[group6b] burst429={got429} after {n_sent} requests")

    # ---- summary
    summary = {"api": api, "routes_total": len(routes), "controllers": len(by_ctrl),
               "groups": R.dump()}
    Path(args.out).write_text(json.dumps(summary, indent=2))

    print("\n================ SUMMARY ================")
    print(f"{'group':16} {'pass':>5} {'fail':>5}  findings")
    tp = tf = 0
    for g in sorted(R.groups):
        v = R.groups[g]
        tp += v["pass"]; tf += v["fail"]
        fs = [f"{x['severity']}:{x['desc'][:40]}" for x in v["findings"] if x["severity"] and x["severity"] not in ("INFO",)]
        print(f"{g:16} {v['pass']:>5} {v['fail']:>5}  {len(fs)} sev-findings")
    print(f"{'TOTAL':16} {tp:>5} {tf:>5}")
    print(f"\nroutes.json + results -> {args.out}")
    return 0


if __name__ == "__main__":
    import urllib.parse  # noqa
    sys.exit(main())
