using System.Threading;
using Jint.Native;
using Jint.Runtime;
using Jint.Runtime.Continuations;
using Jint.Runtime.Environments;
using Jint.Runtime.Interpreter;
using ExecutionContext = Jint.Runtime.Environments.ExecutionContext;

namespace Jint;

public sealed partial class Engine
{
    private HostContinuationRun? _activeHostContinuationRun;

    /// <summary>
    /// The active experimental implicit host-continuation run, if any.
    /// </summary>
    internal HostContinuationRun? ActiveHostContinuationRun => _activeHostContinuationRun;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void VerifyHostContinuationThreadAccess()
    {
        if (_activeHostContinuationRun is not { } run)
        {
            return;
        }

        run.VerifyOwnerThread();
        if (!run.EngineAccessAllowed)
        {
            Throw.InvalidOperationException(
                "This Engine is suspended and owned by an implicit host-continuation run. " +
                "It may only be accessed by the scheduled owner-thread execution slice.");
        }
    }

    /// <summary>
    /// Evaluates JavaScript that may call <see cref="HostContinuationFunction"/> instances without
    /// adding <c>async</c>/<c>await</c> to the JavaScript source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Execution starts synchronously on the caller, then returns to the host whenever a
    /// <see cref="HostContinuationFunction"/> begins an incomplete operation. The supplied scheduler
    /// must later resume the engine on the exact same owner thread.
    /// </para>
    /// <para>
    /// Only one host-continuation run may own an <see cref="Engine"/> at a time. While the returned
    /// task is incomplete, callers must not invoke other Engine APIs.
    /// </para>
    /// </remarks>
    public Task<JsValue> EvaluateWithHostContinuationsAsync(
        string code,
        IHostContinuationScheduler scheduler,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        if (code is null)
        {
            Throw.ArgumentNullException(nameof(code));
        }
        ValidateHostContinuationStart(scheduler);

        var script = _defaultParser.ParseScriptGuarded(
            Realm,
            code,
            source: source ?? "<anonymous>",
            strict: _isStrict);
        return EvaluateWithHostContinuationsCore(
            new Prepared<Script>(script, _defaultParser.Options),
            scheduler,
            static (_, value) => value,
            cancellationToken);
    }

    /// <summary>
    /// Evaluates a prepared script using the experimental implicit host-continuation runtime.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="JsValue"/> remains owned by the Engine thread. NativeAOT and
    /// thread-affine applications should prefer the generic overload and convert the result to a
    /// CLR DTO before the task is completed.
    /// </remarks>
    public Task<JsValue> EvaluateWithHostContinuationsAsync(
        in Prepared<Script> preparedScript,
        IHostContinuationScheduler scheduler,
        CancellationToken cancellationToken = default)
    {
        return EvaluateWithHostContinuationsCore(
            preparedScript,
            scheduler,
            static (_, value) => value,
            cancellationToken);
    }

    /// <summary>
    /// Evaluates JavaScript and converts the final value to CLR data on the Engine owner thread
    /// before completing the returned task.
    /// </summary>
    public Task<TResult> EvaluateWithHostContinuationsAsync<TResult>(
        string code,
        IHostContinuationScheduler scheduler,
        Func<Engine, JsValue, TResult> completionConverter,
        string? source = null,
        CancellationToken cancellationToken = default)
    {
        if (code is null)
        {
            Throw.ArgumentNullException(nameof(code));
        }
        if (completionConverter is null)
        {
            Throw.ArgumentNullException(nameof(completionConverter));
        }
        ValidateHostContinuationStart(scheduler);

        var script = _defaultParser.ParseScriptGuarded(
            Realm,
            code,
            source: source ?? "<anonymous>",
            strict: _isStrict);
        return EvaluateWithHostContinuationsCore(
            new Prepared<Script>(script, _defaultParser.Options),
            scheduler,
            completionConverter,
            cancellationToken);
    }

    /// <summary>
    /// Evaluates a prepared script and converts the final value to CLR data on the Engine owner
    /// thread before completing the returned task.
    /// </summary>
    public Task<TResult> EvaluateWithHostContinuationsAsync<TResult>(
        in Prepared<Script> preparedScript,
        IHostContinuationScheduler scheduler,
        Func<Engine, JsValue, TResult> completionConverter,
        CancellationToken cancellationToken = default)
    {
        if (completionConverter is null)
        {
            Throw.ArgumentNullException(nameof(completionConverter));
        }
        return EvaluateWithHostContinuationsCore(
            preparedScript,
            scheduler,
            completionConverter,
            cancellationToken);
    }

    private Task<TResult> EvaluateWithHostContinuationsCore<TResult>(
        in Prepared<Script> preparedScript,
        IHostContinuationScheduler scheduler,
        Func<Engine, JsValue, TResult> completionConverter,
        CancellationToken cancellationToken)
    {
        ValidateHostContinuationStart(scheduler);
        if (!preparedScript.IsValid)
        {
            Throw.InvalidPreparedScriptArgumentException(nameof(preparedScript));
        }

        var script = preparedScript.Program;
        object? ConvertCompletion(Engine engine, JsValue value) => completionConverter(engine, value);
        var run = new HostContinuationRun(
            this,
            scheduler,
            _isStrict || script.Strict,
            new ScriptRecord(Realm, script, script.Location.SourceFile),
            preparedScript.ParserOptions,
            ConvertCompletion,
            cancellationToken);

        _activeHostContinuationRun = run;
        RunHostContinuationSlice(run, initial: true);
        return CastHostContinuationCompletion<TResult>(run.Completion);
    }

