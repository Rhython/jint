# 测试与需求追踪

## 1. 专项测试结果

```text
Failed: 0
Passed: 23
Skipped: 0
Total: 23
```

证据：`host-continuations-final.log`、`host-continuations-final.trx`。

## 2. 用例清单

| 测试 | 覆盖需求 |
|---|---|
| `ExecutesUnchangedSequentialWorkflowOnOwnerThread` | 原脚本不改；三次异步；snapshot/converter owner 线程 |
| `SynchronousHostCompletionStillResumesOnLaterTurn` | sync-complete ValueTask 也不 inline/re-enter |
| `ResumesNestedOrdinaryFunctionsAndMultipleEffectsInSameChild` | 普通函数嵌套；同一 child 多次挂起 |
| `DoesNotReplayResolvedCalleeOrEarlierArguments` | callee getter/先前参数副作用一次 |
| `NonCallableAfterSuspendedArgumentThrowsAndLeavesEngineUsable` | 参数恢复后错误语义；pool/Engine 可继续使用 |
| `PreservesArgumentsObjectAcrossSuspension` | `arguments` 对象与参数绑定生命周期 |
| `SupportsRepeatedSuspensionInsideLoop` | loop resume position 与累积状态 |
| `PreservesConstructorGetterAndArgumentsBeforeNew` | `new` callee/argument 状态不重放 |
| `InjectsHostFailureAtOriginalCallAndRunsFinallyOnce` | operation fault、catch、finally 一次 |
| `DrainsMicrotasksAtExternalSuspensionBoundary` | JS job/microtask 边界 |
| `GenericHostAndCompletionConvertersRunOnOwnerThread` | AOT 友好三阶段 converter 线程契约 |
| `CancellationAbandonsPendingFrameAndReleasesEngine` | cancel、迟到 completion、Engine 释放 |
| `EngineApisAreRejectedWhileRunIsSuspended` | owner/非 owner 外部 API guard |
| `DisposeFaultsPendingRunAndIgnoresLateCompletion` | Dispose lifecycle |
| `RejectsInlineScheduler` | scheduler later-turn 契约 |
| `WrongThreadSchedulerFaultsWithoutExecutingJavaScriptThere` | 错线程不执行 JS，Engine 隔离 |
| `RejectsUnauditedNativeBoundaries` ×5 | direct eval、indirect eval、Array.map callback、Function.call、class constructor body |
| `RejectsSuspensionDuringParameterInitializationAndAbandonsOperation` | 参数初始化 hard boundary 和 operation abandon |
| `DefaultResultConverterRejectsJsValueFromBackgroundContract` | 禁止异步边界携带 JsValue |

## 3. 完整 Jint 回归

```text
Failed: 0
Passed: 3648
Skipped: 4
Total: 3652
```

4 个 skip 为项目原有标记：

- `InteropExplicitTypeTests.CallObjectMethodHiddenByInterface`
- `BreakPointTests.DebuggerStatementAndBreakpointTriggerSingleBreak`
- `GenericMethodTests.TestGenericClassDeriveFromGenericInterface`
- `FlurlExtensionTest.CanUseFlurlExtensionMethods`

证据：`full-regression-final.log`、`full-regression-final.trx`。

容器系统 tzdata 只有 `Europe/Kyiv`，项目测试使用兼容名称 `Europe/Kiev`。最终回归使用 Python `tzdata` 包目录作为 `TZDIR`；这只影响时区测试数据查找，不改变 Jint 程序集。

## 4. NativeAOT smoke

Smoke 脚本保持同步语法：

```javascript
let answer = user.askInput("which one you want to load");
let resource = api.loadResource(answer);
let data = user.send(resource.content);
data;
```

检查项：

- 三个 CLR operation 使用 `Task.Delay`，非阻塞完成；
- request snapshot 全在 owner 线程；
- result converter 全在 owner 线程；
- final completion converter 在 owner 线程；
- scheduler callback 在 owner 线程；
- 最终结果为 `sent:payload`。

结果：

```text
HOST_CONTINUATION_AOT_OK:sent:payload
```

## 5. 尚需由业务补充的测试

通用 Jint 测试无法替代具体 Rhino 脚本差分验证。迁移项目应增加：

- Java Rhino 与此 fork 的同输入事件轨迹对比；
- 所有实际 `user`/`api` 方法的 DTO 和异常映射；
- 取消、超时、用户断连和重复完成；
- 大量长期 suspended workflow 的内存快照；
- 业务脚本使用的 getter、Proxy、iterator、module 边界检查；
- 每个发布 RID 的 NativeAOT smoke；
- 脚本升级/回滚时 continuation 尚未完成的处理策略。
