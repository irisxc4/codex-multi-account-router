# ADR 0004 - Front Account Projection and Separate Control Plane

## Status

Accepted

## Context

Codex Desktop 连接的是一个 AppServer，并按照当前 generated schema 解析 `account/read`、`account/rateLimits/read` 等响应。Router 内部虽然维护多个账户，但不能把自定义账号数组或 Router 字段塞进原生 AppServer response，否则 Desktop 与 Router 的协议契约会立即分叉。

## Decision

### 1. Desktop AppServer surface 始终保持原生 schema

所有 Desktop-facing account RPC 返回一个合法单账号响应。

### 2. Router 定义 Front Account Projection

投影账户按以下优先级选择：

```text
active/focused thread owner（能可靠识别时）
-> manual pin
-> last successful routed account
-> default healthy account
```

如果没有合法 projection，不伪造账户，而是返回当前协议允许的未登录/错误状态。

### 3. 多账户信息只走 Router Control Plane

Overlay/routerctl 获取：

- accounts[]
- all quota snapshots
- health states
- worker states
- route mode
- migration state

这些数据不进入 Codex 原生 AppServer schema。

### 4. 原生 notification 同样保持 schema

Desktop-facing `account/*` notification 只反映当前 front projection 的合法原生事件。其他账户更新进入 Control Plane event bus，不广播给 Desktop 假装它们属于同一个账户。

## Consequences

### Positive

- 不破坏 Codex Desktop 的协议解析。
- Router 私有能力和上游 AppServer 解耦。
- Overlay 可以自由发展多账户 UI，而无需污染原生协议。

### Negative

- Desktop 原生账户 UI 只能看到一个 projection，不能代表全部账户。
- 需要维护 projection 切换与 account notification 过滤逻辑。

## Rejected Alternative

### 在 `account/read` 中附加 `accounts: []`

拒绝。它不是当前 Codex schema 的一部分，也会把 Router 私有协议泄露到原生 Desktop data plane。
