---
name: codex-multi-account-router
description: Build, install, operate, diagnose, or safely modify the Windows Codex Multi-Account Router. Use for multi-account ChatGPT login, quota-aware routing, account switching, thread continuation, Desktop integration, packaging, and recovery; do not use for generic Codex questions unrelated to this project.
---

# Codex Multi-Account Router

Use the source tree containing this file as the project root.

## Safety invariants

- Never ask the user to paste an access token, refresh token, JWT, cookie, `auth.json`, or browser-session JSON.
- Account onboarding must use the official Codex app-server login flow. Credentials stay inside each local profile and the OS-backed encrypted credential store.
- Never read, print, copy, commit, upload, or include in diagnostics: profile credentials, `auth.json`, keyring material, `*.age`, session JSONL, Router databases, logs, cookies, or account data.
- Do not install, repair, uninstall, enable Desktop integration, change `CODEX_CLI_PATH`, terminate Codex, or open an OAuth/deep link unless the user explicitly requested that mutation in the current task.
- Preserve the source thread and its route. Cross-account continuation creates a new target thread; it must not silently reassign the original thread ID.
- Before any public commit or release, run `scripts/public-secret-scan.ps1` and inspect the exact staged file list.

## Choose the workflow

### Build or test

On Windows with the .NET 7 SDK:

```powershell
pwsh -NoProfile -File .\scripts\test-all.ps1
pwsh -NoProfile -File .\scripts\build-release.ps1 -Version 0.1.0-local
```

The release bundle is written to `artifacts/release/`. Do not commit it; publish installers as GitHub Release assets instead.

### Install or update

Only after explicit approval, build or use a trusted release and run `CodexRouterSetup.exe`. Do not enable Desktop integration until at least one account has completed official browser OAuth and `account/read` verification. Avoid replacing a live Router while a Codex task is active.

### Add or relogin an account

Use the Overlay account action. Prefer official browser OAuth; device-code login is a fallback. Never introduce a token-paste path. If login fails, diagnose the official app-server response, profile storage, keyring persistence, and selected proxy route without reading credential contents.

### Route or switch accounts

- Auto mode selects an eligible account only for a new task using health and fresh quota data.
- Existing tasks remain sticky to their owner.
- An explicit cross-account click waits for an idle source, creates and seeds a continuation on the target, commits the target selection only after success, then opens the official `codex://threads/<thread-id>` deep link.
- On failure, keep the original task and route untouched. Explain that visible context can be handed off, but hidden reasoning and active tool state cannot be migrated.

### Diagnose or recover

Prefer read-only compatibility checks and the built-in redacted diagnostics command. Verify native Codex discovery and reversible Desktop integration before changing state. Diagnostics must remain synthetic-secret tested and must never package profile directories or credential stores.

## Change discipline

- Keep authentication changes isolated from routing and UI changes.
- Run the relevant focused tests, then all 12 test assemblies before packaging.
- Preserve native pass-through and reversible recovery paths.
- Treat official Codex app-server protocol behavior as the authority; fail closed on unsupported protocol changes.
- For public changes, run:

```powershell
pwsh -NoProfile -File .\scripts\public-secret-scan.ps1
git status --short
git diff --cached --name-only
```

Stop if the scan reports any private path, email outside reserved example domains, credential-shaped value, credential file, session, database, log, or generated artifact.
