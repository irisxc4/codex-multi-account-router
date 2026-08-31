# Codex Multi-Account Router

An open-source Windows companion and Codex Skill for running multiple ChatGPT/Codex accounts behind Codex Desktop.

It provides isolated account profiles, official browser OAuth, quota-aware routing, sticky task ownership, explicit cross-account continuation, a compact WPF account switcher, and reversible Desktop integration.

> Unofficial community project. It is not affiliated with or endorsed by OpenAI.

## Security model

- No token-paste login flow.
- OAuth parameters, code exchange, refresh, and credential persistence are handled by the official `codex app-server`.
- Each account uses an isolated `CODEX_HOME`.
- Router SQLite stores account metadata and routes, never OAuth tokens, passwords, or cookies.
- `auth.json`, encrypted secrets, keyring material, sessions, databases, logs, and diagnostics are excluded from Git.
- Public-source checks run before CI builds.

This repository contains source code and synthetic test fixtures only. It contains no real account, session, OAuth, or local Router data.

## Features

- Official ChatGPT browser OAuth with device-code fallback.
- Optional per-account proxy selection without changing the system proxy.
- Automatic quota and health synchronization.
- Auto/manual routing with sticky ownership for existing tasks.
- Safe cross-account continuation: the source task stays unchanged while a new target task receives the visible handoff context.
- Official `codex://threads/<thread-id>` navigation after a successful switch.
- Reversible `CODEX_CLI_PATH` Desktop integration and native pass-through.
- Redacted diagnostics, package verification, repair, and uninstall.

## Install as a Codex Skill

Ask Codex:

```text
Install the skill from https://github.com/irisxc4/codex-multi-account-router
```

The Skill teaches Codex how to build, install, operate, diagnose, and modify this Router without exposing account credentials.

## Build from source

Requirements:

- Windows 10/11 x64
- .NET 7 SDK
- Codex Desktop or the official Codex CLI
- PowerShell 7 recommended

```powershell
git clone https://github.com/irisxc4/codex-multi-account-router.git
cd codex-multi-account-router
pwsh -NoProfile -File .\scripts\test-all.ps1
pwsh -NoProfile -File .\scripts\build-release.ps1 -Version 0.1.0-local
```

The self-contained installer is created at:

```text
artifacts/release/CodexRouterSetup.exe
```

Close active Codex tasks before updating the installed Router. The installer does not enable Desktop integration automatically; first complete an official account login from the Overlay.

## Account switching semantics

- New tasks can be assigned automatically using fresh quota and health data.
- Existing tasks stay on their original account.
- Clicking another account creates a continuation task on the target account, commits the switch only after migration succeeds, and opens the target task.
- The original task is never deleted, rewritten, or silently reassigned.

Visible messages and a bounded handoff can be continued. Hidden reasoning, in-flight tool execution, and non-serializable runtime state cannot be migrated.

## Development

```powershell
pwsh -NoProfile -File .\scripts\public-secret-scan.ps1
pwsh -NoProfile -File .\scripts\test-all.ps1
```

The solution contains 12 test assemblies covering accounts, authentication control, storage, routing, workers, RPC multiplexing, migration, Desktop integration, Overlay behavior, protocol compatibility, diagnostics, and domain models.

## License

MIT
