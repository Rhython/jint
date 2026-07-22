using System.Collections.Concurrent;
using Jint;
using Jint.Native;
using Jint.Runtime.Continuations;

try
{
    using var scheduler = new OwnerThreadScheduler();
    using var engine = new Engine();
    var ownerThreadId = Environment.CurrentManagedThreadId;
    var snapshotThreads = new List<int>();
    var operationCompletionThreads = new ConcurrentBag<int>();
    var converterThreads = new List<int>();
    var completionConverterThread = 0;

    engine.SetValue("askInput", engine.Advanced.CreateHostContinuationFunction<string, string>(
        "askInput",
        (_, arguments) =>
        {
            snapshotThreads.Add(Environment.CurrentManagedThreadId);
            return arguments[0].AsString();
        },
        async (prompt, cancellationToken) =>
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            operationCompletionThreads.Add(Environment.CurrentManagedThreadId);
            return prompt == "which one you want to load" ? "resource-1" : "unexpected";
        },
        (_, result) =>
        {
            converterThreads.Add(Environment.CurrentManagedThreadId);
            return new JsString(result);
        }));

    engine.SetValue("loadResource", engine.Advanced.CreateHostContinuationFunction<string, ResourceDto>(
        "loadResource",
        (_, arguments) =>
        {
            snapshotThreads.Add(Environment.CurrentManagedThreadId);
            return arguments[0].AsString();
        },
        async (id, cancellationToken) =>
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            operationCompletionThreads.Add(Environment.CurrentManagedThreadId);
            return new ResourceDto(id, "payload");
        },
        (e, result) =>
        {
            converterThreads.Add(Environment.CurrentManagedThreadId);
            var resource = e.Intrinsics.Object.Construct(Array.Empty<JsValue>());
            resource.Set("id", result.Id);
            resource.Set("content", result.Content);
            return resource;
        }));

    engine.SetValue("send", engine.Advanced.CreateHostContinuationFunction<string, string>(
        "send",
        (_, arguments) =>
        {
            snapshotThreads.Add(Environment.CurrentManagedThreadId);
            return arguments[0].AsString();
        },
        async (content, cancellationToken) =>
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            operationCompletionThreads.Add(Environment.CurrentManagedThreadId);
            return "sent:" + content;
        },
        (_, result) =>
        {
            converterThreads.Add(Environment.CurrentManagedThreadId);
            return new JsString(result);
        }));

    engine.Execute("globalThis.user = { askInput, send }; globalThis.api = { loadResource };");

    var completion = engine.EvaluateWithHostContinuationsAsync(
        """
        let answer = user.askInput("which one you want to load");
        let resource = api.loadResource(answer);
        let data = user.send(resource.content);
        data;
        """,
        scheduler,
        (_, value) =>
        {
            completionConverterThread = Environment.CurrentManagedThreadId;
            return value.AsString();
        });

    if (completion.IsCompleted)
    {
        throw new InvalidOperationException("The script should have yielded to the owner event loop.");
    }

    scheduler.RunUntil(() => completion.IsCompleted, TimeSpan.FromSeconds(10));
    var result = completion.GetAwaiter().GetResult();

    if (result != "sent:payload")
    {
        throw new InvalidOperationException($"Unexpected result: {result}");
    }

    if (snapshotThreads.Count != 3 || snapshotThreads.Any(id => id != ownerThreadId))
    {
        throw new InvalidOperationException("A request snapshot read JavaScript data outside the owner thread.");
    }

    if (converterThreads.Count != 3 || converterThreads.Any(id => id != ownerThreadId))
    {
        throw new InvalidOperationException("A host result converter ran outside the owner thread.");
    }

    if (completionConverterThread != ownerThreadId)
    {
        throw new InvalidOperationException("The final result converter ran outside the owner thread.");
    }

    if (scheduler.CallbackThreads.Any(id => id != ownerThreadId))
    {
        throw new InvalidOperationException("A JavaScript resume callback ran outside the owner thread.");
    }

    if (operationCompletionThreads.IsEmpty)
    {
        throw new InvalidOperationException("No asynchronous CLR operation completed.");
    }

    Console.WriteLine("HOST_CONTINUATION_AOT_OK:" + result);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("HOST_CONTINUATION_AOT_FAILED:" + exception);
    return 1;
}

internal sealed record ResourceDto(string Id, string Content);

internal sealed class OwnerThreadScheduler : IHostContinuationScheduler, IDisposable
{
    private readonly ConcurrentQueue<Action> _callbacks = new();
    private readonly AutoResetEvent _posted = new(false);

    public OwnerThreadScheduler()
    {
        OwnerThreadId = Environment.CurrentManagedThreadId;
    }

    public int OwnerThreadId { get; }
    public List<int> CallbackThreads { get; } = new();
    public bool CheckAccess() => Environment.CurrentManagedThreadId == OwnerThreadId;

    public void Post(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callbacks.Enqueue(callback);
        _posted.Set();
    }

    public void RunUntil(Func<bool> condition, TimeSpan timeout)
    {
        if (!CheckAccess())
        {
            throw new InvalidOperationException("The scheduler pump must run on its owner thread.");
        }

        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (_callbacks.TryDequeue(out var callback))
            {
                CallbackThreads.Add(Environment.CurrentManagedThreadId);
                callback();
                continue;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("Timed out while pumping the owner event loop.");
            }

            _posted.WaitOne(remaining > TimeSpan.FromMilliseconds(100)
                ? TimeSpan.FromMilliseconds(100)
                : remaining);
        }
    }

    public void Dispose() => _posted.Dispose();
}
