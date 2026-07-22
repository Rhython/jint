using System.Threading;
using Jint.Native;
using Jint.Native.Function;
using Jint.Runtime.Environments;
using Jint.Runtime.Interpreter;
using ExecutionContext = Jint.Runtime.Environments.ExecutionContext;

namespace Jint.Runtime.Continuations;

/// <summary>
/// One logical JavaScript frame in an implicit host-continuation run.
/// </summary>
internal sealed class HostContinuationFrame : ISuspendable
{
    private HostContinuationFrameState _state = HostContinuationFrameState.Running;
    private bool _isResuming;
    private object? _lastSuspensionNode;
    private ExecutionContext? _savedContext;
    private HostOperation? _pendingOperation;
    private HostContinuationFrame? _pendingChild;
    private HostOperation? _suspendedOperation;
    private HostContinuationFrame? _suspendedChild;
    private int _aborted;

    internal HostContinuationFrame(
        HostContinuationRun run,
        HostContinuationFrame? parent,
        ScriptFunction? function = null,
        JintFunctionDefinition? definition = null,
        JintFunctionDefinition.State? functionState = null)
    {
        Run = run;
        Parent = parent;
        Function = function;
        Definition = definition;
        FunctionState = functionState;
    }

    internal HostContinuationRun Run { get; }
    internal HostContinuationFrame? Parent { get; }
    internal ScriptFunction? Function { get; }
    internal JintFunctionDefinition? Definition { get; }
    internal JintFunctionDefinition.State? FunctionState { get; }
    internal bool BindingsInitialized { get; set; }
    internal JsArguments? ArgumentsInstance { get; set; }
    internal JsValue Result { get; private set; } = JsValue.Undefined;
    internal bool IsSuspended => _state == HostContinuationFrameState.Suspended;
    internal bool IsResuming => _isResuming;
    internal bool IsCompleted => _state == HostContinuationFrameState.Completed;

    internal HostContinuationFrame CreateChild(
        ScriptFunction function,
        JintFunctionDefinition definition,
        JintFunctionDefinition.State functionState)
        => new(Run, this, function, definition, functionState);

    internal void CaptureContext(in ExecutionContext context) => _savedContext = context;

    internal ExecutionContext TakeSavedContext()
    {
        if (!_savedContext.HasValue)
        {
            throw new InvalidOperationException("The host continuation frame has no saved execution context.");
        }

        var context = _savedContext.Value;
        _savedContext = null;
        return context;
    }

    internal void PrepareResume()
    {
        if (_state != HostContinuationFrameState.Suspended)
        {
            Throw.InvalidOperationException("Only a suspended host continuation frame can be resumed.");
        }

        _state = HostContinuationFrameState.Running;
        _isResuming = true;
    }

    internal void Suspend(object suspensionNode)
    {
        _lastSuspensionNode = suspensionNode;
        _isResuming = false;
        _state = HostContinuationFrameState.Suspended;
    }

    internal void ConsumeResumePoint(object suspensionNode)
    {
        if (!_isResuming || !ReferenceEquals(_lastSuspensionNode, suspensionNode))
        {
            Throw.InvalidOperationException("The host continuation resumed at an unexpected call expression.");
        }

        _isResuming = false;
        _lastSuspensionNode = null;
        _state = HostContinuationFrameState.Running;
    }

    internal void Complete(JsValue result)
    {
        Result = result;
        _state = HostContinuationFrameState.Completed;
        _isResuming = false;
        _lastSuspensionNode = null;
        _savedContext = null;
        _pendingOperation = null;
        _pendingChild = null;
        _suspendedOperation = null;
        _suspendedChild = null;
        ArgumentsInstance?.FunctionWasCalled();
        ArgumentsInstance = null;
        Data.ClearAll();
    }

    internal void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
        {
            return;
        }

        _state = HostContinuationFrameState.Completed;
        _isResuming = false;
        _lastSuspensionNode = null;
        _savedContext = null;

        _pendingOperation?.Abandon();
        if (!ReferenceEquals(_suspendedOperation, _pendingOperation))
        {
            _suspendedOperation?.Abandon();
        }

        _pendingChild?.Abort();
        if (!ReferenceEquals(_suspendedChild, _pendingChild))
        {
            _suspendedChild?.Abort();
        }

