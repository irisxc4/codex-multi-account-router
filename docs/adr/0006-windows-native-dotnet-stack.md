# ADR 0006 - Windows-native .NET implementation stack

- Status: Accepted
- Date: 2026-08-16

## Context

The project is a private Windows companion for Codex Desktop. The initial planning draft recommended Rust + Tauri, but the target machine currently has no Rust toolchain installed. It does have a working .NET SDK 7.0.102 plus Windows Desktop runtimes, Node.js, and the normal Windows process/UI APIs.

The project requires every module to be compiled and tested before the next module starts. Keeping Rust would therefore make the first implementation phase unverifiable on the actual target machine unless a new toolchain were installed first.

## Decision

Use a Windows-native C#/.NET implementation while preserving the architecture and module boundaries already accepted.

Core stack:

- C# / .NET 7 (`net7.0-windows` where Windows APIs are required)
- `System.Text.Json` for protocol/domain serialization
- `System.Diagnostics.Process` for Codex process and binary probing
- `Microsoft.Data.Sqlite` for Storage
- `Microsoft.Extensions.*` only where a framework facility is materially useful
- WPF + Win32 interop for the overlay/popover/settings UI
- xUnit for unit/integration tests

Project mapping:

- `CodexRouter.Domain`
- `CodexRouter.Protocol`
- `CodexRouter.Storage`
- `CodexRouter.Workers`
- `CodexRouter.Accounts`
- `CodexRouter.Routing`
- `CodexRouter.Rpc`
- `CodexRouter.Migration`
- `CodexRouter.Host`
- `CodexRouter.Diagnostics`
- `CodexRouter.Overlay`
- `CodexRouter.App`
- `CodexRouter.RouterCtl`

## Architectural consequences

The previous rules remain unchanged:

1. Router Core consumes stable domain models, never raw Codex JSON.
2. Codex credentials remain owned by Codex and the OS credential store.
3. Profiles stay isolated by `CODEX_HOME`.
4. Desktop integration must remain reversible.
5. Thread routing remains sticky.
6. Migration is explicit, never silent.
7. Unknown or incompatible Codex protocol versions fail closed for routing and can fall back to pass-through.

## Why this is not a scope reduction

This changes implementation language, not product scope. It removes an unavailable toolchain dependency and gives direct access to Windows UI/process APIs needed by the final product. Every module in `PROJECT_PLAN.md` remains required.
