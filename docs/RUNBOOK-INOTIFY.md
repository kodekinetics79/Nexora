# Runbook — the container will not start, and the log blames a file watcher

**Written after the 2026-09-03 outage. Production was down for 90 minutes.**

## The signature

Every deploy fails. `/build-identity` returns Render's 502 page. The service logs, from
the first boot of the new image and then on a loop every ~45 seconds:

```
Unhandled exception. System.IO.IOException: The configured user limit (128) on the
number of inotify instances has been reached, or the per-process limit on the number
of open file descriptors has been reached.
   at System.IO.FileSystemWatcher.StartRaisingEvents()
   at Microsoft.Extensions.FileProviders.Physical.PhysicalFilesWatcher.TryEnableFileSystemWatcher()
   at Microsoft.Extensions.Configuration.Json.JsonConfigurationSource.Build(IConfigurationBuilder builder)
   at Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(String[] args)
   at Program.<Main>$(String[] args) in /src/ERP_RFQ_Automation/Program.cs:line 67
```

## What it means

`WebApplication.CreateBuilder` adds `appsettings.json` and `appsettings.{Environment}.json`
with `reloadOnChange: true`, and each one registers an inotify watch. This happens **before
the first line of our own code runs**. `inotify` instances are a kernel resource limited
**per user on the host**, not per container, so a container can be refused watches because of
pressure it did not create and cannot see.

## The fix

```bash
KEY=$(grep -A2 '^api:' ~/.render/cli.yaml | grep -m1 'key:' | sed 's/.*key:[[:space:]]*//' | tr -d '"'"'" \r')
curl -s -X PUT \
  "https://api.render.com/v1/services/srv-d9csjhe1a83c739phue0/env-vars/DOTNET_USE_POLLING_FILE_WATCHER" \
  -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"value":"1"}'
```

Then redeploy. .NET polls for configuration changes instead of watching, so the dependency
disappears. The cost is a periodic `stat` of two small files.

**Use the single-key endpoint.** `PUT /v1/services/{id}/env-vars` — the plural form — replaces
**every** variable on the service. The Render CLI (v2.21.0) has no `env-vars` subcommand.

The variable is now also in `render.yaml`, so a Blueprint sync will not drop it.

## Do not misdiagnose this

Three traps, each of which cost real time on the night:

1. **`Program.cs:line 67` does not identify the build.** That line is
   `var builder = WebApplication.CreateBuilder(args);` in every recent revision. Use the deploy
   list to see which image is running, not the stack trace.
2. **A failed deploy does not mean the previous release is safe.** Render marked the new deploy
   `update_failed` and fell back to the image that had served happily for three days — and that
   image crash-looped with the identical error. If you conclude "roll back", you will spend
   twenty minutes proving that rolling back changes nothing.
3. **Redeploying is not a remedy.** Two fresh deploys failed, the second in 26 seconds. The
   resource is not released by trying again.

## How to tell it apart from a real startup failure

The application fails fast and loudly on bad configuration — a missing `Jwt__Key`, a placeholder
secret, a migration that will not apply — and those failures name the setting or the migration.
This one names `FileSystemWatcher` and appears **inside `CreateBuilder`**, above anything in
`Program.cs`. If the trace bottoms out in our own code, it is a different problem and this
runbook does not apply.

## What would have caught it sooner

Nothing did. The outage was found by a person polling `/build-identity` by hand. There is no
uptime monitor, no alerting and no error tracker; `/ready` is polled by nobody. An external
check on `/ready` alerting on `status != "Healthy"` remains the highest-value unbuilt
operational control.

## Related

- `docs/GATE9_10_READINESS.md` — availability and the single-instance deployment
- `docs/design/evidence-object-store-cutover.md` — removing the disk, which is what finally
  allows more than one instance and a deploy without downtime
