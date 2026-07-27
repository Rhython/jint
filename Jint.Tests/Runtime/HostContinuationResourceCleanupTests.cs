// These tests synchronously drive a single-thread owner scheduler before inspecting Task results.
#pragma warning disable xUnit1031
#nullable enable

using System.Collections.Concurrent;
using Jint.Native;
using Jint.Runtime;
using Jint.Runtime.Continuations;

namespace Jint.Tests.Runtime;

public sealed class HostContinuationResourceCleanupTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbortingSuspendedCallArgumentsReleasesPooledResourcesExactlyOnce(bool disposeEngine)
    {
        using var scheduler = new ManualOwnerScheduler();
        using var cancellation = new CancellationTokenSource();
        var engine = new Engine();
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfigureCallScenario(engine, pending);

        var task = engine.EvaluateWithHostContinuationsAsync(
            "getHolder().target(marker(), host('wait'), 3);",
            scheduler,
            static (_, value) => value.AsString(),
            cancellationToken: cancellation.Token);

        var run = Assert.IsType<HostContinuationRun>(engine.ActiveHostContinuationRun);
        Assert.True(run.Root.Data.TryGetForTesting<ExpressionBufferSuspendData>(
            static data => data.Rented && data.Buffer.Length == 3,
            out var bufferData));
        Assert.True(run.Root.Data.TryGetForTesting<HostCallSuspendData>(
            static data => data.Stage == HostCallSuspendStage.Arguments && data.ReferenceRecord is not null,
            out var callData));
        var buffer = bufferData!.Buffer;
        var reference = callData!.ReferenceRecord!;

        if (disposeEngine)
        {
            engine.Dispose();
            Assert.Throws<ObjectDisposedException>(() => task.GetAwaiter().GetResult());
        }
        else
        {
            cancellation.Cancel();
            scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
            Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());
        }

        Assert.Empty(bufferData.Buffer);
        Assert.Equal(0, bufferData.NextIndex);
        Assert.False(bufferData.Rented);
        Assert.All(buffer, static value => Assert.Null(value));
        Assert.Null(callData.ReferenceRecord);
        Assert.Null(callData.Function);
        Assert.Equal(HostCallSuspendStage.None, callData.Stage);

        AssertReturnedOnce(engine, buffer);
        AssertReturnedOnce(engine, reference);
    }

    [Fact]
    public void CancellingSuspendedSpreadArgumentsClearsPartialListAndReleasesReference()
    {
        using var scheduler = new ManualOwnerScheduler();
        using var cancellation = new CancellationTokenSource();
        var engine = new Engine();
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfigureCallScenario(engine, pending);

        var task = engine.EvaluateWithHostContinuationsAsync(
            "getHolder().target(...[marker()], host('wait'));",
            scheduler,
            static (_, value) => value.AsString(),
            cancellationToken: cancellation.Token);

        var run = Assert.IsType<HostContinuationRun>(engine.ActiveHostContinuationRun);
        Assert.True(run.Root.Data.TryGetForTesting<SpreadArgumentsSuspendData>(
            static data => data.Target.Count == 1,
            out var spreadData));
        Assert.True(run.Root.Data.TryGetForTesting<HostCallSuspendData>(
            static data => data.Stage == HostCallSuspendStage.Arguments && data.ReferenceRecord is not null,
            out var callData));
        var target = spreadData!.Target;
        var reference = callData!.ReferenceRecord!;

        cancellation.Cancel();
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());

        Assert.Empty(target);
        Assert.Equal(0, spreadData.NextExpressionIndex);
        Assert.Null(callData.ReferenceRecord);
        AssertReturnedOnce(engine, reference);
    }

    [Fact]
    public void CancellingSuspendedNewArgumentsReleasesBufferAndConstructor()
    {
        using var scheduler = new ManualOwnerScheduler();
        using var cancellation = new CancellationTokenSource();
        var engine = new Engine();
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfigureHost(engine, pending);
        engine.Execute(
            """
            function marker() { return { retained: true }; }
            class Box { constructor(a, b, c) { this.value = b; } }
            """);

        var task = engine.EvaluateWithHostContinuationsAsync(
            "new Box(marker(), host('wait'), 3);",
            scheduler,
            static (_, value) => value.ToString(),
            cancellationToken: cancellation.Token);

        var run = Assert.IsType<HostContinuationRun>(engine.ActiveHostContinuationRun);
        Assert.True(run.Root.Data.TryGetForTesting<ExpressionBufferSuspendData>(
            static data => data.Rented && data.Buffer.Length == 3,
            out var bufferData));
        Assert.True(run.Root.Data.TryGetForTesting<HostNewSuspendData>(
            static data => !data.Constructor.IsUndefined(),
            out var newData));
        var buffer = bufferData!.Buffer;

        cancellation.Cancel();
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());

        Assert.Empty(bufferData.Buffer);
        Assert.True(newData!.Constructor.IsUndefined());
        Assert.All(buffer, static value => Assert.Null(value));
        AssertReturnedOnce(engine, buffer);
    }

    [Theory]
    [InlineData("[marker(), host('wait'), 3]")]
    [InlineData("[...([marker()]), host('wait')]")]
    public void DisposingSuspendedArrayLiteralClearsRetainedElementState(string expression)
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfigureHost(engine, pending);
        engine.Execute("function marker() { return { retained: true }; }");

        var task = engine.EvaluateWithHostContinuationsAsync(
            expression,
            scheduler,
            static (_, value) => value.ToString());

        var run = Assert.IsType<HostContinuationRun>(engine.ActiveHostContinuationRun);
        JsValue[]? buffer = null;
        List<JsValue>? target = null;
        if (expression[1] == '.')
        {
            Assert.True(run.Root.Data.TryGetForTesting<SpreadArgumentsSuspendData>(
                static data => data.Target.Count == 1,
                out var spreadData));
            target = spreadData!.Target;
        }
        else
        {
            Assert.True(run.Root.Data.TryGetForTesting<ExpressionBufferSuspendData>(
                static data => !data.Rented && data.Buffer.Length == 3,
                out var bufferData));
            buffer = bufferData!.Buffer;
        }

        engine.Dispose();
        Assert.Throws<ObjectDisposedException>(() => task.GetAwaiter().GetResult());

        if (buffer is not null)
        {
            Assert.All(buffer, static value => Assert.Null(value));
        }
        else
        {
            Assert.Empty(target!);
        }
    }

    [Fact]
    public void NormalResumeReturnsCallResourcesExactlyOnce()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfigureCallScenario(engine, pending);

        var task = engine.EvaluateWithHostContinuationsAsync(
            "getHolder().target(marker(), host('wait'), 3);",
            scheduler,
            static (_, value) => value.AsString());

        var run = Assert.IsType<HostContinuationRun>(engine.ActiveHostContinuationRun);
        Assert.True(run.Root.Data.TryGetForTesting<ExpressionBufferSuspendData>(
            static data => data.Rented && data.Buffer.Length == 3,
            out var bufferData));
        Assert.True(run.Root.Data.TryGetForTesting<HostCallSuspendData>(
            static data => data.Stage == HostCallSuspendStage.Arguments && data.ReferenceRecord is not null,
            out var callData));
        var buffer = bufferData!.Buffer;
        var reference = callData!.ReferenceRecord!;

        pending.SetResult("resumed");
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);

        Assert.Equal("resumed", task.GetAwaiter().GetResult());
        AssertReturnedOnce(engine, buffer);
        AssertReturnedOnce(engine, reference);
    }

    [Fact]
    public void CancellingSuspendedCompoundAssignmentReleasesSavedReference()
    {
        using var scheduler = new ManualOwnerScheduler();
        using var cancellation = new CancellationTokenSource();
        var engine = new Engine();
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfigureHost(engine, pending);
        engine.Execute(
            """
            globalThis.target = { value: 1 };
            function getTarget() { return target; }
            """);

        var task = engine.EvaluateWithHostContinuationsAsync(
            "getTarget().value += host('wait');",
            scheduler,
            static (_, value) => value.AsNumber(),
            cancellationToken: cancellation.Token);

        var run = Assert.IsType<HostContinuationRun>(engine.ActiveHostContinuationRun);
        Assert.True(run.Root.Data.TryGetForTesting<AssignmentSuspendData>(
            static data => data.Lref is not null,
            out var assignmentData));
        var reference = assignmentData!.Lref;

        cancellation.Cancel();
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());

        Assert.Null(assignmentData.Lref);
        Assert.True(assignmentData.OriginalLeftValue.IsUndefined());
        AssertReturnedOnce(engine, reference);
    }

    [Fact]
    public void CancellingSuspendedTaggedTemplateReleasesArgumentBuffer()
    {
        using var scheduler = new ManualOwnerScheduler();
        using var cancellation = new CancellationTokenSource();
        var engine = new Engine();
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfigureHost(engine, pending);
        engine.Execute(
            """
            function marker() { return { retained: true }; }
            function tag(strings, first, second) { return second; }
            """);

        var task = engine.EvaluateWithHostContinuationsAsync(
            "tag`${marker()}${host('wait')}`;",
            scheduler,
            static (_, value) => value.AsString(),
            cancellationToken: cancellation.Token);

        var run = Assert.IsType<HostContinuationRun>(engine.ActiveHostContinuationRun);
        Assert.True(run.Root.Data.TryGetForTesting<TaggedTemplateSuspendData>(
            static data => data.Args.Length == 3,
            out var templateData));
        var buffer = templateData!.Args;

        cancellation.Cancel();
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());

        Assert.Empty(templateData.Args);
        Assert.Null(templateData.Tagger);
        Assert.True(templateData.ThisObject.IsUndefined());
        Assert.All(buffer, static value => Assert.Null(value));
        AssertReturnedOnce(engine, buffer);
    }

    [Fact]
    public void NormalResumeReturnsAssignmentReferenceExactlyOnce()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfigureHost(engine, pending);
        engine.Execute(
            """
            globalThis.target = { value: "before:" };
            function getTarget() { return target; }
            """);

        var task = engine.EvaluateWithHostContinuationsAsync(
            "getTarget().value += host('wait');",
            scheduler,
            static (_, value) => value.AsString());

        var run = Assert.IsType<HostContinuationRun>(engine.ActiveHostContinuationRun);
        Assert.True(run.Root.Data.TryGetForTesting<AssignmentSuspendData>(
            static data => data.Lref is not null,
            out var assignmentData));
        var reference = assignmentData!.Lref;

        pending.SetResult("after");
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);

        Assert.Equal("before:after", task.GetAwaiter().GetResult());
        AssertReturnedOnce(engine, reference);
    }

    [Fact]
    public void NormalResumeReturnsTaggedTemplateBufferExactlyOnce()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfigureHost(engine, pending);
        engine.Execute(
            """
            function marker() { return { retained: true }; }
            function tag(strings, first, second) { return second; }
            """);

        var task = engine.EvaluateWithHostContinuationsAsync(
            "tag`${marker()}${host('wait')}`;",
            scheduler,
            static (_, value) => value.AsString());

        var run = Assert.IsType<HostContinuationRun>(engine.ActiveHostContinuationRun);
        Assert.True(run.Root.Data.TryGetForTesting<TaggedTemplateSuspendData>(
            static data => data.Args.Length == 3,
            out var templateData));
        var buffer = templateData!.Args;

        pending.SetResult("resumed");
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);

        Assert.Equal("resumed", task.GetAwaiter().GetResult());
        AssertReturnedOnce(engine, buffer);
    }

    private static void ConfigureCallScenario(Engine engine, TaskCompletionSource<object?> pending)
    {
        ConfigureHost(engine, pending);
        engine.Execute(
            """
            function marker() { return { retained: true }; }
            globalThis.holder = {
                target(a, b, c) { return b; }
            };
            function getHolder() { return holder; }
            """);
    }

    private static void ConfigureHost(Engine engine, TaskCompletionSource<object?> pending)
    {
        engine.SetValue(
            "host",
            engine.Advanced.CreateHostContinuationFunction(
                "host",
                (_, _, _) => new ValueTask<object?>(pending.Task),
                resultConverter: static (_, value) => JsString.Create((string) value!)));
    }

    private static void AssertReturnedOnce(Engine engine, JsValue[] expected)
    {
        var rented = new JsValue[16][];
        for (var i = 0; i < rented.Length; i++)
        {
            rented[i] = engine._jsValueArrayPool.RentArray(expected.Length);
        }

        Assert.Equal(1, rented.Count(candidate => ReferenceEquals(candidate, expected)));

        foreach (var candidate in rented)
        {
            engine._jsValueArrayPool.ReturnArray(candidate);
        }
    }

    private static void AssertReturnedOnce(Engine engine, Reference expected)
    {
        var rented = new Reference[11];
        for (var i = 0; i < rented.Length; i++)
        {
            rented[i] = engine._referencePool.Rent(JsValue.Undefined, JsString.Empty, strict: false, thisValue: null);
        }

        Assert.Equal(1, rented.Count(candidate => ReferenceEquals(candidate, expected)));

        foreach (var candidate in rented)
        {
            engine._referencePool.Return(candidate);
        }
    }

    private sealed class ManualOwnerScheduler : IHostContinuationScheduler, IDisposable
    {
        private readonly ConcurrentQueue<Action> _callbacks = new();
        private readonly AutoResetEvent _posted = new(false);
        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

        public bool CheckAccess() => Environment.CurrentManagedThreadId == _ownerThreadId;

        public void Post(Action callback)
        {
            _callbacks.Enqueue(callback);
            _posted.Set();
        }

        public void RunUntil(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!condition())
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException("Timed out while driving the owner-thread scheduler.");
                }

                if (_callbacks.TryDequeue(out var callback))
                {
                    callback();
                }
                else
                {
                    _posted.WaitOne(remaining > TimeSpan.FromMilliseconds(100)
                        ? TimeSpan.FromMilliseconds(100)
                        : remaining);
                }
            }
        }

        public void Dispose() => _posted.Dispose();
    }
}
