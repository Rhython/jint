# API 与使用指南

## 1. Owner 线程调度器

宿主必须实现：

```csharp
public interface IHostContinuationScheduler
{
    bool CheckAccess();
    void Post(Action callback);
}
```

契约：

- `CheckAccess()` 只有在 Engine 的固定 owner 事件循环线程上返回 `true`；
- `Post()` 可从任意线程调用；
- `Post()` 必须排入**后续** owner-thread turn；
- `Post()` 绝不能 inline 调用 callback；
- callback 必须在最初调用 `EvaluateWithHostContinuationsAsync` 的同一 managed thread id 上运行；
- 不允许把工作交给任意线程池线程后仅伪造 `CheckAccess=true`。

一个最小单线程调度器示例：

```csharp
public sealed class OwnerLoopScheduler : IHostContinuationScheduler, IDisposable
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly AutoResetEvent _posted = new(false);
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

    public bool CheckAccess() =>
        Environment.CurrentManagedThreadId == _ownerThreadId;

    public void Post(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _queue.Enqueue(callback);
        _posted.Set();
    }

    public void PumpUntil(Func<bool> completed, CancellationToken cancellationToken)
    {
        if (!CheckAccess())
            throw new InvalidOperationException("Pump must run on the owner thread.");

        while (!completed())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_queue.TryDequeue(out var callback))
            {
                callback();
                continue;
            }
            _posted.WaitOne(TimeSpan.FromMilliseconds(50));
        }
    }

    public void Dispose() => _posted.Dispose();
}
```

实际产品应把 `Post` 接入已有单线程事件循环，而不是另建阻塞泵。上例只用于展示所有权规则。

## 2. 推荐：强类型 NativeAOT 友好宿主函数

```csharp
var askInput = engine.Advanced.CreateHostContinuationFunction<string, string>(
    "askInput",
    requestSnapshot: (_, args) => args[0].AsString(),
    operation: (prompt, cancellationToken) =>
        userService.AskInputAsync(prompt, cancellationToken),
    resultConverter: static (_, answer) => new JsString(answer),
    length: 1);
```

三个阶段的线程边界：

```text
requestSnapshot : owner thread，允许读取 JsValue/user thread-affine state
operation       : 可在任意线程完成，只能持有 CLR DTO
resultConverter : owner thread，允许构造 JsValue/读取 Engine realm
```

复杂结果应使用 DTO：

```csharp
internal sealed record ResourceDto(string Id, string Content);

var loadResource = engine.Advanced.CreateHostContinuationFunction<string, ResourceDto>(
    "loadResource",
    requestSnapshot: (_, args) => args[0].AsString(),
    operation: api.LoadResourceAsync,
    resultConverter: static (e, resource) =>
    {
        var obj = e.Realm.Intrinsics.Object.Construct(Arguments.Empty);
        obj.Set("id", new JsString(resource.Id));
        obj.Set("content", new JsString(resource.Content));
        return obj;
    },
    length: 1);
```

## 3. 非泛型入口

```csharp
var function = engine.Advanced.CreateHostContinuationFunction(
    "operation",
    handler: (thisObject, arguments, cancellationToken) =>
    {
        // 只能在返回前读取 arguments/thisObject，并立即复制为 CLR 数据。
        var request = arguments[0].AsString();
        return new ValueTask<object?>(ServiceAsync(request, cancellationToken));
    },
    length: 1,
    resultConverter: static (_, result) => new JsString((string) result!));
```

`HostContinuationHandler` 接收到的参数数组会在 handler 返回后被清空。异步方法不得闭包捕获该数组、`JsValue`、Engine 或任何只能在 owner 线程访问的对象。

未提供 converter 时使用 `JsValue.FromObject`。这对简单 CLR primitive 方便，但生产 NativeAOT 项目应优先显式 converter，避免反射式包装扩大 AOT 和审计表面。

## 4. 暴露宿主对象

```csharp
engine.SetValue("askInput", askInput)
      .SetValue("loadResource", loadResource)
      .SetValue("send", send)
      .Execute("""
          globalThis.user = { askInput, send };
          globalThis.api = { loadResource };
      """);
```

请仅暴露显式 façade，不要在 NativeAOT 模式向不可信脚本开放任意 `AllowClr()` 或大范围 POCO 反射访问。

## 5. 执行脚本

推荐最终结果也在 owner 线程转为 CLR DTO：

