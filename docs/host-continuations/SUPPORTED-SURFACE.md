# 支持面与限制矩阵

## 1. 已测试支持

| 场景 | 状态 | 证据 |
|---|---|---|
| 顶层连续多个宿主异步调用 | 支持 | `ExecutesUnchangedSequentialWorkflowOnOwnerThread`、AOT smoke |
| 同步完成宿主 operation later-turn 恢复 | 支持 | `SynchronousHostCompletionStillResumesOnLaterTurn` |
| 多层普通函数嵌套 | 支持 | `ResumesNestedOrdinaryFunctionsAndMultipleEffectsInSameChild` |
| 同一 child frame 多次挂起 | 支持 | 同上 |
| 调用 callee getter/先前参数副作用不重放 | 支持 | `DoesNotReplayResolvedCalleeOrEarlierArguments` |
| 参数挂起后发现 callee 非函数 | 支持并保持 Engine 可用 | `NonCallableAfterSuspendedArgumentThrowsAndLeavesEngineUsable` |
| `arguments` 对象跨挂起 | 支持 | `PreservesArgumentsObjectAcrossSuspension` |
| 循环内重复挂起 | 支持 | `SupportsRepeatedSuspensionInsideLoop` |
| `new` 的 constructor getter 和已求值参数不重放 | 支持 | `PreservesConstructorGetterAndArgumentsBeforeNew` |
| operation 异常注入原调用点 | 支持 | `InjectsHostFailureAtOriginalCallAndRunsFinallyOnce` |
| `catch/finally`，finally 恰好一次 | 支持 | 同上 |
| 外部挂起边界前排空可用微任务 | 支持 | `DrainsMicrotasksAtExternalSuspensionBoundary` |
| 强类型 request/result/final converter owner 线程 | 支持 | `GenericHostAndCompletionConvertersRunOnOwnerThread`、AOT smoke |
| Cancellation 清理和迟到完成忽略 | 支持 | `CancellationAbandonsPendingFrameAndReleasesEngine` |
| suspended Engine API 拒绝 | 支持 | `EngineApisAreRejectedWhileRunIsSuspended` |
| Dispose pending run | 支持 | `DisposeFaultsPendingRunAndIgnoresLateCompletion` |
| inline scheduler 拒绝 | 支持 | `RejectsInlineScheduler` |
| 错线程 scheduler 不执行 JS | 支持 | `WrongThreadSchedulerFaultsWithoutExecutingJavaScriptThere` |
| 默认 converter 拒绝后台携带 `JsValue` | 支持 | `DefaultResultConverterRejectsJsValueFromBackgroundContract` |
| 原生 Module 无挂起及 `Prepared<Module>` | 支持 | `ImportsPreparedModuleWithoutSuspension` |
| Module 同步/异步完成 host operation | 支持 | `ModuleSynchronousHostCompletionUsesLaterOwnerTurn`、`ModuleDependencyGraphSuspendsAndResumes` |
| 原生静态依赖图中的 module body 挂起 | 支持 | `ModuleDependencyGraphSuspendsAndResumes` |
| wrapper module 调用 imported default function 后挂起 | 支持 | `WrapperModuleCanInvokeImportedDefaultFunctionThatSuspends` |
| Module run overlap、取消与迟到完成 | 支持 | `ModuleRunRejectsOverlapAndCancellationIgnoresLateCompletion` |

## 2. 明确拒绝边界

以下路径在运行时主动报错，不会静默产生错误语义：

| 边界 | 原因 |
|---|---|
| direct `eval(...)` | 动态代码和 active lexical environment 的可恢复状态尚未审计 |
| indirect eval | 同上，且 realm/scope 语义不同 |
| 默认参数/解构参数初始化期间挂起 | 普通函数 bindings 尚未完成，现有 replay 边界不足以保证一次性副作用 |
| class constructor 函数体内挂起 | constructor 的 `this` 初始化、derived return/super 和实例元素语义尚未状态化 |
| `Array.prototype.map` 等同步 native callback 内挂起 | native builtin 调用帧未保存，无法从 callback 返回点恢复 |
| `Function.prototype.call/apply` 内部转调 | 该 native 转发边界尚未转换为可恢复状态机 |
| native stack 耗尽后的 StackGuard 迁移 | 会切到线程池，违反 owner-thread 不变量 |
| 普通 `Evaluate` 中直接调用 `HostContinuationFunction` | 没有 active run/frame 可保存 |
| 显式 JS async function/generator 中调用隐式 host function | 两套 suspendable 状态机会冲突，当前拒绝 |
| Module 依赖图中的 top-level `await` | async-module promise 状态机与隐式 frame 的恢复所有权尚未组合；执行 module body 前拒绝 |

## 3. 未验证，生产中应暂时禁止

以下场景没有专项证据，不应仅凭“可能工作”纳入生产支持：

- script getter/setter 函数体内部挂起；
- Proxy trap 内挂起；
- `valueOf`、`toString`、比较器、iterator protocol 等由 builtin 同步回调脚本的路径；
- `super()` 参数或 class field initializer 中挂起；
- optional call/chain 的所有复杂组合；
- tagged template tag 内嵌 native callback 后挂起；
- dynamic `import()` 与隐式 host effect 的组合；
- ShadowRealm；
- explicit resource management (`using`/`await using`) 与隐式 host effect 交叉；
- 极深递归、tail-call 相关路径；
- 并行从多个外部 task 等待同一 Engine；
- continuation 状态序列化或跨进程恢复。

建议在脚本加载阶段进行 AST 规则检查，拒绝业务允许范围之外的宿主 effect 调用位置。

## 4. 与现代 ECMAScript 的关系

Jint 原有 parser/runtime 仍负责现代 ECMAScript 语法。新增机制没有把脚本降级为 ES5，也没有转换源码。但“Jint 支持某语法”不自动等于“该语法中的任意子表达式可隐式挂起”。支持判定以本矩阵和新增测试为准。

## 5. 一次性语义

每个 `HostOperation`：

- 只接受第一个完成；
- 只允许消费一次；
- run 结束后不可恢复；
- 不提供 continuation object 给 JavaScript；
- 不支持多次 resume 同一捕获点。

依赖 Rhino multi-shot continuation 的脚本不能直接迁移到本实现。
