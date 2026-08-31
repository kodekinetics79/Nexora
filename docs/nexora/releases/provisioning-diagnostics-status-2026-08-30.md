# Provisioning diagnostics: truthful execution states

## Change

Successful provisioning was classified as `RETRYABLE_SYSTEM_FAILURE`, leading the console to
recommend a retry even when every step had completed. Healthy queued/running attempts and
operator cancellation also received failure-oriented classifications.

- Add explicit `NO_FAILURE` and `CANCELLED` classifications to the diagnostic read model.
- Keep stalled work, actual step failures and execution-level terminal failures actionable.
- Render success, progress and cancellation without recovery advice intended for failures.
- Preserve terminal-state copy when an older backend is still serving during a staggered rollout.
- Unknown classifications request review; they do not invent safe retry advice.
- Avoid repeating the same diagnostic explanation twice.

No provisioning writes, retry rules, access policies, activation gates, database schema, login
styling or commercial processing are changed.

## Verification

- Before the fix: 5 backend and 6 frontend regression cases failed.
- After the fix: 129 local backend tests passed, covering diagnostics, execution, idempotency,
  lease recovery, founding administrators, commercial provisioning and activation.
- 64 frontend tests passed across sign-in, provisioning progress, diagnostics and activation.
- Frontend production build and bundle budget passed.
- Focused ESLint, Impeccable detector and `git diff --check` passed.
- Local browser review of the actual diagnostics component at desktop and 390px mobile widths:
  completion is clearly separated from activation, recovery remains disabled, and blockers remain
  visible. Synthetic local fixtures are not production acceptance evidence.

Production deployment and authenticated post-deployment verification are separate release gates.
This correction does not certify an independent client pilot or waive operational prerequisites.
