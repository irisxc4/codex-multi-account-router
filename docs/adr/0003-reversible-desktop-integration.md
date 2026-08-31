# ADR 0003 - Reversible Desktop Integration

## Status

Accepted

## Context

Windows Codex Desktop 的 CLI 选择行为可观察到 `CODEX_CLI_PATH` 等路径，但它不是稳定公开的插件 SDK。直接修改 WindowsApps/MSIX 资源、Electron bundle、DLL 或内部文件虽然可能短期可用，但升级风险和恢复成本都过高。

## Decision

1. Desktop 接入独立放在 `host-integration` 模块，不进入 Router Core。
2. 每次安装接入前先 probe 当前 Codex Desktop / CLI 实际路径和版本。
3. 优先使用可逆的 CLI override/shim 方式。
4. `codex-route.exe` 只对 `app-server` 进入 Router；其他 CLI 命令完整 passthrough 到真实 Codex。
5. 接入前保存 previous configuration / real binary identity / install marker。
6. Codex update 后重新 probe；stale path 不静默继续。
7. compatibility failure 时 routing fail closed，并提供 pass-through/native recovery。
8. uninstall 必须恢复接入前状态。

## Hard Ban

禁止：

- 修改 `%ProgramFiles%\WindowsApps\...\Codex` 包内容。
- 替换官方资源文件。
- Electron JS patch。
- DLL injection / API hook。
- HTTPS token hook。
- 浏览器 Cookie 抓取。

## Consequences

### Positive

- Codex 更新时 blast radius 小。
- Router 可单独 repair/uninstall。
- 核心路由逻辑不依赖某个 Desktop 发行方式。

### Negative

- Host Integration 必须针对 Codex Desktop 版本做 probe。
- 某些版本若没有安全 override，可能只能进入 unsupported 状态，而不能强行接管。

## Recovery Invariant

任何时候都必须存在一条不依赖 Router 数据库正确性的恢复路径，让用户重新启动原生 Codex Desktop。