        _pendingOperation = null;
        _pendingChild = null;
        _suspendedOperation = null;
        _suspendedChild = null;
        ArgumentsInstance?.FunctionWasCalled();
        ArgumentsInstance = null;
        Data.ClearAll();
    }

    internal void SetPendingOperation(HostOperation operation)
    {
        if (_pendingOperation is not null || _pendingChild is not null)
        {
            Throw.InvalidOperationException("A host continuation frame already has an unclaimed suspended call.");
        }
        _pendingOperation = operation;
    }

    internal bool TryTakePendingOperation(out HostOperation? operation)
    {
        operation = _pendingOperation;
        _pendingOperation = null;
        return operation is not null;
    }

    internal void SetPendingChild(HostContinuationFrame child)
    {
        if (_pendingOperation is not null || _pendingChild is not null)
        {
            Throw.InvalidOperationException("A host continuation frame already has an unclaimed suspended call.");
        }
        _pendingChild = child;
    }

    internal bool TryTakePendingChild(out HostContinuationFrame? child)
    {
        child = _pendingChild;
        _pendingChild = null;
        return child is not null;
    }

    internal void TrackSuspendedOperation(HostOperation operation)
    {
        if (_suspendedOperation is not null || _suspendedChild is not null)
        {
            Throw.InvalidOperationException("A host continuation frame already tracks a suspended call.");
        }
        _suspendedOperation = operation;
    }

    internal void ReleaseSuspendedOperation(HostOperation operation)
    {
        if (!ReferenceEquals(_suspendedOperation, operation))
        {
            Throw.InvalidOperationException("The resumed host operation is not owned by this continuation frame.");
        }
        _suspendedOperation = null;
    }

    internal void TrackSuspendedChild(HostContinuationFrame child)
    {
        if (_suspendedOperation is not null || _suspendedChild is not null)
        {
            Throw.InvalidOperationException("A host continuation frame already tracks a suspended call.");
        }
        _suspendedChild = child;
    }

    internal void ReleaseSuspendedChild(HostContinuationFrame child)
    {
        if (!ReferenceEquals(_suspendedChild, child))
        {
            Throw.InvalidOperationException("The resumed child frame is not owned by this continuation frame.");
        }
        _suspendedChild = null;
    }

    public SuspendDataDictionary Data { get; } = new();
    bool ISuspendable.IsSuspended => IsSuspended;
    bool ISuspendable.IsResuming { get => _isResuming; set => _isResuming = value; }
    JsValue? ISuspendable.SuspendedValue => JsValue.Undefined;
    object? ISuspendable.LastSuspensionNode => _lastSuspensionNode;
    bool ISuspendable.ReturnRequested => false;
    CompletionType ISuspendable.PendingCompletionType { get; set; }
    JsValue? ISuspendable.PendingCompletionValue { get; set; }
    object? ISuspendable.CurrentFinallyStatement { get; set; }
}

internal enum HostContinuationFrameState : byte
{
    Running,
    Suspended,
    Completed
}

/// <summary>
/// Completion of one host operation. It contains CLR-only state and is safe to populate away from
/// the engine owner thread.
/// </summary>
internal sealed class HostOperation
{
    private readonly HostContinuationRun _run;
    private readonly HostContinuationResultConverter _resultConverter;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private HostOperationCompletion? _completion;
    private int _consumed;
    private int _abandoned;

    internal HostOperation(
        HostContinuationRun run,
        ValueTask<object?> pending,
        HostContinuationResultConverter resultConverter)
    {
        _run = run;
        _resultConverter = resultConverter;
        if (run.CancellationToken.CanBeCanceled)
        {
            var registration = run.CancellationToken.Register(
                static state => ((HostOperation) state!)._run.ScheduleCancellation(),
                this);
            _cancellationRegistration = registration;
            if (run.CancellationToken.IsCancellationRequested)
            {
                registration.Dispose();
            }
        }

        Observe(pending);
    }

    private void Observe(ValueTask<object?> pending)
    {
        if (pending.IsCompleted)
        {
            try
            {
                TryComplete(new HostOperationCompletion(pending.GetAwaiter().GetResult(), null));
            }
            catch (Exception exception)
            {
                TryComplete(new HostOperationCompletion(null, exception));
            }
            return;
        }
        _ = ObserveSlowAsync(this, pending);
    }

    private static async Task ObserveSlowAsync(HostOperation operation, ValueTask<object?> pending)
    {
        try
        {
            var value = await pending.ConfigureAwait(false);
            operation.TryComplete(new HostOperationCompletion(value, null));
        }
        catch (Exception exception)
        {
            operation.TryComplete(new HostOperationCompletion(null, exception));
        }
    }

    private void TryComplete(HostOperationCompletion completion)
    {
        if (Volatile.Read(ref _abandoned) != 0)
        {
            _cancellationRegistration.Dispose();
            return;
        }

        if (Interlocked.CompareExchange(ref _completion, completion, null) is null)
        {
            _cancellationRegistration.Dispose();
            if (Volatile.Read(ref _abandoned) == 0)
            {
                _run.ScheduleResume();
            }
        }
    }

