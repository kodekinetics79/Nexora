# Nexora — Security & Secrets

## ⚠️ ACTION REQUIRED: rotate three exposed credentials

These credentials were found in **plaintext** in tracked config/docs during the
Phase 0 baseline. They have been **removed from source** (replaced with
`__PLACEHOLDER__` tokens). Because they were live and stored in cleartext, each
must be **rotated** — removal from source does not undo prior exposure.

| # | Secret | Where it was | Blast radius if leaked | Priority |
|---|--------|--------------|------------------------|----------|
| 1 | **JWT signing key** (`Jwt:Key`) | `appsettings.json`, `appsettings.Development.json` | **Auth bypass.** Anyone with this key can forge a valid JWT for any user/role/business-unit → full impersonation across tenants. | **P0** |
| 2 | **SQL Server `sa` password** | `appsettings*.json`, inner `Readme.md` | Full control of the database (`sa` = sysadmin) at `__DB_SERVER__`. Read/write/drop all tenant data. | **P0** |
| 3 | **Ollama Cloud API key** (`Ollama:ApiKey`) | `appsettings*.json` | Billable API usage on your account; potential data-egress of customer RFQ content to a third party. | **P1** |

### Rotation steps (owner: you)
1. **JWT key** — generate a new 256-bit random key, set it via the config
   mechanism below, and **restart the API**. All existing tokens are invalidated
   (users re-login). Do this first.
2. **SQL `sa`** — change the `sa` password on the SQL Server host. Better:
   create a **least-privilege application login** scoped to the
   `ERP_RFQ_Automation` database instead of using `sa` at all. Coordinate —
   the DB may be shared.
3. **Ollama key** — revoke the exposed key in the Ollama account and issue a new
   one. (Note: an ADR is in progress to move the AI layer to Claude — see
   `docs/adr/`.)

## Configuration pattern (how secrets are supplied now)

`appsettings.json` is a **committed template containing only placeholders**
(`__DB_PASSWORD__`, `__JWT_SIGNING_KEY__`, `__OLLAMA_API_KEY__`, …). Real values
are supplied per-environment and are **never committed**:

- **Local dev / demo:** real values live in `appsettings.Development.json`, which
  is **git-ignored** (see `.gitignore`). The app loads it automatically when
  `ASPNETCORE_ENVIRONMENT=Development`, so the demo runs unchanged.
- **Preferred (more secure):** use .NET user-secrets, e.g.
  ```
  dotnet user-secrets init
  dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=...;"
  dotnet user-secrets set "Jwt:Key" "<new-random-key>"
  dotnet user-secrets set "Ollama:ApiKey" "<new-key>"
  ```
- **Production:** supply via environment variables / a secrets manager. Because
  `appsettings.json` holds only placeholders, a misconfigured prod deploy
  **fails closed** (cannot connect / cannot sign tokens) rather than silently
  using a leaked credential.

## Notes
- `appsettings.Development.json` still holds the **old** live values on this
  machine so the demo keeps working. Replace them with the **rotated** values
  when you rotate.
- User-uploaded images under `wwwroot/UserImages`, `wwwroot/InventoryImages`,
  etc. are git-ignored (may contain personal data). They remain on disk for the
  demo but are not committed.
