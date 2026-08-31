# Security Policy

## Reporting a vulnerability

Use GitHub's private security advisory flow for this repository. Do not open a public issue containing credentials, account identifiers, unredacted diagnostics, session files, local paths, or exploit details that expose user data.

Never attach or paste:

- OAuth access, refresh, or ID tokens
- cookies, JWTs, or browser-session JSON
- `auth.json` or encrypted secret files
- Windows Credential Manager exports
- Router databases, profile directories, or session JSONL
- unredacted logs or diagnostics

Create a minimal reproduction using synthetic accounts and synthetic secrets instead.

## Security boundaries

The Router delegates OAuth and credential persistence to the official Codex app-server and an OS-backed encrypted credential store. Runtime profiles and account data are local-only and are not part of the source repository.

The project does not claim to make multiple accounts one identity. Cross-account continuation creates a new target task and preserves the source task.
