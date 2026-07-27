# 线程、所有权与生命周期

## 1. 不变量

运行时强制以下不变量：

1. `EvaluateWithHostContinuationsAsync` 必须在 scheduler owner 线程启动。
2. run 捕获 `Environment.CurrentManagedThreadId`，之后不允许迁移。
3. 每个恢复 callback 同时检查 managed thread id 与 `scheduler.CheckAccess()`。
4. operation 完成线程不能调用 Engine、读取 `JsValue` 或访问 thread-affine `user` 数据。
5. Engine 在外部等待期间处于 suspended/owned 状态，外部 API 被拒绝。
6. 一个 Engine 同时只能有一个 implicit run。
7. scheduler `Post` 必须 later-turn、非 inline。
8. operation completion 只能消费一次；清理后迟到完成被忽略。
9. Module API 的解析、链接、namespace 转换和每个 module body slice 都在同一个 owner 线程执行。

## 2. Engine API 防护

防护接入：

- `ExecuteWithConstraints`，覆盖主要 Execute/Evaluate/Invoke 路径；
- 所有公开 `SetValue` overload；
- 公开 `GetValue` overload；
- `RunAvailableContinuations`；
- Advanced API：stack trace、reset call stack、task processing、manual promise、proxy、host function factory；
- `Dispose`。

当 active run 存在：

```text
错误线程调用             → owner-thread violation
owner 线程但 run suspended → Engine suspended violation
run 的执行/清理 scope      → 临时允许
```

防护不是对 Jint 每一个 internal helper 的形式化 capability 系统；它覆盖公开入口及本实现使用的核心入口。生产封装仍应避免把 Engine 引用泄漏给其他组件。

## 3. Operation 完成

`HostOperation` 使用原子字段管理：

- `_completion`：第一个 CLR result/exception 获胜；
- `_consumed`：防止重复注入；
- `_abandoned`：取消/Dispose 后拒绝后续调度；
- cancellation registration：取消时只请求 owner-thread resume/cleanup。

对于同步完成的 `ValueTask`，虽然可以立即读取结果，但 `TryComplete` 仍只安排 scheduler callback，不在当前宿主调用栈恢复 JS。

## 4. 错线程 scheduler

如果 scheduler 在错误线程执行 callback：

- JavaScript 不会在那里继续；
- completion task 失败；
- 因无法在错误线程安全清理 Engine 内部 frame，Engine 进入隔离状态；
- 真实 owner 线程必须调用 `Dispose`。

这是故障隔离策略，不是自动线程迁移。

## 5. Inline scheduler

运行时在 `Scheduler.Post` 调用期间记录 `_postInProgress` 和发起线程 id。若 callback 在同一 `Post` 栈上执行，run 失败并报告 inline 违规。即使 operation 同步完成，也必须通过事件队列进入下一 turn。

## 6. Cancellation

取消 token callback 可以在任意线程运行。它只设置 `_cancellationRequested` 并调用 `ScheduleResume`。真正的：

- call stack reset；
- frame abort；
- `arguments` 生命周期结束；
- suspend-data 清除；
- completion task cancellation；

均在 owner 线程执行。

当前完成 task 使用 `TrySetCanceled()`，不会保留原 token 实例；调用者应按 canceled task 处理，而不要依赖 token identity。

Module run 的取消与 script run 相同：活动 module `ExecutionContext`、root frame、依赖 body 的 statement-list 恢复位置和 pending operation 都只在 owner cleanup turn 释放。取消后 module record 不应通过普通 `Modules.Import` 继续使用；应释放该 Engine，或把该 Engine 视为仅用于终止后的普通非模块工作。迟到完成不会重新进入 module graph。

## 7. Dispose

Owner 线程 Dispose pending run：

1. 验证当前不在执行 slice；
2. 用 `ObjectDisposedException` 终止 run；
3. 递归 abort root/child frames；
4. abandon pending operations；
5. 清理 run 对 Engine 的所有权；
6. 执行普通 Engine Dispose。

迟到的 operation completion 检测 `_abandoned` 后不再 `Post`。

## 8. JavaScript 环境池

Jint 普通函数通常在 `finally` 中释放 execution context，并可能复用 function environment/slot array。本实现挂起时：

- 不把 frame environment 放回复用池；
- 保存完整 `ExecutionContext`；
- 恢复后继续同一 logical invocation；
- 只有真正完成或 abort 后才结束 `arguments` 和 suspend-data 生命周期。

这是不能用普通异常模拟 continuation 的核心原因之一。

## 9. StackGuard 限制

Jint 的 `StackGuard.RunOnEmptyStack` 会在线程池栈执行并同步等待。implicit run 中这会违反固定 owner 线程，因此在 native stack 耗尽时明确拒绝，而不是迁移。应用需要设置合理递归深度并避免依赖极深 CLR 递归。
