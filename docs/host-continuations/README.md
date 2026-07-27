# Jint 隐式宿主 Continuation 扩展

## 1. 交付目标

本变更在 Jint 4.13.0 源码快照上增加一种**隐式宿主 continuation** 执行模式，使既有 JavaScript 可以继续使用同步表面语法调用实际异步的 C# 宿主操作：

```javascript
let answer = user.askInput("which one you want to load");
let resource = api.loadResource(answer);
let data = user.send(resource.content);
data;
```

宿主操作不阻塞线程；当操作未完成时，解释器保存逻辑 JavaScript 执行状态并退出当前执行片段。完成通知只携带 CLR 数据，随后通过用户提供的事件循环调度器回到**创建 Engine 的同一个 owner 线程**，把结果作为原调用表达式的返回值注入并继续执行。

## 2. 关键结论

- 原始脚本无需人工添加 `async`、`await`、Promise 或 callback。
- 等待期间不占用事件循环线程，也不使用 `.Wait()`、`.Result` 或嵌套消息泵。
- Jint Engine、`JsValue`、结果转换器和恢复执行均被约束在同一个 owner 线程。
- 异步完成线程只保存 CLR 结果/异常并调用 `IHostContinuationScheduler.Post`。
- 同步完成的 `ValueTask` 也强制经过后续事件循环 turn，避免重入。
- 普通函数嵌套、循环、调用参数、赋值、`try/catch/finally` 等路径复用 Jint 已有的 generator/async suspend-data 基础设施。
- 原生 ECMAScript Module 静态依赖图可通过 `ImportModuleWithHostContinuationsAsync` 使用同一模型；不转写为 CommonJS，当前明确拒绝 top-level await 依赖图。
- 这是一种**一次性恢复的宿主 effect 模式**，不是 Rhino 可复制、多次调用、可序列化的 first-class continuation 对象。

## 3. 实际验证状态

验证日期：2026-07-22。

| 项目 | 结果 |
|---|---:|
| Release 编译 | 0 warning，0 error |
| continuation 专项测试 | 23 passed，0 failed |
| Jint 完整测试回归 | 3648 passed，4 个项目原有 skip，0 failed |
| NativeAOT restore | 成功 |
| NativeAOT publish | 成功，生成 Linux x64 ELF |
| NativeAOT 执行 | `HOST_CONTINUATION_AOT_OK:sent:payload` |

完整证据位于审计证据包和以下仓库路径：

- `artifacts/audit-logs/final-build-tests.log`
- `artifacts/audit-logs/host-continuations-final.log`
- `artifacts/audit-logs/full-regression-final.log`
- `artifacts/audit-logs/aot-restore.log`
- `artifacts/audit-logs/aot-publish-final.log`
- `artifacts/audit-logs/aot-binary-file.log`
- `artifacts/audit-logs/aot-run-final.log`
- `Jint.Tests/TestResults/host-continuations-final.trx`
- `Jint.Tests/TestResults/full-regression-final.trx`

最终清洁源码包不包含 `artifacts`、`bin`、`obj` 和 `TestResults`；这些证据单独打包，避免把生成物混入源码审计范围。

## 4. 文档索引

- [DESIGN.md](DESIGN.md)：执行模型、状态机和核心不变量。
- [API-USAGE.md](API-USAGE.md)：宿主 API、调度器和完整使用示例。
- [THREADING-AND-LIFECYCLE.md](THREADING-AND-LIFECYCLE.md)：线程边界、取消、Dispose 和迟到完成。
- [SUPPORTED-SURFACE.md](SUPPORTED-SURFACE.md)：已支持、拒绝和未验证语义矩阵。
- [CHANGE-AUDIT.md](CHANGE-AUDIT.md)：逐文件、逐职责的修改审计表。
- [TEST-TRACEABILITY.md](TEST-TRACEABILITY.md)：需求到测试和证据的追踪关系。
- [OFFLINE-BUILD.md](OFFLINE-BUILD.md)：使用上传 SDK/NuGet 缓存的离线复现步骤。
- [NATIVEAOT-AUDIT.md](NATIVEAOT-AUDIT.md)：AOT 证据、警告和适用边界。
- [SECURITY-AND-RISKS.md](SECURITY-AND-RISKS.md)：安全约束、滥用风险和生产建议。
- [AUDIT-MANIFEST.md](AUDIT-MANIFEST.md)：基线、输入、交付物和哈希说明。

## 5. 审计判定边界

本交付证明的是：

1. 文档列明的同步外观脚本路径能够在指定 owner 线程上非阻塞挂起/恢复；
2. 新增路径通过专项测试、Jint 完整回归和一个真实 NativeAOT 二进制；
3. 改动可由完整补丁和 Git bundle 审阅与重放。

本交付**不声称**：

- Jint 全部反射式 CLR interop 已经做到 warning-free NativeAOT；
- 任意 ECMAScript 语义位置都可挂起；
- 与 Rhino first-class/multi-shot continuation API 二进制或对象模型兼容；
- 可以在多个线程之间迁移 Engine；
- 可以序列化 continuation 并跨进程或重启恢复。

生产启用前必须把业务脚本语法与调用边界限制在 [SUPPORTED-SURFACE.md](SUPPORTED-SURFACE.md) 明确列出的范围内。
