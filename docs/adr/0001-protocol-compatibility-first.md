# ADR 0001 - Protocol Compatibility First

## Status

Accepted

## Context

Codex AppServer 协议持续演进，而且本机 Codex 当前为 `0.149.0-alpha.4.1`。项目如果直接把某一版 README、issue 或手写 JSON shape 固化进 Router Core，Codex 更新后很容易出现 silent breakage。

## Decision

1. `protocol-compat` 是第一个实现模块。
2. 每个真实 Codex binary 先记录 path/version/file identity。
3. 使用该 binary 的 `app-server generate-json-schema` 生成 schema。
4. 默认只使用 stable surface。
5. experimental surface 必须由单独 ADR 批准。
6. Router Core 只依赖项目内部 Domain，不读取 raw Codex JSON shape。
7. Codex binary identity 改变后必须重新 compatibility probe。
8. 必需 RPC 缺失时 fail closed：禁用 routing，允许 diagnostics / pass-through recovery。
9. `-32001 Server overloaded` 作为明确 retryable protocol condition 建模。

## Required RPC Baseline

```text
initialize
thread/start
thread/resume
thread/fork
thread/list
turn/start
turn/interrupt
account/read
account/login/start
account/rateLimits/read
```

后续若项目功能依赖新增 RPC，必须更新 compatibility registry 和本 ADR/后续 superseding ADR。

## Consequences

### Positive

- Codex 更新风险集中在一层。
- Router 算法、Storage、UI 不被上游字段名污染。
- 可以明确判断 compatible/degraded/incompatible。

### Negative

- 初期代码量比直接写 JSON-RPC proxy 大。
- 需要维护 generated schema cache 与 adapter fixtures。

## Rejected Alternatives

### 直接依据 README 手写请求/响应类型

拒绝。文档和本机 binary 版本可能不同。

### 直接把 generated types 散布到所有 crates

拒绝。会让上游协议成为整个项目的隐式公共 API。

### 遇到未知字段/方法尽量猜着透传

拒绝。核心 route ownership 不能靠猜。
