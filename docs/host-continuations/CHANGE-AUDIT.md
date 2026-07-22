# 修改内容审计

## 1. 基线

- 输入归档：`jint-4.13.0.zip`
- ZIP comment / 上游 revision：`c244f9281795738e68df321ceb584c57935c9c66`
- 输入归档 SHA-256：`99e3dbd3e98c01ce496e82673da4c1c75e748801de8e0d635fae9d43598f63ab`
- 本文表中的“新增/修改”均相对此基线计算。
- 最终完整逐行差异以交付物 `jint-host-continuations.patch` 为准；Git bundle 包含 baseline commit 和 change commit。

## 2. 运行时代码

| 文件 | 类型 | 修改职责 | 主要审计关注点 |
|---|---|---|---|
| `Jint/Engine.HostContinuations.cs` | 新增 | public Evaluate overload、run 创建、slice 执行、挂起/恢复/完成/失败、Engine 所有权 | execution context 是否只在 owner 线程进出；异常/cleanup 是否幂等；microtask 边界 |
| `Jint/Runtime/Continuations/IHostContinuationScheduler.cs` | 新增 | 定义 owner event-loop scheduler 契约 | Post 必须 later-turn 且非 inline；不可迁移 managed thread id |
| `Jint/Runtime/Continuations/HostContinuationFunction.cs` | 新增 | 宿主可挂起函数、handler/result converter 委托 | handler 返回后不得保留 JsValue；默认 converter 的 AOT 风险 |
| `Jint/Runtime/Continuations/HostContinuationState.cs` | 新增 | frame、operation、run、call/new suspend-data 状态机 | 原子完成；一次消费；cancel/dispose；frame 递归清理；inline/错线程检测 |
| `Jint/Runtime/Environments/ExecutionContext.cs` | 修改 | execution context 增加 `HostContinuationFrame`，纳入 `Suspendable` | 所有 Update 方法必须保留新字段，避免环境切换丢 frame |
| `Jint/Native/Function/Function.cs` | 修改 | `PrepareForOrdinaryCall` 在 active run 下创建 child frame | 只对直接普通脚本函数链传播；native boundary 不误继承 |
| `Jint/Native/Function/ScriptFunction.cs` | 修改 | 普通函数挂起时保存 context/environment；恢复 suspended function；真正完成后再释放资源 | 不得在挂起时回收 slots/env；`arguments` 生命周期；class constructor 硬边界 |
| `Jint/Runtime/Interpreter/JintFunctionDefinition.cs` | 修改 | 普通 host-continuation body 初始化/恢复路径 | 参数绑定只执行一次；参数初始化挂起必须拒绝并清理 |
| `Jint/Runtime/Interpreter/Expressions/JintCallExpression.cs` | 修改 | callee/this/参数/operation/child frame 的挂起与恢复；eval/native boundary 检查 | 不重放副作用；reference/argument pool 释放一次；异常注入原调用点 |
| `Jint/Runtime/Interpreter/Expressions/JintNewExpression.cs` | 修改 | 参数挂起时保存已解析 constructor | getter/proxy callee 不得二次观察；constructor body 仍拒绝 |
| `Jint/Runtime/SuspendData.cs` | 修改 | 增加 `ClearAll` 供 run abort/complete 释放全部状态 | 不能遗留 iterator/environment/reference；不影响既有 generator 路径 |
| `Jint/Engine.Advanced.cs` | 修改 | host function factory；Advanced API owner/suspended guard | 泛型 factory 的 request/result 边界；所有 exposed Advanced API 防护 |
| `Jint/Engine.cs` | 修改 | public Engine API guard；Dispose pending run | 外部重入/错线程拒绝；普通无 active run 行为保持不变 |

## 3. 测试和 AOT 示例

| 文件 | 类型 | 内容 |
|---|---|---|
| `Jint.Tests/Runtime/HostContinuationTests.cs` | 新增 | 23 个执行用例，覆盖连续操作、嵌套、参数副作用、循环、异常/finally、微任务、线程、取消、Dispose 和拒绝边界 |
| `Jint.HostContinuations.AotSmoke/Jint.HostContinuations.AotSmoke.csproj` | 新增 | net10.0 NativeAOT smoke；保留 Jint root assembly；报告但不提升既有 AOT 警告为 error |
| `Jint.HostContinuations.AotSmoke/Program.cs` | 新增 | 使用原始同步脚本执行三个真实异步 CLR operation，并检查 snapshot/converter/resume 全部 owner-thread |

## 4. 离线构建兼容层

