# Branching

One long-lived branch. Everything else is short-lived and deleted on merge.

```text
main ──●──●──●──●──●──►        the only permanent branch; what Render deploys
        \        /
         ●──●──●              a change: branch, PR, CI green, merge, delete
```

## The rule

1. Branch from `main`.
2. Push, open a PR into `main`.
3. `CI green` must pass.
4. Merge. Delete the branch.

Nothing is committed directly to `main`. Nothing long-lived exists beside it.

## Why it is written down

The repo did not look like this. Before 12 August 2026 it carried five `release/*`
and `hotfix/*` branches that were fully merged and still present, five
`codex/wave6-*` branches whose every file had already landed on `main` by another
route, and three stale worktrees. None of it was doing anything, and all of it had
to be read before anyone could answer "is this work in?".

Two of those leftovers cost real time:

- **A worktree pinned to `hotfix/platform-tenant-management` at `c54d947`.** That
  commit was also what the production service was running, 42 commits behind
  `main`, which sent an investigation looking for a deploy problem that was not
  there.
- **A local `main` that was a different project.** The original Vite prototype
  shared *no common ancestor* with `origin/main` — `git merge-base main origin/main`
  returned nothing. It was 264 commits behind and 14 ahead, with commit messages
  like "updating thigns" and ".", and it was what `main` resolved to in one of the
  worktrees. It is now `legacy/vite-prototype`, tagged
  `archive/vite-prototype-20260812`.

## Deleting a branch is not deleting the work

Anything unmerged is tagged `archive/<name>-<date>` and pushed before the branch
goes. The five wave6 branches were archived that way after checking that every
file they added already existed on `main` (7/7, 4/4, 10/10) and that neither
file-less branch contained a single declaration `main` lacked. The tags are
permanent; the branches were noise.

Recover one with `git checkout -b <name> archive/<name>-<date>`.

## The gate

`CI green` (`.github/workflows/ci.yml`) is a required status check on `main`, and
the Render service is set to auto-deploy only **after CI checks pass**, so Render
only ships a commit whose checks passed.

Both halves are set, and both were verified by making them fail rather than by
reading configuration:

- a direct `git push` to `main` is refused — `GH013 ... Required status check
  "CI green" is expected. Changes must be made through a pull request.`
- `GET /v1/services/{id}` reports `autoDeployTrigger: checksPass`.

**The deploy trigger lives in the Render dashboard, not in `render.yaml`.** That
service was created by hand and is not linked to the Blueprint, so the file is a
description and the dashboard is the configuration — see the banner at the top of
`render.yaml`. The `checksPass` line sat in that file for a day, reviewed and
merged, doing nothing, because Render never read it.

That pairing is the point, and it is worth stating why it is not ceremony. This
service applies migrations at boot (`Database__ApplyMigrationsOnStartup=true`), so
a migration that does not apply is not a failed test — it is a container that will
not start, against the live database, with no rollback. At the time the gate was
added the last five pushes to `main` had all left CI red and all five had deployed
anyway.

**`checksPass` does nothing without the required check.** Render waits on a check
that no rule demands, finds nothing to wait for, and deploys. If the ruleset is
ever removed, remove the trigger too rather than leaving a deploy gate that only
looks like one.

## Worktrees

Fine to use, and they are how parallel work stays out of each other's way — but a
worktree pins a branch, so `git branch -d` refuses it and the branch outlives its
purpose. Remove the worktree when the work merges:

```sh
git worktree remove <path> && git worktree prune
```

`git worktree list` should only ever show trees someone is actively using.