    internal JsValue ConsumeAndConvert(Engine engine)
    {
        var completion = Volatile.Read(ref _completion);
        if (completion is null)
        {
            Throw.InvalidOperationException("The host operation was resumed before it completed.");
        }
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
        {
            Throw.InvalidOperationException("The host operation completion was consumed more than once.");
        }
        if (completion.Exception is { } exception)
        {
            Throw.FromClrException(engine, exception);
        }

        var value = _resultConverter(engine, completion.Result);
        if (value is null)
        {
            Throw.InvalidOperationException("A host-continuation result converter returned null instead of JsValue.");
        }
        return value;
    }

    internal void Abandon()
    {
        if (Interlocked.Exchange(ref _abandoned, 1) != 0)
        {
            return;
        }
        _cancellationRegistration.Dispose();
    }
}

internal sealed class HostOperationCompletion
{
    internal HostOperationCompletion(object? result, Exception? exception)
    {
        Result = result;
        Exception = exception;
    }
    internal object? Result { get; }
    internal Exception? Exception { get; }
}

internal sealed class HostNewSuspendData : SuspendData
{
    internal JsValue Constructor { get; set; } = JsValue.Undefined;
}

internal enum HostCallSuspendStage : byte
{
    None,
    Arguments,
    Operation,
    Child
}

internal sealed class HostCallSuspendData : SuspendData
{
    internal HostCallSuspendStage Stage { get; set; }
    internal object? Reference { get; set; }
    internal Reference? ReferenceRecord { get; set; }
    internal JsValue? Function { get; set; }
    internal JsValue ThisObject { get; set; } = JsValue.Undefined;
    internal HostOperation? Operation { get; set; }
    internal HostContinuationFrame? ChildFrame { get; set; }

    internal void ClearCallTarget()
    {
        Reference = null;
        ReferenceRecord = null;
        Function = null;
        ThisObject = JsValue.Undefined;
    }
}

/// <summary>
/// One active implicit host-continuation evaluation. Only one run may own an Engine at a time.
/// </summary>
internal sealed class HostContinuationRun
{
    private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<Engine, JsValue, object?> _completionConverter;
    private int _resumeScheduled;
    private int _postInProgress;
    private int _postThreadId;
    private int _cancellationRequested;
    private bool _sliceRunning;
    private int _ownerAccessDepth;
    private int _completed;
    private int _cleaned;
    private Exception? _deferredOwnerFailure;

    internal HostContinuationRun(
        Engine engine,
        IHostContinuationScheduler scheduler,
        bool strict,
        ScriptRecord scriptRecord,
        ParserOptions parserOptions,
        Func<Engine, JsValue, object?> completionConverter,
        CancellationToken cancellationToken)
    {
        Engine = engine;
        Scheduler = scheduler;
        CancellationToken = cancellationToken;
        Strict = strict;
        ScriptRecord = scriptRecord;
        ParserOptions = parserOptions;
        _completionConverter = completionConverter;
        OwnerThreadId = System.Environment.CurrentManagedThreadId;
        Root = new HostContinuationFrame(this, parent: null);
    }

    internal Engine Engine { get; }
    internal IHostContinuationScheduler Scheduler { get; }
    internal CancellationToken CancellationToken { get; }
    internal bool Strict { get; }
    internal ScriptRecord ScriptRecord { get; }
    internal ParserOptions ParserOptions { get; }
    internal int OwnerThreadId { get; }
    internal HostContinuationFrame Root { get; }
    internal JintStatementList? Body { get; set; }
    internal Task<object?> Completion => _completion.Task;
    internal bool IsCompleted => Volatile.Read(ref _completed) != 0;
    internal bool IsSliceRunning => _sliceRunning;
    internal bool EngineAccessAllowed => Volatile.Read(ref _ownerAccessDepth) > 0;

    internal HostOperation BeginOperation(
        HostContinuationHandler handler,
        HostContinuationResultConverter resultConverter,
        JsValue thisObject,
        JsCallArguments arguments)
    {
        VerifyOwnerThread();
        JsValue[] argumentSnapshot;
        if (arguments.Length == 0)
        {
            argumentSnapshot = Array.Empty<JsValue>();
        }
        else
        {
            argumentSnapshot = new JsValue[arguments.Length];
            for (var i = 0; i < arguments.Length; i++)
            {
                argumentSnapshot[i] = arguments[i];
            }
        }

        ValueTask<object?> pending;
        try
        {
            pending = handler(thisObject, argumentSnapshot, CancellationToken);
        }
        catch (Exception exception)
        {
            pending = new ValueTask<object?>(Task.FromException<object?>(exception));
        }
        finally
        {
            if (argumentSnapshot.Length != 0)
            {
                Array.Clear(argumentSnapshot, 0, argumentSnapshot.Length);
            }
        }

        return new HostOperation(this, pending, resultConverter);
    }