    private static async Task<TResult> CastHostContinuationCompletion<TResult>(Task<object?> completion)
    {
        var result = await completion.ConfigureAwait(false);
        return (TResult) result!;
    }

    private void ValidateHostContinuationStart(IHostContinuationScheduler scheduler)
    {
        if (scheduler is null)
        {
            Throw.ArgumentNullException(nameof(scheduler));
        }
        if (!scheduler.CheckAccess())
        {
            Throw.InvalidOperationException(
                "EvaluateWithHostContinuationsAsync must be started on the scheduler owner thread.");
        }
        if (_activeHostContinuationRun is not null)
        {
            Throw.InvalidOperationException(
                "This Engine already has an active implicit host-continuation run.");
        }
    }

    internal void ResumeHostContinuationRun(HostContinuationRun run)
    {
        if (!ReferenceEquals(_activeHostContinuationRun, run))
        {
            Throw.InvalidOperationException(
                "The requested host continuation no longer owns this Engine.");
        }
        RunHostContinuationSlice(run, initial: false);
    }

    internal void FailHostContinuationRun(HostContinuationRun run, Exception exception)
    {
        run.VerifyOwnerThread();
        if (!ReferenceEquals(_activeHostContinuationRun, run))
        {
            return;
        }
        if (run.IsSliceRunning)
        {
            run.DeferOwnerFailure(exception);
            return;
        }

        run.BeginOwnerCleanup();
        try
        {
            ResetCallStack();
            run.Fail(exception);
        }
        finally
        {
            run.EndOwnerCleanup();
        }
        FinishHostContinuationRun(run);
    }

    private void RunHostContinuationSlice(HostContinuationRun run, bool initial)
    {
        if (!run.TryBeginSlice())
        {
            return;
        }

        var contextActive = false;
        var ownsEvaluationContext = _activeEvaluationContext is null;
        try
        {
            if (!ownsEvaluationContext)
            {
                Throw.InvalidOperationException(
                    "Implicit host-continuation execution cannot re-enter an active Engine evaluation.");
            }

            run.CancellationToken.ThrowIfCancellationRequested();
            ResetConstraints();
            _activeEvaluationContext = new EvaluationContext(this);

            using (new StrictModeScope(run.Strict))
            {
                if (initial)
                {
                    Debugger.OnBeforeEvaluate(run.ScriptRecord.EcmaScriptCode);

                    var globalEnv = Realm.GlobalEnv;
                    var scriptContext = new ExecutionContext(
                        run.ScriptRecord,
                        lexicalEnvironment: globalEnv,
                        variableEnvironment: globalEnv,
                        privateEnvironment: null,
                        Realm,
                        parserOptions: run.ParserOptions,
                        hostContinuationFrame: run.Root);

                    EnterExecutionContext(scriptContext);
                    contextActive = true;

                    var script = run.ScriptRecord.EcmaScriptCode;
                    var reEvaluation = GlobalDeclarationInstantiation(script, globalEnv);
                    run.Body = GetOrBuildScriptStatementList(script, reEvaluation);
                }
                else
                {
                    run.Root.PrepareResume();
                    EnterExecutionContext(run.Root.TakeSavedContext());
                    contextActive = true;
                }

                var result = run.Body!.Execute(_activeEvaluationContext);
                if (run.Root.IsSuspended)
                {
                    run.Root.CaptureContext(ExecutionContext);
                    LeaveExecutionContext();
                    contextActive = false;

                    // Returning control to the host ends the current JavaScript job. Drain Jint's
                    // microtask queue on the owner thread before the external scheduler resumes the
                    // operation on a later turn.
                    RunAvailableContinuations();
                    return;
                }

                if (result.Type == CompletionType.Throw)
                {
                    var exception = new JavaScriptException(result.GetValueOrDefault())
                        .SetJavaScriptCallstack(this, result.Location);
                    ResetCallStack();
                    throw exception;
                }

                var completionValue = result.GetValueOrDefault();
                RunAvailableContinuations();
                run.Root.Complete(completionValue);
                run.Complete(completionValue);
            }
        }
        catch (Exception exception)
        {
            ResetCallStack();
            run.Fail(exception);
        }
        finally
        {
            if (contextActive)
            {
                LeaveExecutionContext();
            }
            if (ownsEvaluationContext)
            {
                _activeEvaluationContext = null;
            }

            ResetConstraints();
            _agent.ClearKeptObjects();
            run.EndSlice();

            if (!run.IsCompleted && run.TryTakeDeferredOwnerFailure(out var deferredFailure))
            {
                FailHostContinuationRun(run, deferredFailure!);
            }

            if (run.IsCompleted)
            {
                FinishHostContinuationRun(run);
            }
        }
    }

    private void FinishHostContinuationRun(HostContinuationRun run)
    {
        run.BeginOwnerCleanup();
        try
        {
            run.Cleanup();
            if (ReferenceEquals(_activeHostContinuationRun, run))
            {
                _activeHostContinuationRun = null;
            }
        }
        finally
        {
            run.EndOwnerCleanup();
        }
    }
}
