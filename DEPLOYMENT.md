# Nexora deployment

Topology: **Frontend on Vercel** + **Backend on a container host** + **Neon (Postgres)**.
(Vercel cannot host the .NET backend — it's a persistent server with background
workers, not serverless.)

Current: frontend is deployed at **https://nexora1-ai.vercel.app**; it needs
the backend URL below to function.

---

## 1. Backend → Fly.io (recommended)

From the `Backend/` directory (contains `Dockerfile` + `fly.toml`):

```bash
cd Backend
fly launch --no-deploy            # first time: creates the app "nexora-api"
```

Set configuration as **secrets/env** (nothing sensitive is baked into the image —
`appsettings.json` ships only placeholders and the app FAILS FAST without these):

```bash
fly secrets set \
  ConnectionStrings__DefaultConnection="Host=<neon-DIRECT-endpoint>;Database=neondb;Username=neondb_owner;Password=<neon-pw>;SSL Mode=Require;Trust Server Certificate=True" \
  Jwt__Key="<a NEW 32+ byte random key>" \
  Ollama__BaseUrl="https://ollama.com/" \
  Ollama__ApiKey="<ollama key>" \
  Cors__AllowedOrigins__0="https://nexora1-ai.vercel.app"

fly deploy
```

- Health check: `GET /health` → `Healthy` (already wired in `fly.toml`).
- The app URL will be `https://nexora-api.fly.dev` (or your chosen app name).
- **Neon endpoint:** use the **direct** endpoint (no `-pooler`) for now. The
  pooled endpoint needs `Max Auto Prepare=0` + RLS-via-`SET LOCAL` (ADR-0005 Ph2).

> Any container host works (Render, Railway, Azure Container Apps) — they all build
> the same `Dockerfile`. Fly is the simplest for a .NET server + workers.

## 2. Frontend → Vercel (already deployed)

The SPA reads the API base URL from `VITE_API_BASE_URL` **at build time**. In the
Vercel project settings:

1. **Environment Variables** → add `VITE_API_BASE_URL = https://nexora-api.fly.dev`
   (your backend URL).
2. **Redeploy** the frontend so the value is baked in.

`vercel.json` already has the SPA rewrite for client-side routing.

## 3. Demo login (Neon is seeded)

- URL: https://nexora1-ai.vercel.app
- **Email:** `john@example.com`
- **Password:** `Demo@2026!`
- **Business Unit:** `Customer POC`

## Required backend env vars (reference)

| Key (env form) | Purpose |
|---|---|
| `ConnectionStrings__DefaultConnection` | Neon Postgres connection |
| `Jwt__Key` | JWT signing key (**use a new strong key**; ≥32 bytes) |
| `Ollama__BaseUrl` / `Ollama__ApiKey` | AI extraction provider (until the Claude migration, ADR-0001) |
| `Cors__AllowedOrigins__0..n` | allowed frontend origins (the Vercel URL) |
| `ASPNETCORE_ENVIRONMENT` | `Production` (set by the Dockerfile) |

## Security reminders before a real pilot
- **Rotate** the 3 original secrets (old SQL `sa` / JWT / Ollama) — `SECURITY.md`.
- Consider rotating the Neon credentials (they passed through chat).
- Change the demo user's password from `Demo@2026!`.
