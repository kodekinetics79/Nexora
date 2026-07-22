# Nexora deployment

Topology: **Frontend on Vercel** + **Backend on a container host** + **Neon (Postgres)**.
(Vercel cannot host the .NET backend — it's a persistent server with background
workers, not serverless.)

Current: frontend is deployed at **https://nexora1-ai.vercel.app**; it needs
the backend URL below to function.

---

## 1. Backend → Render

The current backend is deployed at **https://nexora-fyjw.onrender.com**.

Set configuration as **Render environment variables** (nothing sensitive is baked into the image —
`appsettings.json` ships only placeholders and the app FAILS FAST without these):

```text
ConnectionStrings__DefaultConnection=Host=<neon-DIRECT-endpoint>;Database=neondb;Username=neondb_owner;Password=<neon-pw>;SSL Mode=Require;Trust Server Certificate=True
Jwt__Key=<a NEW 32+ byte random key>
Ollama__BaseUrl=https://ollama.com/
Ollama__ApiKey=<ollama key>
Cors__AllowedOrigins__0=https://nexora1-ai.vercel.app
```

- Health check: `GET /health` → `Healthy`.
- The app URL is `https://nexora-fyjw.onrender.com`.
- **Neon endpoint:** use the **direct** endpoint (no `-pooler`) for now. The
  pooled endpoint needs `Max Auto Prepare=0` + RLS-via-`SET LOCAL` (ADR-0005 Ph2).

> Any container host works (Render, Fly.io, Railway, Azure Container Apps) — they all build
> the same `Dockerfile`.

## 2. Frontend → Vercel (already deployed)

The SPA reads the API base URL from `VITE_API_BASE_URL` **at build time**. In the
Vercel project settings:

1. **Environment Variables** → add `VITE_API_BASE_URL = https://nexora-fyjw.onrender.com`
   (your backend URL).
2. **Redeploy** the frontend so the value is baked in.

`vercel.json` already has the SPA rewrite for client-side routing.

## 3. Demo login (Neon is seeded)

- URL: https://nexora1-ai.vercel.app
- **Email:** `robert@example.com`
- **Password:** `Nexora#Pilot-a9bc9e`
- **Business Unit:** `Customer POC`

The backend verifies this demo account on startup so a fresh Render/Neon deploy
can log in immediately. To disable that repair in a production tenant, set:

```text
DemoUser__Enabled=false
```

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
- Change the demo user's password before a real pilot.
