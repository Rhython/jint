# 设计方案

## 1. 问题定义

目标脚本将异步宿主操作写成普通调用：

```javascript
const value = host.operation(input);
use(value);
```

约束如下：

- `host.operation` 实际返回异步结果；
- 等待期间不能阻塞任何线程；
- Jint 和宿主线程敏感数据只能在一个固定事件循环线程访问；
- 原始脚本不能依赖作者正确添加 `await`；
- 需要保留现代 JavaScript 解析和执行能力；
- 运行时需要可由 .NET NativeAOT 发布。

普通 C# 异常无法满足要求：它会展开 CLR 调用栈，并触发普通函数环境清理。普通 Promise 也无法满足要求：未写 `await` 的脚本会得到 Promise 对象而非结果。因此必须让解释器的逻辑执行状态存在于堆对象中，并能在后续事件循环 turn 重新进入。

## 2. 设计原则

### 2.1 复用 Jint 已有 suspend-data

当前 Jint 已为 generator 和 async function 保存：

- statement-list 位置；
- 二元/条件表达式已求值的左操作数；
- 调用/数组参数缓冲区；
- 赋值左值及原值；
- member expression 的 base/this；
- 循环迭代器、block/catch 环境；
- object/template/tagged-template 的部分构造状态；
- `try/finally` 的 pending completion。

本实现新增 `HostContinuationFrame : ISuspendable`，使普通同步函数调用也能复用这些状态保存点。

### 2.2 每次普通函数调用一个逻辑 frame

AST 节点会被多个调用共享，递归和并发逻辑调用不能共享恢复字典。因此每次普通 `ScriptFunction` 调用创建独立 `HostContinuationFrame`，形成：

```text
Root script frame
  └─ ordinary function frame A
       └─ ordinary function frame B
            └─ pending HostOperation
```

每个 frame 独立拥有：

- `SuspendDataDictionary`；
- 保存的 `ExecutionContext`；
- 是否已完成参数/绑定初始化；
- `arguments` 对象生命周期；
- 当前挂起 operation 或 child frame；
- 恢复点节点身份；
- `finally` pending completion 字段。

### 2.3 异步边界只传 CLR 数据

调用宿主函数时：

1. 在 owner 线程执行 `requestSnapshot`，把 `JsValue` 输入转为不可变 CLR DTO；
2. 清空临时 `JsValue[]` 快照，禁止异步 operation 保留 JavaScript 对象；
3. `operation(TRequest, CancellationToken)` 可在任意线程完成，只返回 CLR `TResult` 或异常；
4. 完成线程调用 scheduler 的 `Post`，不得访问 Engine；
5. owner 线程执行 `resultConverter`，把 CLR 结果构造为 `JsValue`；
6. 结果注回原 `CallExpression`。

这是 NativeAOT 和线程安全的推荐接口：

```csharp
CreateHostContinuationFunction<TRequest, TResult>(
    string name,
    Func<JsValue, JsValue[], TRequest> requestSnapshot,
    Func<TRequest, CancellationToken, ValueTask<TResult>> operation,
    Func<Engine, TResult, JsValue> resultConverter,
    int length = 0)
```

## 3. 执行状态机

### 3.1 Run 状态

`HostContinuationRun` 一次拥有一个 Engine：

```text
Created
  │ initial synchronous slice
  ├───────────────► Completed / Faulted / Canceled
  │
  ▼
Suspended ── Post(later owner turn) ──► Resuming
   ▲                                      │
   └──────────── another host effect ─────┘
```

关键字段：

- `OwnerThreadId`：开始执行时捕获，不允许迁移；
- `_sliceRunning`：防止执行片段重入；
- `_ownerAccessDepth`：仅执行/清理片段临时开放 Engine API；
- `_resumeScheduled`：合并重复完成/取消唤醒；
- `_postInProgress`/`_postThreadId`：检测 scheduler inline 执行；
- `_completed`/`_cleaned`：一次性完成和幂等清理；
- `_deferredOwnerFailure`：当前 slice 内产生的 owner-side 调度错误延迟到安全退出点处理。

### 3.2 Frame 状态

```text
Running ── host call/child suspension ──► Suspended
   ▲                                          │
   └──────── PrepareResume + re-entry ────────┘
   │
   └──────────────────────────────► Completed
```

挂起时保存当前 `ExecutionContext`。恢复时重新压入该 context，并从 statement-list 与 expression suspend-data 记录的节点位置执行。

### 3.3 CallExpression 状态

`HostCallSuspendData` 分四阶段：

- `None`：未挂起；
- `Arguments`：callee、reference、`this` 已解析，参数列表求值期间挂起；
- `Operation`：直接宿主 operation 挂起；
- `Child`：普通脚本函数子 frame 挂起。

保存内容包括：

- callee reference；
- reference record；
- callable；
- `this`；
- operation 或 child frame。

因此恢复时不会重复执行：

- getter/proxy 形式的 callee 解析；
- 已执行的参数副作用；
- 宿主 operation；
- 已进入的普通函数前半段。

