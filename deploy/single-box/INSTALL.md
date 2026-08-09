# Nexora — single-box install

This builds the demo machine, and it is the same machine the client gets. Demoing on one
topology and shipping another proves nothing about the thing delivered, so there is one
procedure here and it is used for both.

**Host:** Linux, 8 CPU, 32 GB RAM, 100 GB disk. A GPU is optional — see *Model sizing*.

---

## 1. Ollama, on the host

Ollama runs natively rather than in a container: GPU access is far simpler, and the backend
must reach it over **loopback**.

```bash
curl -fsSL https://ollama.com/install.sh | sh
ollama pull qwen2.5:14b-instruct     # see Model sizing before choosing
curl -s http://127.0.0.1:11434/api/tags | head    # must answer
```

> **Do not** point Nexora at `https://ollama.com/`, at a container name, or at a LAN address.
> Nexora classifies a provider as `Local` only when the endpoint is loopback. Anything else is
> `External` and unstructured extraction is refused for every tenant until a per-tenant
> authorization is granted. That refusal is the single reason PDF and email-body RFQs currently
> fail in the cloud deployment.

## 2. Configuration

```bash
cd deploy/single-box
cp .env.example .env
openssl rand -base64 48        # run once per >>> SET THIS <<< line
mkdir -p /srv/nexora/evidence /srv/nexora/web
```

Fill in every `>>> SET THIS <<<`. There are nine: the database password, four signing/protection
secrets, three commercial-finance secrets, and the SMTP password.

## 3. Frontend

```bash
cd Frontend
VITE_API_BASE_URL=http://<host-or-ip> npm ci && npm run build
cp -r dist/* /srv/nexora/web/
```

## 4. Start

```bash
cd deploy/single-box
docker compose up -d --build
docker compose logs -f backend        # watch the migration run complete
curl -s http://127.0.0.1/health       # liveness
curl -s http://127.0.0.1/ready        # evidence storage, scanner, workers — from the host only
```

ClamAV downloads its signature database on first boot and reports unhealthy for a few minutes.
That is expected; `/ready` goes green once it finishes.

## 5. Verify before demoing

Do these in order. Each one has failed silently in the past, which is why they are listed.

1. **A spreadsheet RFQ ingests.** Upload one with a title block above the header and a unit
   column. Confirm the lines appear *with their units* — a quantity with no unit is a quotation
   waiting to go out wrong.
2. **A Word RFQ ingests with no model involved.** Upload one of the `.docx` samples. It should
   read deterministically: check the log line "was read deterministically from its table". This
   path does not touch Ollama at all.
3. **Email intake works.** Configure the mailbox under Setup → Mailboxes, send an RFQ to it, and
   confirm a lead appears within the poll interval.
4. **Outbound email actually leaves.** Send a supplier RFQ and confirm it arrives. If
   `Notifications__Provider` is still `console`, nothing is sent and the solicitation lands in
   `DeliveryFailed` — honest, but invisible unless you look.
5. **A PDF RFQ ingests.** This is the one that exercises Ollama. If it holds for review with an
   authorization message, the endpoint is not loopback — go back to step 1.

## Model sizing

| Host | Model | Extraction |
|---|---|---|
| GPU, 24 GB+ VRAM | `qwen2.5:14b-instruct` or `32b` | Comfortably inside the 60-second target |
| GPU, 12–16 GB | `qwen2.5:14b-instruct` quantized | Around the target |
| CPU only, 32 GB | `qwen2.5:7b-instruct` | **Above the target** — measure before promising |

Two things worth knowing before buying hardware. First, a table-structured document — most
spreadsheets, and Word files whose lines sit in a table — is read **deterministically with no
model at all**, so it is unaffected by any of this. The model matters for prose and scans.
Second, the configured client timeout is 180 seconds against a 60-second requirement, so a slow
box will not error; it will simply be three times slower than promised and nobody will notice
until acceptance.

## Backup — not optional

`EVIDENCE_PATH` holds every source document. The application never deletes from it, and there is
no second copy anywhere. Back it up with the database, and **test a restore** rather than
assuming one works.

```bash
docker compose exec -T postgres pg_dump -U nexora nexora | gzip > nexora-$(date +%F).sql.gz
tar czf evidence-$(date +%F).tar.gz -C /srv/nexora evidence
```

## Handover to the client

Nothing changes but the host. The same compose file, the same `.env` shape, fresh secrets, and
their own mailbox. Rotate every secret at handover — a demo box's keys must never become a
production box's keys.
