#nullable enable
// These tests deliberately inspect completed tasks synchronously after driving the owner scheduler.
#pragma warning disable xUnit1031

using System.Collections.Concurrent;
using Jint.Native;
using Jint.Runtime.Continuations;
using Jint.Runtime.Modules;
using Module = Jint.Runtime.Modules.Module;

namespace Jint.Tests.Runtime;

public sealed class HostContinuationSchedulerFailureTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void SynchronousCompletionPostRejectionTerminatesRunAndReleasesEngine()
    {
        var scheduler = new RejectingOwnerScheduler();
        var engine = new Engine();
        engine.SetValue(
            "host",
            engine.Advanced.CreateHostContinuationFunction(
                "host",
                static (_, _, _) => new ValueTask<object?>("done"),
                resultConverter: static (_, value) => JsString.Create((string) value!)));

        var task = engine.EvaluateWithHostContinuationsAsync(
            "host();",
            scheduler,
            static (_, value) => value.AsString());

        Assert.True(task.IsCompleted);
        var exception = Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
        Assert.Contains("rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, engine.Evaluate("1 + 1").AsNumber());
    }

    [Fact]
    public void CancellationPostRejectionTerminatesIdleRunAndReleasesEngine()
    {
        var scheduler = new RejectingOwnerScheduler();
        using var cancellation = new CancellationTokenSource();
        var engine = new Engine();
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SetValue(
            "host",
            engine.Advanced.CreateHostContinuationFunction(
                "host",
                (_, _, _) => new ValueTask<object?>(pending.Task),
                resultConverter: static (_, value) => JsString.Create((string) value!)));

        var task = engine.EvaluateWithHostContinuationsAsync(
            "host();",
            scheduler,
            static (_, value) => value.AsString(),
            cancellationToken: cancellation.Token);

        Assert.False(task.IsCompleted);
        cancellation.Cancel();

        Assert.True(SpinWait.SpinUntil(() => task.IsCompleted, TestTimeout));
        var exception = Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
        Assert.Contains("rejected", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, engine.Evaluate("1 + 1").AsNumber());

        pending.TrySetResult("late");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModuleEntryPointsRejectSuspendedRunBeforeMutationOrEvaluation(bool useWrongThread)
    {
        using var scheduler = new ManualOwnerScheduler();
        var loader = new CountingModuleLoader();
        var engine = new Engine(options => options.EnableModules(loader));
        var pending = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SetValue(
            "host",
            engine.Advanced.CreateHostContinuationFunction(
                "host",
                (_, _, _) => new ValueTask<object?>(pending.Task),
                resultConverter: static (_, value) => JsString.Create((string) value!)));

        var prebuilt = new ModuleBuilder(engine, "prebuilt");
        prebuilt.AddSource("export const value = 3;");
        var task = engine.EvaluateWithHostContinuationsAsync(
            "host();",
            scheduler,
            static (_, value) => value.AsString());
        var buildCallbackInvoked = false;

        AssertRejected(() => engine.Modules.Add("source", "export const value = 1;"), useWrongThread);
        AssertRejected(
            () => engine.Modules.Add(
                "callback",
                builder =>
                {
                    buildCallbackInvoked = true;
                    builder.AddSource("export const value = 2;");
                }),
            useWrongThread);
        AssertRejected(() => engine.Modules.Add("prebuilt", prebuilt), useWrongThread);
        AssertRejected(() => engine.Modules.Import("loaded"), useWrongThread);

        Assert.False(buildCallbackInvoked);
        Assert.Equal(0, loader.ResolveCount);
        Assert.Equal(0, loader.LoadCount);

        pending.TrySetResult("done");
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.Equal("done", task.GetAwaiter().GetResult());

        engine.Modules.Add("source", "export const value = 1;");
        engine.Modules.Add("callback", "export const value = 2;");
        engine.Modules.Add("prebuilt", prebuilt);
        Assert.Equal(1, engine.Modules.Import("source").Get("value").AsInteger());
    }

    private static void AssertRejected(Action action, bool useWrongThread)
    {
        var exception = useWrongThread
            ? Task.Run(() => Record.Exception(action)).GetAwaiter().GetResult()
            : Record.Exception(action);

        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains(
            useWrongThread ? "owner" : "suspended",
            invalidOperation.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RejectingOwnerScheduler : IHostContinuationScheduler
    {
        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

        public bool CheckAccess() => Environment.CurrentManagedThreadId == _ownerThreadId;

        public void Post(Action callback)
        {
            throw new InvalidOperationException("Owner scheduler rejected the posted callback.");
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
                if (_callbacks.TryDequeue(out var callback))
                {
                    callback();
                    continue;
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException("Timed out while driving the owner-thread scheduler.");
                }
                _posted.WaitOne(remaining > TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : remaining);
            }
        }

        public void Dispose() => _posted.Dispose();
    }

    private sealed class CountingModuleLoader : IModuleLoader
    {
        public int ResolveCount { get; private set; }
        public int LoadCount { get; private set; }

        public ResolvedSpecifier Resolve(string? referencingModuleLocation, ModuleRequest moduleRequest)
        {
            ResolveCount++;
            return new ResolvedSpecifier(moduleRequest, moduleRequest.Specifier, Uri: null, SpecifierType.Bare);
        }

        public Module LoadModule(Engine engine, ResolvedSpecifier resolved)
        {
            LoadCount++;
            return ModuleFactory.BuildSourceTextModule(engine, resolved, "export const value = 4;");
        }
    }
}