### 3.4 NewExpression 状态

当构造参数中发生挂起，`HostNewSuspendData` 保存已经解析的 constructor，恢复后不会重复执行 constructor getter。构造函数**体内**发生宿主挂起目前是硬拒绝边界，原因见限制文档。

## 4. 执行序列

```text
Owner thread             Jint                       I/O completion thread
    │                      │                                  │
    │ Evaluate...          │                                  │
    ├─────────────────────►│ execute script                   │
    │                      │ requestSnapshot(JsValue→DTO)     │
    │                      │ start ValueTask                  │
    │                      │ save frame/context               │
    │◄─────────────────────┤ return incomplete Task           │
    │ event loop free      │                                  │
    │                      │                      I/O completes│
    │                      │◄─────────────────────────────────┤
    │                      │ scheduler.Post(callback)         │
    │◄────────────────────────────────────────────────────────┤
    │ later event turn     │                                  │
    ├─────────────────────►│ verify same owner thread         │
    │                      │ resultConverter(DTO→JsValue)     │
    │                      │ inject at original call          │
    │                      │ continue script                  │
```

## 5. 异常语义

- operation fault：在 owner 线程调用 `Throw.FromClrException`，表现为原宿主调用抛出的 JavaScript 异常；
- result converter fault：完成 Task fault，并清理逻辑 frames；
- JavaScript `catch`：可捕获 operation 异常；
- JavaScript `finally`：由 Jint suspendable pending-completion 机制保证在真实离开作用域时执行，而不是在外部等待期间提前执行；
- scheduler 错线程/inline：不在错误线程继续执行 JavaScript，完成 Task fault；
- cancellation：下一次 owner-thread 唤醒进入取消清理；
- Dispose：owner 线程终止 pending run，迟到完成被忽略。

## 6. 微任务边界

当脚本因外部宿主 operation 返回控制权时，当前 JavaScript job 已结束。本实现先在 owner 线程调用 `RunAvailableContinuations()` 排空当前可用 Jint microtasks，再等待外部事件循环恢复。专项测试验证 Promise microtask 在外部恢复前按该边界执行。

这不是浏览器或 Node 的完整事件循环模拟；宿主仍需定义自身宏任务/事件 turn 规则，并确保 `Post` 总是排队而非 inline。

## 6.1 Module 执行

`ImportModuleWithHostContinuationsAsync` 仍通过 Jint 原生 Module Record 完成解析、链接、依赖遍历、import binding 和 namespace 创建。无 top-level await 的 `SourceTextModule` body 使用 root `HostContinuationFrame` 作为 suspendable；依赖 body 挂起时，当前模块求值 DFS 的活动记录暂时回到 `Linked`，但已完成的依赖保持 `Evaluated`。后续 owner turn 从保存的模块 `ExecutionContext` 和 statement-list 位置重新进入同一原生求值图，不经过同步 `DrainEventLoopUntilSettled`。

模块 body 正常结束后才执行环境资源清理并提交 `Evaluated` 状态。取消、Dispose 或迟到 operation completion 仍由同一个 run/frame 清理路径处理。top-level await 使用另一套 async-module promise 状态机，当前入口在执行前拒绝包含它的整个依赖图，避免两套 suspension 所有权交叉。

模块 body 或活动依赖图失败时，求值错误会按原生 Module Record 语义提交到当时活动的模块记录；以后再次导入 entry、失败依赖或失败循环中的成员都会传播同一失败，不会把已失败记录当作成功 namespace 返回。语句约束或 CLR fault 等非 JavaScript completion 同样作为该图的终态故障缓存。body 已进入的模块 `ExecutionContext` 在这些直接异常路径上仍会退出，因此 run 清理后 Engine 可继续执行不依赖该失败图的代码，但失败图本身不可重试。

## 7. Engine 所有权

一个 Engine 同时最多有一个 active `HostContinuationRun`。run 未完成期间：

- 普通外部 Engine API 被拒绝；
- 只有 run 的执行 slice 和 owner cleanup scope 临时开放；
- 后台完成线程永远不开放；
- 错线程投递导致 Engine 隔离，必须由真实 owner 线程 Dispose。

## 8. 与 Rhino continuation 的差异

| 能力 | 本实现 | Rhino continuation |
|---|---|---|
| 普通同步表面语法调用异步宿主 | 支持 | 支持 |
| 单次挂起后恢复 | 支持 | 支持 |
| continuation 作为 JS first-class 对象 | 不支持 | 支持相关 API |
| 同一 continuation 多次恢复 | 不支持 | 可支持 multi-shot 场景 |
| continuation 克隆/序列化 | 不支持 | 取决于 Rhino 使用方式 |
| 跨任意 native frame | 明确限制 | Rhino 本身也有限制 |
| 固定 C# owner 线程 | 强制 | 由 Java 宿主策略决定 |

因此审计名称使用“隐式宿主 continuation”，避免误导为 Rhino API 的逐项移植。
