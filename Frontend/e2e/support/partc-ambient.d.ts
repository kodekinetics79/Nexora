/**
 * TEST-ONLY ambient declarations for the PART C harness.
 *
 * This workspace has no `@types/node` (checked: nothing under `Frontend/node_modules/@types`),
 * and adding one would mean editing `package.json` and running an install — a change to shared
 * project configuration, which an independent SDET does not get to make on the way to a
 * certification run.
 *
 * The harness therefore declares the single Node global it actually uses. Everything else it
 * needs — `crypto.subtle`, `TextEncoder`, `console`, `URL` — is already in the DOM lib the root
 * tsconfig enables, which is why the harness derives TOTP with Web Crypto instead of
 * `node:crypto` and takes the authenticator seed from the environment instead of reading the
 * seed file off disk.
 *
 * Deliberately narrow: `env` only, readonly values, no `fs`, no `child_process`. A harness that
 * could not reach the filesystem cannot quietly start fixing things.
 */

declare const process: {
  readonly env: Record<string, string | undefined>;
};
