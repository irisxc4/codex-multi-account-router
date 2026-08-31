# ADR 0005 - Profile Materialization and Keyring Isolation

## Status

Accepted

## Context

多账户需要独立 `CODEX_HOME`。但 `CODEX_HOME` 不只承载认证，还承载 config、history、logs、cache 等状态。若每个账户创建完全空的 home，多个账户会变成多套不同 Codex 环境；若直接复制整个 home，又会把 auth/session 等私有状态一起复制。

当前 Codex 源码确认：Windows 的 encrypted-secrets key 由 canonical `CODEX_HOME` 路径计算 SHA-256 并派生 `secrets|<hash>`，因此不同 profile 的加密凭据天然隔离。OAuth 完整 payload 位于各 profile 的 `secrets/codex_auth.age` 加密文件，OS keyring 只保存短加密密钥。

旧的 direct keyring backend 会把完整 OAuth JSON 放入 Windows Credential Manager。Windows 对 generic credential blob 的上限是 2560 bytes，当前 ChatGPT OAuth payload 可能超过该限制，因此 direct backend 不再适用于官方浏览器登录。

Source:
- https://github.com/openai/codex/blob/main/codex-rs/login/src/auth/storage.rs
- https://github.com/openai/codex/blob/main/codex-rs/secrets/src/lib.rs
- https://github.com/openai/codex/issues/10353
- https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw

## Decision

### 1. 每账户独立 CODEX_HOME

```text
profiles/<account-id>/codex-home
```

### 2. 使用 Shared Template + Private State

共享层：

- 通用 config policy
- skills/plugin code policy
- Router 强制设置

私有层：

- auth/encrypted secrets
- sessions/history
- logs/cache
- account-specific overrides

### 3. Profile Materializer 负责生成

不允许直接复制整个原生 CODEX_HOME。

Materializer 必须：

- allowlist/denylist 配置字段。
- 记录 template version。
- 原子写入生成 config。
- 保留 account-specific overrides。
- 不读取/复制 OAuth token。
- 不覆盖私有 session/history。

### 4. 强制 keyring + encrypted secrets

Router 管理的 profile 默认强制：

```toml
cli_auth_credentials_store = "keyring"

[features]
secret_auth_storage = true
```

该组合要求 Codex CLI `>= 0.140.0`。OAuth payload 由官方 Codex 加密保存在 profile 内，只把短密钥写入 OS keyring。若当前 OS keyring 不可用，Account onboarding 失败并给出明确诊断，不自动退回 file 模式保存 token。

## Consequences

### Positive

- 多账户认证真正隔离。
- 不受 Windows Credential Manager 2560-byte payload 上限影响。
- 不产生明文 `auth.json` fallback。
- Codex 通用使用体验保持一致。
- 配置更新可集中治理。

### Negative

- 需要维护配置同步规则。
- 某些未来 Codex 配置可能同时具有账号/全局语义，需要逐字段分类。

## Rejected Alternatives

### 每个 profile 完全独立手工配置

拒绝。长期会产生严重 drift。

### 复制整个 `.codex`

拒绝。会复制认证/会话/缓存等私有状态。

### `cli_auth_credentials_store = "auto"`

拒绝作为默认。`auto` 在 keyring 不可用时会 fallback 到 file，不符合 Router profile 的凭据存储红线。

### Direct keyring backend

拒绝。完整 OAuth JSON 可能超过 Windows Credential Manager 的 2560-byte 上限；该模式仅保留为历史 AgentIdentity 兼容代码，不再用于官方 OAuth profile。