| 文件 | 类型 | 内容 | 是否影响默认上游构建 |
|---|---|---|---|
| `Directory.Build.targets` | 新增 | 仅当 `OfflineUseSdkRoslyn=true` 时使用 SDK Roslyn，并选择缓存内 10.0.9 runtime/AOT/ILLink packs | 否，默认条件不成立 |
| `Jint.SourceGenerators/Jint.SourceGenerators.csproj` | 修改 | opt-in 模式将 source generator target 改为 net10.0，以匹配 SDK Roslyn 5.0 host | 默认仍是原 netstandard2.0 |
| `Jint.slnx` | 修改 | 把 AOT smoke project、入口文档和 targets 加入解决方案导航 | 不改变运行时 |
| `HOST_CONTINUATIONS.md` | 新增 | 根目录入口，指向完整文档和复现命令 | 无 |
| `eng/host-continuations/offline-build.sh` | 新增 | 生成临时 NuGet config、restore/build/test/AOT publish/run | 仅审计复现工具 |
| `eng/host-continuations/NuGet.offline.config.example` | 新增 | 离线 global package cache 配置模板 | 无 |

## 5. 审计文档

以下文件全部新增，属于本交付的审计材料：

- `docs/host-continuations/README.md`
- `docs/host-continuations/DESIGN.md`
- `docs/host-continuations/API-USAGE.md`
- `docs/host-continuations/THREADING-AND-LIFECYCLE.md`
- `docs/host-continuations/SUPPORTED-SURFACE.md`
- `docs/host-continuations/TEST-TRACEABILITY.md`
- `docs/host-continuations/OFFLINE-BUILD.md`
- `docs/host-continuations/NATIVEAOT-AUDIT.md`
- `docs/host-continuations/SECURITY-AND-RISKS.md`
- `docs/host-continuations/AUDIT-MANIFEST.md`
- `docs/host-continuations/CHANGESET.csv`（由最终源树生成）

文档本身也包含在完整 patch/Git bundle 中，因此其任何后续修改都可审计。

## 6. 逐类/逐方法变化摘要

### `ExecutionContext`

- 构造器新增可选 `HostContinuationFrame?`。
- 新增 readonly 字段 `HostContinuationFrame`。
- `Suspendable` 优先顺序扩展为 generator / async function / async generator / host frame。
- 所有 `Update*` 方法传递 host frame，防止 lexical/private environment 更新时丢失。

### `ScriptFunction`

- active implicit run 中普通脚本调用创建 per-invocation logical child frame。
- 挂起时保存 execution context，不执行普通返回清理和 environment pooling。
- 新增恢复入口，重用原 function definition/body 和保存的 bindings。
- 参数/arguments 对象只初始化一次。
- class constructor body 检测到 implicit suspension 时主动报错。

### `JintCallExpression`

- native stack guard 路径在 implicit run 中拒绝线程迁移。
- direct/indirect eval 在 implicit chain 中拒绝。
- 参数挂起时保存 resolved callable/reference/this。
- 调用 `HostContinuationFunction` 后领取 pending operation，保存 stage 并向上传播 suspension。
- 普通子函数挂起时领取 child frame，保存 stage 并向上传播。
- 恢复时 operation 转换为 `JsValue` 或恢复 child frame，随后继续原调用表达式。
- 引用与参数池在完成/异常/挂起各路径严格按所有权释放。

### `HostContinuationRun`

- 捕获 owner thread id。
- 调用 handler 前复制参数；handler 返回后清空数组。
- sync/async operation 统一使用 scheduler later-turn。
- 原子合并 resume、cancel 和重复完成。
- 检测 inline scheduler 和 wrong-thread callback。
- 完成 converter 在 owner 线程执行。
- cleanup 递归 abort frames，迟到完成不再投递。

## 7. NativeAOT 调试中发现并修复的实现问题

早期版本使用集合表达式复制参数：

```csharp
argumentSnapshot = [.. arguments];
```

在上传的 .NET SDK 10.0.100 与离线 10.0.9 runtime/AOT pack 组合中，生成的 NativeAOT 程序在第一次宿主调用触发 runtime `RhFailFast`。最终代码改为显式数组分配和索引复制：

```csharp
argumentSnapshot = new JsValue[arguments.Length];
for (var i = 0; i < arguments.Length; i++)
{
    argumentSnapshot[i] = arguments[i];
}
```

修改后同一 native binary 场景成功输出 `HOST_CONTINUATION_AOT_OK:sent:payload`。该问题和修复记录在 AOT 审计文档与最终补丁中。

## 8. 基线换行保持规则

最终 `.gitattributes` 为上传归档中原本采用 CRLF 的 5 个既有文件增加 `-text` 规则。文件正文没有被修改；此项只防止 Git 在 archive、bundle 或 checkout 时把未修改文件规范化为 LF，从而保证完整 patch 应用于输入 ZIP 后，与交付源码树字节一致。涉及路径已在 `.gitattributes` 中逐项列明。

## 9. 生成物不属于源码修改

以下目录仅为构建/验证证据，不纳入源码 patch：

- `artifacts/`
- `**/bin/`
- `**/obj/`
- `Jint.Tests/TestResults/`
- 本机绝对路径版 `NuGet.offline.config`

审计证据包单独保存这些日志/TRX/NativeAOT 输出信息。
