# ADR 0002 - Thread Sticky and Explicit Migration

## Status

Accepted

## Context

Codex Thread 是持久会话实体，包含历史、turn、工具状态、审批、运行环境与本地状态。仅因为另一个账号额度更高，就在同一个 thread 生命周期中把后续请求静默切到另一个 AppServer，会破坏 owner 一致性，也让故障不可解释。

## Decision

### 1. 新 Thread 只路由一次

`thread/start` 时：

```text
Router Core choose account
-> selected worker thread/start
-> receive threadId
-> persist thread_routes
```

之后该 thread 所有 thread-scoped / turn-scoped 请求都使用持久化 owner。

### 2. Quota 变化不改变已有 owner

账号进入 `Draining`：

- 已有 Thread 继续。
- 新 Thread 不再分配。

账号进入 `Cooldown` / `AuthRequired`：

- 不给新 Thread。
- 已有 Thread 按实际可恢复性显式报状态，不偷偷切号。

### 3. Fork 默认继承 source owner

`thread/fork` 是同一历史的派生，默认在 source worker 上完成。

### 4. 跨账户继续使用 Migration

迁移必须：

```text
Source Thread A / Account A
-> Snapshot
-> Destination Account B
-> New Thread B
-> Persist Linkage
```

不得直接把：

```text
thread_routes[A] = B
```

### 5. Migration 必须显式

用户通过 UI/命令触发，界面显示 `A -> B`，不伪装成原 Thread 未变化。

## Consequences

### Positive

- Thread owner 可推理、可审计。
- turn/interrupt/approval/tool event 不会跨 worker 串线。
- 额度路由只影响新工作，不破坏进行中工作。

### Negative

- 额度耗尽时不能无感续跑。
- Migration Engine 需要额外 snapshot 和新 Thread 创建流程。

## Rejected Alternatives

### 每个 turn 都重新选择最佳账号

拒绝。破坏 thread state owner。

### 额度耗尽时直接修改 sticky mapping

拒绝。DB owner 与 AppServer 实际 thread state 不一致。

### 自动迁移且不提示

拒绝。不可解释，且可能改变执行环境/授权语义。