```csharp
Task<string> completion = engine.EvaluateWithHostContinuationsAsync(
    script,
    scheduler,
    completionConverter: static (_, value) => value.AsString(),
    source: "workflow.js",
    cancellationToken);
```

可用 overload：

```csharp
Task<JsValue> EvaluateWithHostContinuationsAsync(
    string code,
    IHostContinuationScheduler scheduler,
    string? source = null,
    CancellationToken cancellationToken = default);

Task<TResult> EvaluateWithHostContinuationsAsync<TResult>(
    string code,
    IHostContinuationScheduler scheduler,
    Func<Engine, JsValue, TResult> completionConverter,
    string? source = null,
    CancellationToken cancellationToken = default);

// 同时提供 Prepared<Script> 版本。
```

返回 `Task<JsValue>` 时，结果仍受 Engine owner-thread 规则约束。跨线程消费时应使用泛型 overload，在 task 完成前已将结果变成纯 CLR DTO。

## 6. 事件循环集成

```csharp
var completion = engine.EvaluateWithHostContinuationsAsync(...);

// 调用立即执行到第一个未完成宿主 operation 并返回。
// 之后由现有事件循环自然消费 scheduler.Post 的 callback。
// 不要在 owner 线程对 completion 做 .Wait()/.Result。
```

当最后结果已被 `completionConverter` 转成 CLR DTO 后，外部代码可在其他线程观察完成 task；但 Engine 和 `JsValue` 仍不能跨线程访问。

## 6.1 执行原生 ECMAScript Module

通过 `Engine.Modules.Add` 注册的源码模块（包括 `Prepared<Module>`）可以在相同 owner 调度模型下导入：

```csharp
engine.Modules.Add("workflow", """
    export default function run(input) {
        return hostOperation(input);
    }
    """);
engine.Modules.Add("entry", """
    import run from "workflow";
    export const result = run("request");
    """);

Task<string> completion = engine.ImportModuleWithHostContinuationsAsync(
    "entry",
    scheduler,
    completionConverter: static (_, moduleNamespace) =>
        moduleNamespace.AsObject().Get("result").AsString(),
    cancellationToken);
```

公开 overload：

```csharp
Task<JsValue> ImportModuleWithHostContinuationsAsync(
    string specifier,
    IHostContinuationScheduler scheduler,
    CancellationToken cancellationToken = default);

Task<TResult> ImportModuleWithHostContinuationsAsync<TResult>(
    string specifier,
    IHostContinuationScheduler scheduler,
    Func<Engine, JsValue, TResult> completionConverter,
    CancellationToken cancellationToken = default);
```

传给 converter 的值是原生 module namespace object；import/export、live binding、依赖图链接和模块缓存继续使用 Jint 的 Module Record 实现，不做 CommonJS 改写。模块依赖图中的源码模块以及 entry module 直接或间接调用普通脚本函数时都可以挂起。

此入口明确不支持依赖图中存在 top-level `await`；它会在执行任何模块 body 前抛出 `NotSupportedException`。dynamic `import()` 与隐式 host continuation 的组合也不在已支持边界内。普通同步 `Modules.Import` 行为不变。

## 7. 取消与 Dispose

```csharp
using var cts = new CancellationTokenSource();
var completion = engine.EvaluateWithHostContinuationsAsync(
    script, scheduler, converter, cancellationToken: cts.Token);

cts.Cancel(); // 触发 owner-thread 清理 turn
```

- operation 应尊重传入 token；
- 即使 operation 不响应取消，run 也会放弃 frame，迟到结果被忽略；
- `engine.Dispose()` 必须在 owner 线程调用；
- Dispose pending run 会使 completion 以 `ObjectDisposedException` 失败；
- 不能在正在执行的 slice 内 Dispose。

## 8. 错误示例

禁止保留 JS 参数：

```csharp
// 错误：后台 continuation 捕获 arguments[0]。
handler: (_, arguments, _) =>
    new ValueTask<object?>(Task.Run(() => arguments[0].ToString()));
```

正确做法：

```csharp
handler: (_, arguments, _) =>
{
    var text = arguments[0].AsString(); // owner thread snapshot
    return new ValueTask<object?>(Task.Run<object?>(() => Process(text)));
}
```

禁止 inline scheduler：

```csharp
public void Post(Action callback) => callback(); // 会被运行时检测并拒绝
```

禁止 owner 线程阻塞等待：

```csharp
completion.GetAwaiter().GetResult(); // 在事件循环仍需继续 pump 时会死锁
```
