#nullable enable
// These tests intentionally drive a single-thread owner scheduler synchronously. Awaiting the
// returned task would let the test framework resume on another thread and invalidate the invariant
// under test, so blocking result inspection is deliberate after the scheduler has completed it.
#pragma warning disable xUnit1031

using System.Collections.Concurrent;
using System.Threading;
using Jint.Native;
using Jint.Runtime;
using Jint.Runtime.Continuations;

namespace Jint.Tests.Runtime;

public sealed class HostContinuationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void ExecutesUnchangedSequentialWorkflowOnOwnerThread()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var requests = new Queue<PendingRequest>();
        var observedThreads = new List<int>();
        var converterThreads = new List<int>();

        HostContinuationFunction Deferred(
            string name,
            Func<JsValue[], string> snapshot,
            HostContinuationResultConverter converter)
        {
            return engine.Advanced.CreateHostContinuationFunction(
                name,
                (_, arguments, _) =>
                {
                    Assert.True(scheduler.CheckAccess());
                    observedThreads.Add(Environment.CurrentManagedThreadId);
                    var request = new PendingRequest(name, snapshot(arguments));
                    requests.Enqueue(request);
                    return new ValueTask<object?>(request.Completion.Task);
                },
                resultConverter: (e, value) =>
                {
                    Assert.True(scheduler.CheckAccess());
                    converterThreads.Add(Environment.CurrentManagedThreadId);
                    return converter(e, value);
                });
        }

        var ask = Deferred("askInput", a => a[0].AsString(), static (_, value) => JsString.Create((string) value!));
        var load = Deferred("loadResource", a => a[0].AsString(), static (e, value) =>
        {
            var result = (ResourceResult) value!;
            var resource = e.Realm.Intrinsics.Object.Construct(Arguments.Empty);
            resource.Set("content", JsString.Create(result.Content));
            return resource;
        });
        var send = Deferred("send", a => a[0].AsString(), static (_, value) => JsString.Create((string) value!));

        engine.SetValue("askInput", ask)
            .SetValue("loadResource", load)
            .SetValue("send", send)
            .Execute("globalThis.user = { askInput, send }; globalThis.api = { loadResource };");

        var task = engine.EvaluateWithHostContinuationsAsync(
            """
            let answer = user.askInput("which one you want to load");
            let resource = api.loadResource(answer);
            let data = user.send(resource.content);
            data;
            """,
            scheduler,
            static (_, value) => value.AsString());

        Assert.False(task.IsCompleted);
        CompleteNext(requests, "askInput", "which one you want to load", "r-1");
        scheduler.RunUntil(() => requests.Count == 1 || task.IsCompleted, TestTimeout);
        CompleteNext(requests, "loadResource", "r-1", new ResourceResult("payload"));
        scheduler.RunUntil(() => requests.Count == 1 || task.IsCompleted, TestTimeout);
        CompleteNext(requests, "send", "payload", "sent:payload");
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);

        Assert.Equal("sent:payload", task.GetAwaiter().GetResult());
        Assert.All(observedThreads, id => Assert.Equal(scheduler.OwnerThreadId, id));
        Assert.All(converterThreads, id => Assert.Equal(scheduler.OwnerThreadId, id));
        Assert.True(scheduler.PostCount >= 3);
    }

    [Fact]
    public void SynchronousHostCompletionStillResumesOnLaterTurn()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var handlerCalls = 0;
        var converterCalls = 0;

        var host = engine.Advanced.CreateHostContinuationFunction(
            "host",
            (_, _, _) =>
            {
                Assert.True(scheduler.CheckAccess());
                handlerCalls++;
                return new ValueTask<object?>("ok");
            },
            resultConverter: (_, value) =>
            {
                Assert.True(scheduler.CheckAccess());
                converterCalls++;
                return JsString.Create((string) value!);
            });
        engine.SetValue("host", host);

        var task = engine.EvaluateWithHostContinuationsAsync(
            "let side = 0; side++; let value = host(); side + ':' + value;",
            scheduler,
            static (_, value) => value.AsString());

        Assert.False(task.IsCompleted);
        Assert.Equal(1, handlerCalls);
        Assert.Equal(0, converterCalls);
        Assert.Equal(1, scheduler.PendingCount);

        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.Equal("1:ok", task.GetAwaiter().GetResult());
        Assert.Equal(1, handlerCalls);
        Assert.Equal(1, converterCalls);
    }

    [Fact]
    public void ResumesNestedOrdinaryFunctionsAndMultipleEffectsInSameChild()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var requests = new Queue<PendingRequest>();
        engine.SetValue("host", CreateDeferredStringHost(engine, scheduler, requests));

        var task = engine.EvaluateWithHostContinuationsAsync(
            """
            function leaf(value) {
                let first = host(value);
                let second = host(first);
                return second + 1;
            }
            function middle(value) { return leaf(value) * 2; }
            function root() { return middle("a") + 3; }
            root();
            """,
            scheduler,
            static (_, value) => value.AsNumber());

        CompleteNext(requests, "host", "a", "4");
        scheduler.RunUntil(() => requests.Count == 1 || task.IsCompleted, TestTimeout);
        CompleteNext(requests, "host", "4", 5);
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);

        Assert.Equal(15, task.GetAwaiter().GetResult());
    }

    [Fact]
    public void DoesNotReplayResolvedCalleeOrEarlierArguments()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var requests = new Queue<PendingRequest>();
        engine.SetValue("host", CreateDeferredStringHost(engine, scheduler, requests));

        var task = engine.EvaluateWithHostContinuationsAsync(
            """
            let calleeGets = 0;
            let argumentEffects = 0;
            const holder = {
                get fn() {
                    calleeGets++;
                    return (left, right) => left + right;
                }
            };
            let result = holder.fn(argumentEffects++, host("x"));
            `${calleeGets}:${argumentEffects}:${result}`;
            """,
            scheduler,
            static (_, value) => value.AsString());

        CompleteNext(requests, "host", "x", 7);
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);

        Assert.Equal("1:1:7", task.GetAwaiter().GetResult());
    }

    [Fact]
    public void NonCallableAfterSuspendedArgumentThrowsAndLeavesEngineUsable()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var requests = new Queue<PendingRequest>();
        engine.SetValue("host", CreateDeferredStringHost(engine, scheduler, requests));

        var task = engine.EvaluateWithHostContinuationsAsync(
            "const notCallable = 1; notCallable(host('argument'));",
            scheduler,
            static (_, value) => value.ToString());

        CompleteNext(requests, "host", "argument", "done");
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);

        var exception = Assert.Throws<JavaScriptException>(() => task.GetAwaiter().GetResult());
        Assert.Contains("not a function", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, engine.Evaluate("[1, 2, 3].length").AsNumber());
    }

    [Fact]
    public void PreservesArgumentsObjectAcrossSuspension()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var requests = new Queue<PendingRequest>();
        engine.SetValue("host", CreateDeferredStringHost(engine, scheduler, requests));

        var task = engine.EvaluateWithHostContinuationsAsync(
            """
            function f() {
                let before = arguments[0];
                let value = host(arguments[1]);
                return before + ':' + value + ':' + arguments[0];
            }
            f('left', 'request');
            """,
            scheduler,
            static (_, value) => value.AsString());

        CompleteNext(requests, "host", "request", "answer");
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.Equal("left:answer:left", task.GetAwaiter().GetResult());
    }

    [Fact]
    public void SupportsRepeatedSuspensionInsideLoop()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var requests = new Queue<PendingRequest>();
        engine.SetValue("host", CreateDeferredStringHost(engine, scheduler, requests));

        var task = engine.EvaluateWithHostContinuationsAsync(
            "let total = 0; for (let i = 0; i < 3; i++) total += host(String(i)); total;",
            scheduler,
            static (_, value) => value.AsNumber());

        for (var i = 0; i < 3; i++)
        {
            CompleteNext(requests, "host", i.ToString(), i + 1);
            scheduler.RunUntil(() => task.IsCompleted || requests.Count == 1, TestTimeout);
        }

        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.Equal(6, task.GetAwaiter().GetResult());
    }

    [Fact]
    public void PreservesConstructorGetterAndArgumentsBeforeNew()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var requests = new Queue<PendingRequest>();
        engine.SetValue("host", CreateDeferredStringHost(engine, scheduler, requests));

        var task = engine.EvaluateWithHostContinuationsAsync(
            """
            let getterCalls = 0;
            let argumentEffects = 0;
            const holder = {
                get C() {
                    getterCalls++;
                    return class Box {
                        constructor(left, right) { this.value = left + right; }
                    };
                }
            };
            const box = new holder.C(argumentEffects++, host('new'));
            `${getterCalls}:${argumentEffects}:${box.value}`;
            """,
            scheduler,
            static (_, value) => value.AsString());

        CompleteNext(requests, "host", "new", 9);
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.Equal("1:1:9", task.GetAwaiter().GetResult());
    }

    [Fact]
    public void InjectsHostFailureAtOriginalCallAndRunsFinallyOnce()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var requests = new Queue<PendingRequest>();
        engine.SetValue("host", CreateDeferredStringHost(engine, scheduler, requests));

        var task = engine.EvaluateWithHostContinuationsAsync(
            """
            let finallyCount = 0;
            let caught = '';
            function run() {
                try {
                    return host('failure');
                } catch (error) {
                    caught = error.message;
                    return 'caught';
                } finally {
                    finallyCount++;
                }
            }
            run() + ':' + finallyCount + ':' + caught;
            """,
            scheduler,
            static (_, value) => value.AsString());

        var pending = Dequeue(requests, "host", "failure");
        pending.Completion.SetException(new InvalidOperationException("boom"));
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);

        Assert.Equal("caught:1:boom", task.GetAwaiter().GetResult());
    }

    [Fact]
    public void DrainsMicrotasksAtExternalSuspensionBoundary()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var requests = new Queue<PendingRequest>();
        engine.SetValue("host", CreateDeferredStringHost(engine, scheduler, requests));

        var task = engine.EvaluateWithHostContinuationsAsync(
            """
            const log = [];
            Promise.resolve().then(() => log.push('microtask'));
            host('pause');
            log.push('after');
            log.join(',');
            """,
            scheduler,
            static (_, value) => value.AsString());

        CompleteNext(requests, "host", "pause", "ok");
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.Equal("microtask,after", task.GetAwaiter().GetResult());
    }

    [Fact]
    public void GenericHostAndCompletionConvertersRunOnOwnerThread()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var operationThread = 0;
        var hostConverterThread = 0;
        var completionConverterThread = 0;

        var length = engine.Advanced.CreateHostContinuationFunction<string, int>(
            "length",
            (_, arguments) => arguments[0].AsString(),
            (request, _) =>
            {
                operationThread = Environment.CurrentManagedThreadId;
                return new ValueTask<int>(request.Length);
            },
            (_, result) =>
            {
                hostConverterThread = Environment.CurrentManagedThreadId;
                return JsNumber.Create(result);
            });
        engine.SetValue("length", length);

        var task = engine.EvaluateWithHostContinuationsAsync(
            "length('abcd') + 1;",
            scheduler,
            (_, value) =>
            {
                completionConverterThread = Environment.CurrentManagedThreadId;
                return value.AsNumber();
            });

        Assert.False(task.IsCompleted);
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.Equal(5, task.GetAwaiter().GetResult());
        Assert.Equal(scheduler.OwnerThreadId, operationThread);
        Assert.Equal(scheduler.OwnerThreadId, hostConverterThread);
        Assert.Equal(scheduler.OwnerThreadId, completionConverterThread);
    }

    [Fact]
    public void CancellationAbandonsPendingFrameAndReleasesEngine()
    {
        using var scheduler = new ManualOwnerScheduler();
        using var cts = new CancellationTokenSource();
        var engine = new Engine();
        var requests = new Queue<PendingRequest>();
        engine.SetValue("host", CreateDeferredStringHost(engine, scheduler, requests));

        var task = engine.EvaluateWithHostContinuationsAsync(
            "host('cancel');",
            scheduler,
            static (_, value) => value.AsString(),
            cancellationToken: cts.Token);

        Assert.Single(requests);
        cts.Cancel();
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.True(task.IsCanceled);
        Assert.Equal(2, engine.Evaluate("1 + 1").AsNumber());

        var postsBeforeLateCompletion = scheduler.PostCount;
        requests.Dequeue().Completion.TrySetResult("late");
        Assert.False(SpinWait.SpinUntil(
            () => scheduler.PostCount != postsBeforeLateCompletion,
            TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void EngineApisAreRejectedWhileRunIsSuspended()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var requests = new Queue<PendingRequest>();
        engine.SetValue("host", CreateDeferredStringHost(engine, scheduler, requests));

        var task = engine.EvaluateWithHostContinuationsAsync(
            "globalThis.marker = 42; host('pause'); marker;",
            scheduler,
            static (_, value) => value.AsNumber());

        var ownerError = Assert.Throws<InvalidOperationException>(() => engine.GetValue("marker"));
        Assert.Contains("suspended", ownerError.Message, StringComparison.OrdinalIgnoreCase);

        var backgroundError = Task.Run(() =>
            Assert.Throws<InvalidOperationException>(() => engine.GetValue("marker")))
            .GetAwaiter().GetResult();
        Assert.Contains("owner", backgroundError.Message, StringComparison.OrdinalIgnoreCase);

        CompleteNext(requests, "host", "pause", "ok");
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.Equal(42, task.GetAwaiter().GetResult());
        Assert.Equal(42, engine.GetValue("marker").AsNumber());
    }

    [Fact]
    public void DisposeFaultsPendingRunAndIgnoresLateCompletion()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var requests = new Queue<PendingRequest>();
        engine.SetValue("host", CreateDeferredStringHost(engine, scheduler, requests));

        var task = engine.EvaluateWithHostContinuationsAsync(
            "host('dispose');",
            scheduler,
            static (_, value) => value.AsString());
        var pending = Dequeue(requests, "host", "dispose");

        engine.Dispose();
        Assert.Throws<ObjectDisposedException>(() => task.GetAwaiter().GetResult());

        var posts = scheduler.PostCount;
        pending.Completion.TrySetResult("late");
        Assert.False(SpinWait.SpinUntil(
            () => scheduler.PostCount != posts,
            TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void RejectsInlineScheduler()
    {
        var scheduler = new InlineOwnerScheduler();
        var engine = new Engine();
        var host = engine.Advanced.CreateHostContinuationFunction(
            "host",
            static (_, _, _) => new ValueTask<object?>("ok"),
            resultConverter: static (_, value) => JsString.Create((string) value!));
        engine.SetValue("host", host);

        var task = engine.EvaluateWithHostContinuationsAsync(
            "host();",
            scheduler,
            static (_, value) => value.AsString());

        var exception = Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
        Assert.Contains("inline", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongThreadSchedulerFaultsWithoutExecutingJavaScriptThere()
    {
        using var scheduler = new WrongThreadScheduler();
        var engine = new Engine();
        var afterHost = 0;
        engine.SetValue("mark", new Action(() => afterHost++));
        var host = engine.Advanced.CreateHostContinuationFunction(
            "host",
            static (_, _, _) => new ValueTask<object?>("ok"),
            resultConverter: static (_, value) => JsString.Create((string) value!));
        engine.SetValue("host", host);

        var task = engine.EvaluateWithHostContinuationsAsync(
            "host(); mark();",
            scheduler,
            static (_, value) => value.AsString());

        Assert.True(SpinWait.SpinUntil(() => task.IsCompleted, TestTimeout));
        var exception = Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
        Assert.Contains("owner", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, afterHost);

        // Wrong-thread delivery intentionally quarantines the Engine until the real owner disposes it.
        engine.Dispose();
    }

    [Theory]
    [InlineData("eval(\"host('x')\")", "eval")]
    [InlineData("const indirect = eval; indirect(\"host('x')\")", "eval")]
    [InlineData("[1].map(() => host('x'))", "direct synchronous call chain")]
    [InlineData("function f(){ return host('x'); } f.call(null)", "direct synchronous call chain")]
    [InlineData("class X { constructor(){ this.x = host('x'); } } new X()", "direct synchronous call chain")]
    public void RejectsUnauditedNativeBoundaries(string script, string messagePart)
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var calls = 0;
        var host = engine.Advanced.CreateHostContinuationFunction(
            "host",
            (_, _, _) =>
            {
                calls++;
                return new ValueTask<object?>("unexpected");
            });
        engine.SetValue("host", host);

        var task = engine.EvaluateWithHostContinuationsAsync(
            script,
            scheduler,
            static (_, value) => value.ToString());

        Assert.True(task.IsCompleted);
        var exception = Assert.Throws<JavaScriptException>(() => task.GetAwaiter().GetResult());
        Assert.Contains(messagePart, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, calls);
        Assert.Equal(2, engine.Evaluate("1 + 1").AsNumber());
    }

    [Fact]
    public void RejectsSuspensionDuringParameterInitializationAndAbandonsOperation()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var requests = new Queue<PendingRequest>();
        engine.SetValue("host", CreateDeferredStringHost(engine, scheduler, requests));

        var task = engine.EvaluateWithHostContinuationsAsync(
            "function f(value = host('default')) { return value; } f();",
            scheduler,
            static (_, value) => value.AsString());

        Assert.True(task.IsCompleted);
        var exception = Assert.Throws<JavaScriptException>(() => task.GetAwaiter().GetResult());
        Assert.Contains("initializing function parameters", exception.Message, StringComparison.OrdinalIgnoreCase);

        var pending = Dequeue(requests, "host", "default");
        var postCount = scheduler.PostCount;
        pending.Completion.TrySetResult("late");
        Assert.False(SpinWait.SpinUntil(
            () => scheduler.PostCount != postCount,
            TimeSpan.FromMilliseconds(100)));
        Assert.Equal(2, engine.Evaluate("1 + 1").AsNumber());
    }

    [Fact]
    public void DefaultResultConverterRejectsJsValueFromBackgroundContract()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var host = engine.Advanced.CreateHostContinuationFunction(
            "host",
            static (_, _, _) => new ValueTask<object?>((object) JsString.Create("not-clr-data")));
        engine.SetValue("host", host);

        var task = engine.EvaluateWithHostContinuationsAsync(
            "host();",
            scheduler,
            static (_, value) => value.AsString());
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);

        var exception = Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
        Assert.Contains("CLR data", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HostContinuationFunction CreateDeferredStringHost(
        Engine engine,
        ManualOwnerScheduler scheduler,
        Queue<PendingRequest> requests)
    {
        return engine.Advanced.CreateHostContinuationFunction(
            "host",
            (_, arguments, _) =>
            {
                Assert.True(scheduler.CheckAccess());
                var request = new PendingRequest("host", arguments.Length == 0 ? string.Empty : arguments[0].ToString());
                requests.Enqueue(request);
                return new ValueTask<object?>(request.Completion.Task);
            },
            resultConverter: static (_, value) => value switch
            {
                int number => JsNumber.Create(number),
                double number => JsNumber.Create(number),
                string text => JsString.Create(text),
                _ => JsValue.Undefined
            });
    }

    [Fact]
    public void ImportsPreparedModuleWithoutSuspension()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var prepared = Engine.PrepareModule("export const result = 42;");
        engine.Modules.Add("entry", builder => builder.AddModule(prepared));

        var task = engine.ImportModuleWithHostContinuationsAsync(
            "entry",
            scheduler,
            static (_, ns) => ns.AsObject().Get("result").AsNumber());

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(42, task.GetAwaiter().GetResult());
    }

    [Fact]
    public void ModuleSynchronousHostCompletionUsesLaterOwnerTurn()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        engine.SetValue("host", engine.Advanced.CreateHostContinuationFunction(
            "host",
            static (_, _, _) => new ValueTask<object?>("ok"),
            resultConverter: static (_, value) => JsString.Create((string) value!)));
        engine.Modules.Add("entry", "export const result = host();");

        var task = engine.ImportModuleWithHostContinuationsAsync(
            "entry",
            scheduler,
            static (_, ns) => ns.AsObject().Get("result").AsString());

        Assert.False(task.IsCompleted);
        Assert.Equal(1, scheduler.PendingCount);
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.Equal("ok", task.GetAwaiter().GetResult());
    }

    [Fact]
    public void ModuleDependencyGraphSuspendsAndResumes()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var requests = new Queue<PendingRequest>();
        engine.SetValue("host", CreateDeferredStringHost(engine, scheduler, requests));
        engine.Modules.Add("dependency", "export const value = host('dependency');");
        engine.Modules.Add("entry", "import { value } from 'dependency'; export const result = value + ':entry';");

        var task = engine.ImportModuleWithHostContinuationsAsync(
            "entry",
            scheduler,
            static (_, ns) => ns.AsObject().Get("result").AsString());

        Assert.False(task.IsCompleted);
        CompleteNext(requests, "host", "dependency", "resolved");
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.Equal("resolved:entry", task.GetAwaiter().GetResult());
    }

    [Fact]
    public void WrapperModuleCanInvokeImportedDefaultFunctionThatSuspends()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var requests = new Queue<PendingRequest>();
        engine.SetValue("host", CreateDeferredStringHost(engine, scheduler, requests));
        engine.Modules.Add("workflow", "export default function run(value) { return host(value) + ':workflow'; }");
        engine.Modules.Add("entry", "import run from 'workflow'; export const result = run('request');");

        var task = engine.ImportModuleWithHostContinuationsAsync(
            "entry",
            scheduler,
            static (_, ns) => ns.AsObject().Get("result").AsString());

        CompleteNext(requests, "host", "request", "response");
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.Equal("response:workflow", task.GetAwaiter().GetResult());
    }

    [Fact]
    public void DefaultExportExpressionIsInitializedAfterSuspendedConditionalResumes()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        var requests = new Queue<PendingRequest>();
        engine.SetValue("host", CreateDeferredStringHost(engine, scheduler, requests));
        engine.Modules.Add("workflow", "export default function run(value) { return host(value) + ':module'; }");
        engine.Modules.Add(
            "entry",
            """
            import * as workflow from 'workflow';
            const entry = workflow.default;
            export default typeof entry === 'function' ? entry('request') : undefined;
            """);

        var task = engine.ImportModuleWithHostContinuationsAsync(
            "entry",
            scheduler,
            static (_, ns) => ns.AsObject().Get("default").AsString());

        CompleteNext(requests, "host", "request", "response");
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.Equal("response:module", task.GetAwaiter().GetResult());
    }

    [Fact]
    public void ModuleRunRejectsOverlapAndCancellationIgnoresLateCompletion()
    {
        using var scheduler = new ManualOwnerScheduler();
        using var cancellation = new CancellationTokenSource();
        var engine = new Engine();
        var requests = new Queue<PendingRequest>();
        engine.SetValue("host", CreateDeferredStringHost(engine, scheduler, requests));
        engine.Modules.Add("entry", "export const result = host('request');");
        engine.Modules.Add("other", "export const result = 1;");

        var task = engine.ImportModuleWithHostContinuationsAsync(
            "entry",
            scheduler,
            static (_, ns) => ns.AsObject().Get("result").AsString(),
            cancellation.Token);

        var overlap = Assert.Throws<InvalidOperationException>(
            () => { _ = engine.ImportModuleWithHostContinuationsAsync("other", scheduler); });
        Assert.Contains("active implicit host-continuation run", overlap.Message);

        cancellation.Cancel();
        scheduler.RunUntil(() => task.IsCompleted, TestTimeout);
        Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());

        var postCount = scheduler.PostCount;
        CompleteNext(requests, "host", "request", "late");
        Thread.Sleep(50);
        Assert.Equal(postCount, scheduler.PostCount);
    }

    [Fact]
    public void ModuleImportRejectsTopLevelAwaitBeforeExecution()
    {
        using var scheduler = new ManualOwnerScheduler();
        var engine = new Engine();
        engine.Modules.Add("dependency", "await Promise.resolve(); export const value = 1;");
        engine.Modules.Add("entry", "import { value } from 'dependency'; export { value };");

        var exception = Assert.Throws<NotSupportedException>(
            () => { _ = engine.ImportModuleWithHostContinuationsAsync("entry", scheduler); });

        Assert.Contains("Top-level await", exception.Message);
    }

    private static void CompleteNext(
        Queue<PendingRequest> requests,
        string expectedName,
        string expectedArgument,
        object? result)
    {
        var request = Dequeue(requests, expectedName, expectedArgument);
        request.Completion.SetResult(result);
    }

    private static PendingRequest Dequeue(
        Queue<PendingRequest> requests,
        string expectedName,
        string expectedArgument)
    {
        Assert.NotEmpty(requests);
        var request = requests.Dequeue();
        Assert.Equal(expectedName, request.Name);
        Assert.Equal(expectedArgument, request.Argument);
        return request;
    }

    private sealed record ResourceResult(string Content);

    private sealed class PendingRequest
    {
        public PendingRequest(string name, string argument)
        {
            Name = name;
            Argument = argument;
        }

        public string Name { get; }
        public string Argument { get; }
        public TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ManualOwnerScheduler : IHostContinuationScheduler, IDisposable
    {
        private readonly ConcurrentQueue<Action> _callbacks = new();
        private readonly AutoResetEvent _posted = new(false);
        private int _postCount;

        public ManualOwnerScheduler()
        {
            OwnerThreadId = Environment.CurrentManagedThreadId;
        }

        public int OwnerThreadId { get; }
        public int PostCount => Volatile.Read(ref _postCount);
        public int PendingCount => _callbacks.Count;
        public bool CheckAccess() => Environment.CurrentManagedThreadId == OwnerThreadId;

        public void Post(Action callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            _callbacks.Enqueue(callback);
            Interlocked.Increment(ref _postCount);
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

                if (!_callbacks.TryDequeue(out var callback))
                {
                    _posted.WaitOne(remaining > TimeSpan.FromMilliseconds(100)
                        ? TimeSpan.FromMilliseconds(100)
                        : remaining);
                    continue;
                }

                callback();
            }
        }

        public void Dispose() => _posted.Dispose();
    }

    private sealed class InlineOwnerScheduler : IHostContinuationScheduler
    {
        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
        public bool CheckAccess() => Environment.CurrentManagedThreadId == _ownerThreadId;
        public void Post(Action callback) => callback();
    }

    private sealed class WrongThreadScheduler : IHostContinuationScheduler, IDisposable
    {
        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
        private readonly CancellationTokenSource _dispose = new();

        public bool CheckAccess() => Environment.CurrentManagedThreadId == _ownerThreadId;

        public void Post(Action callback)
        {
            _ = Task.Run(callback, _dispose.Token);
        }

        public void Dispose() => _dispose.Dispose();
    }
}
