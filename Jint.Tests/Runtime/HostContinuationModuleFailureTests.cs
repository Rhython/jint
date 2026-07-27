#nullable enable
// These tests synchronously drive a single-thread owner scheduler and only block after the
// continuation task has completed.
#pragma warning disable xUnit1031

using System.Collections.Concurrent;
using Jint.Native;
using Jint.Runtime;
using Jint.Runtime.Continuations;

namespace Jint.Tests.Runtime;

public sealed class HostContinuationModuleFailureTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void SuccessfulModuleEvaluationRemainsCached()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        engine.SetValue("executions", 0);
        engine.Modules.Add("entry", "executions++; export const result = 42;");

        Assert.Equal(42, ImportNumber(engine, scheduler, "entry", "result"));
        Assert.Equal(42, ImportNumber(engine, scheduler, "entry", "result"));
        Assert.Equal(1, engine.GetValue("executions").AsNumber());
    }

    [Fact]
    public void SuspendedModuleStillResumesNormally()
    {
        using var scheduler = new ManualOwnerScheduler();
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Engine();
        engine.SetValue("host", engine.Advanced.CreateHostContinuationFunction(
            "host",
            (_, _, _) => new ValueTask<object?>(completion.Task),
            resultConverter: static (_, value) => JsString.Create((string) value!)));
        engine.Modules.Add("entry", "export const result = host();");

        var task = engine.ImportModuleWithHostContinuationsAsync(
            "entry",
            scheduler,
            static (_, ns) => ns.AsObject().Get("result").AsString());

        Assert.False(task.IsCompleted);
        completion.SetResult("resumed");
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.Equal("resumed", task.GetAwaiter().GetResult());
    }

    [Fact]
    public void EntryThrowIsCachedAcrossImports()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        engine.SetValue("executions", 0);
        engine.Modules.Add("entry", "executions++; throw new Error('entry failed');");

        Assert.Equal("entry failed", ImportJavaScriptError(engine, scheduler, "entry").Message);
        Assert.Equal("entry failed", ImportJavaScriptError(engine, scheduler, "entry").Message);
        Assert.Equal(1, engine.GetValue("executions").AsNumber());
    }

    [Fact]
    public void DependencyThrowIsCachedForDependencyAndEntry()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        engine.SetValue("executions", 0);
        engine.Modules.Add("dependency", "executions++; throw new Error('dependency failed');");
        engine.Modules.Add("entry", "import 'dependency'; export const unreachable = 1;");

        Assert.Equal("dependency failed", ImportJavaScriptError(engine, scheduler, "entry").Message);
        Assert.Equal("dependency failed", ImportJavaScriptError(engine, scheduler, "entry").Message);
        Assert.Equal("dependency failed", ImportJavaScriptError(engine, scheduler, "dependency").Message);
        Assert.Equal(1, engine.GetValue("executions").AsNumber());
    }

    [Fact]
    public void CyclicGraphFailureIsCachedForEveryActiveMember()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        engine.SetValue("executions", 0);
        engine.Modules.Add("a", "import 'b'; executions++; export const a = 1;");
        engine.Modules.Add("b", "import 'a'; executions++; throw new Error('cycle failed');");

        Assert.Equal("cycle failed", ImportJavaScriptError(engine, scheduler, "a").Message);
        Assert.Equal("cycle failed", ImportJavaScriptError(engine, scheduler, "a").Message);
        Assert.Equal("cycle failed", ImportJavaScriptError(engine, scheduler, "b").Message);
        Assert.Equal(1, engine.GetValue("executions").AsNumber());
    }

    [Fact]
    public void ConstraintFailureIsCachedAndLeavesEngineUsable()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine(options => options.MaxStatements(1));
        engine.Modules.Add("entry", "globalThis.first = 1; globalThis.second = 2; export const result = 3;");

        Assert.Throws<StatementsCountOverflowException>(() => Import(engine, scheduler, "entry"));
        Assert.Equal(2, engine.Evaluate("1 + 1").AsNumber());
        Assert.Throws<StatementsCountOverflowException>(() => Import(engine, scheduler, "entry"));
    }

    [Fact]
    public void DirectClrFailureIsCachedAndLeavesEngineUsable()
    {
        using var scheduler = new ManualOwnerScheduler();
        var executions = 0;
        var engine = new Engine();
        engine.SetValue("fail", new Action(() =>
        {
            executions++;
            throw new InvalidOperationException("host failed");
        }));
        engine.Modules.Add("entry", "fail(); export const unreachable = 1;");

        Assert.Equal("host failed", Assert.Throws<InvalidOperationException>(() => Import(engine, scheduler, "entry")).Message);
        Assert.Equal(2, engine.Evaluate("1 + 1").AsNumber());
        Assert.Equal("host failed", Assert.Throws<InvalidOperationException>(() => Import(engine, scheduler, "entry")).Message);
        Assert.Equal(1, executions);
    }

    private static double ImportNumber(Engine engine, IHostContinuationScheduler scheduler, string specifier, string exportName)
    {
        return Import(
            engine,
            scheduler,
            specifier,
            (_, ns) => ns.AsObject().Get(exportName).AsNumber());
    }

    private static JavaScriptException ImportJavaScriptError(Engine engine, IHostContinuationScheduler scheduler, string specifier)
    {
        return Assert.Throws<JavaScriptException>(() => Import(engine, scheduler, specifier));
    }

    private static JsValue Import(Engine engine, IHostContinuationScheduler scheduler, string specifier)
    {
        return Import(engine, scheduler, specifier, static (_, value) => value);
    }

    private static TResult Import<TResult>(
        Engine engine,
        IHostContinuationScheduler scheduler,
        string specifier,
        Func<Engine, JsValue, TResult> converter)
    {
        var task = engine.ImportModuleWithHostContinuationsAsync(specifier, scheduler, converter);
        return task.GetAwaiter().GetResult();
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
            Assert.True(CheckAccess());
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
                    _posted.WaitOne(remaining > TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : remaining);
                }
            }
        }

        public void Dispose() => _posted.Dispose();
    }
}