    internal void ScheduleCancellation()
    {
        Interlocked.Exchange(ref _cancellationRequested, 1);
        ScheduleResume();
    }

    internal void ScheduleResume()
    {
        if (IsCompleted || Interlocked.Exchange(ref _resumeScheduled, 1) != 0)
        {
            return;
        }

        try
        {
            Volatile.Write(ref _postThreadId, System.Environment.CurrentManagedThreadId);
            Interlocked.Exchange(ref _postInProgress, 1);
            Scheduler.Post(ResumePostedCallback);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _resumeScheduled, 0);
            if (System.Environment.CurrentManagedThreadId == OwnerThreadId && Scheduler.CheckAccess())
            {
                Interlocked.CompareExchange(ref _deferredOwnerFailure, exception, null);
            }
            else
            {
                FailWithoutEngine(exception);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _postInProgress, 0);
            Volatile.Write(ref _postThreadId, 0);
        }
    }

    private void ResumePostedCallback()
    {
        Interlocked.Exchange(ref _resumeScheduled, 0);
        if (IsCompleted)
        {
            return;
        }

        if (Volatile.Read(ref _postInProgress) != 0
            && System.Environment.CurrentManagedThreadId == Volatile.Read(ref _postThreadId))
        {
            var exception = new InvalidOperationException(
                "IHostContinuationScheduler.Post executed inline. Post must enqueue a later owner-thread turn.");
            if (System.Environment.CurrentManagedThreadId == OwnerThreadId && Scheduler.CheckAccess())
            {
                if (_sliceRunning)
                {
                    Interlocked.CompareExchange(ref _deferredOwnerFailure, exception, null);
                }
                else
                {
                    Engine.FailHostContinuationRun(this, exception);
                }
            }
            else
            {
                FailWithoutEngine(exception);
            }
            return;
        }

        try
        {
            VerifyOwnerThread();
            if (Volatile.Read(ref _cancellationRequested) != 0)
            {
                Engine.FailHostContinuationRun(this, new OperationCanceledException(CancellationToken));
                return;
            }
            Engine.ResumeHostContinuationRun(this);
        }
        catch (Exception exception)
        {
            if (System.Environment.CurrentManagedThreadId != OwnerThreadId || !Scheduler.CheckAccess())
            {
                FailWithoutEngine(exception);
            }
            else
            {
                Engine.FailHostContinuationRun(this, exception);
            }
        }
    }

    internal void VerifyOwnerThread()
    {
        if (System.Environment.CurrentManagedThreadId != OwnerThreadId || !Scheduler.CheckAccess())
        {
            Throw.InvalidOperationException(
                "The Jint Engine and its host continuation must only be accessed on the owner event-loop thread.");
        }
    }

    internal bool TryBeginSlice()
    {
        VerifyOwnerThread();
        if (IsCompleted)
        {
            return false;
        }
        if (_sliceRunning)
        {
            Throw.InvalidOperationException("A host-continuation execution slice is already running.");
        }
        _sliceRunning = true;
        Interlocked.Increment(ref _ownerAccessDepth);
        return true;
    }

    internal void EndSlice()
    {
        _sliceRunning = false;
        if (Interlocked.Decrement(ref _ownerAccessDepth) < 0)
        {
            Throw.InvalidOperationException("The host-continuation owner access scope became unbalanced.");
        }
    }

    internal void BeginOwnerCleanup()
    {
        VerifyOwnerThread();
        Interlocked.Increment(ref _ownerAccessDepth);
    }

    internal void EndOwnerCleanup()
    {
        if (Interlocked.Decrement(ref _ownerAccessDepth) < 0)
        {
            Throw.InvalidOperationException("The host-continuation cleanup scope became unbalanced.");
        }
    }

    internal void DeferOwnerFailure(Exception exception)
    {
        Interlocked.CompareExchange(ref _deferredOwnerFailure, exception, null);
    }

    internal bool TryTakeDeferredOwnerFailure(out Exception? exception)
    {
        exception = Interlocked.Exchange(ref _deferredOwnerFailure, null);
        return exception is not null;
    }

    internal void Complete(JsValue value)
    {
        object? converted;
        try
        {
            VerifyOwnerThread();
            converted = _completionConverter(Engine, value);
        }
        catch (Exception exception)
        {
            Fail(exception);
            return;
        }

        Cleanup();
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _completion.TrySetResult(converted);
        }
    }

    internal void Fail(Exception exception)
    {
        Cleanup();
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        if (exception is OperationCanceledException)
        {
            _completion.TrySetCanceled();
        }
        else
        {
            _completion.TrySetException(exception);
        }
    }

    internal void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) == 0)
        {
            Root.Abort();
        }
    }

    private void FailWithoutEngine(Exception exception)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _completion.TrySetException(exception);
        }
    }
}
