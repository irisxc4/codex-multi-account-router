# ADR 0007 - Quota Freshness and Router Control

## Status

Accepted

## Context

Codex may return more than one rate-limit snapshot. The legacy `rateLimits`
field represents the general Codex limit when a `codex` bucket exists, while
`rateLimitsByLimitId` preserves model-specific limits. Response order is not a
business rule. Treating the first short and long windows as the account total
can therefore display 100% even when the general weekly quota is lower.

Cached quota also becomes unsafe for routing if it is allowed to expire without
a refresh path. A failed or missing read must never be represented as 100%.

Desktop integration and runtime routing mode are separate recovery mechanisms,
but exposing both as peer switches makes an enabled Auto mode appear effective
when Codex Desktop is not connected to the shim.

## Decision

1. A full `account/rateLimits/read` response is the quota baseline.
2. `account/rateLimits/updated` remains a sparse update and is merged into the
   latest baseline; an invalid update triggers a full read.
3. Quota reads are single-flight per account. New-thread routing refreshes
   missing or stale quota before selecting an account. UI refresh runs in the
   background and preserves the last known good snapshot on failure.
4. The general display bucket is `limitId == "codex"`; only when absent may the
   UI use a deterministic fallback. Model-specific limits stay named and
   separate.
5. Route scoring always includes the general Codex constraint. When a requested
   model matches a named limit, that limit is an additional constraint. The
   effective headroom is conservative; unrelated buckets are never averaged to
   inflate a score.
6. The primary UI exposes one `Multi-account routing` control. Turning it on
   enables Auto mode and, when needed, configures reversible Desktop integration.
   Turning it off changes runtime mode to pass-through without deleting the
   integration marker. Removing Desktop integration remains an advanced recovery
   action and still requires a Codex restart.
7. Automatic routing selects the owner only for a new thread. Existing threads
   remain sticky. Quota or authentication failure may offer an explicit one-click
   migration, but must not silently mutate ownership.

## Failure Semantics

- Refresh failure: keep last known good data, mark it stale, show retry status.
- Missing quota: show unknown (`-`), never 100%.
- Stale quota before route: refresh once; if no fresh eligible account remains,
  fail closed with a diagnosable reason.
- Concurrent refresh: callers await one account-scoped operation.
- Desktop integration conflict: do not overwrite an external `CODEX_CLI_PATH`.
- Migration failure: preserve the source thread and expose retry/cancel.

## Upstream Basis

OpenAI Codex app-server exposes `account/rateLimits/read`,
`rateLimitsByLimitId`, and sparse `account/rateLimits/updated` notifications.
The official app-server selects the `codex` limit for its backward-compatible
single-bucket view before falling back to another entry. The official TUI uses
background startup/status refreshes and routes results back through its event
loop. This project follows those protocol semantics and adds routing-time
freshness because quota directly controls account selection here.

## Consequences

Quota shown in the overlay and quota used by the router now share the same
selection rules. Manual refresh remains a secondary recovery action. Internal
integration and route states remain independently recoverable even though the
primary UI orchestrates them as one user action.
