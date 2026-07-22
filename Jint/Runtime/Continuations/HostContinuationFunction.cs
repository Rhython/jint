using System.Threading;
using Jint.Native;
using Jint.Native.Function;
using Jint.Runtime.Descriptors;

namespace Jint.Runtime.Continuations;

/// <summary>
/// Delegate used by <see cref="HostContinuationFunction"/> to begin a non-blocking host operation.
/// </summary>
/// <remarks>
/// The delegate is invoked on the engine owner thread. It must synchronously copy or convert every
/// <see cref="JsValue"/> it needs before returning. The returned asynchronous operation may complete
/// on any thread, but it must not retain or access the engine, JavaScript values, or thread-affine
/// host state after this delegate returns.
/// </remarks>
public delegate ValueTask<object?> HostContinuationHandler(
    JsValue thisObject,
    JsValue[] arguments,
    CancellationToken cancellationToken);

/// <summary>
/// Converts a completed CLR operation result into a JavaScript value on the engine owner thread.
/// </summary>
/// <remarks>
/// Supplying an explicit converter is recommended for NativeAOT applications because it avoids
/// reflection-based object wrapping and makes the reachable host surface auditable.
/// </remarks>
public delegate JsValue HostContinuationResultConverter(Engine engine, object? result);

/// <summary>
/// A host function whose asynchronous result is injected back into an unchanged, synchronous-looking
/// JavaScript call expression by the implicit host-continuation runtime.
/// </summary>
/// <remarks>
/// Instances are only valid while executing through
/// <see cref="Engine.EvaluateWithHostContinuationsAsync(string,IHostContinuationScheduler,string?,CancellationToken)"/>.
/// Invoking one from normal <see cref="Engine.Evaluate(string,string?)"/> execution, an explicit
/// JavaScript async function, a generator, or an unsupported native callback boundary is rejected.
/// </remarks>
public sealed class HostContinuationFunction : Function
{
    private readonly HostContinuationHandler _handler;
    private readonly HostContinuationResultConverter _resultConverter;

    /// <summary>
    /// Creates a new host-continuation function.
    /// </summary>
    public HostContinuationFunction(
        Engine engine,
        string name,
        HostContinuationHandler handler,
        int length = 0,
        HostContinuationResultConverter? resultConverter = null)
        : base(engine, engine.Realm, JsString.CachedCreate(ValidateName(name)))
    {
        if (handler is null)
        {
            Throw.ArgumentNullException(nameof(handler));
        }
        if (length < 0)
        {
            Throw.ArgumentOutOfRangeException(nameof(length), "Function length must not be negative.");
        }

        _handler = handler;
        _resultConverter = resultConverter ?? DefaultResultConverter;
        _prototype = engine._originalIntrinsics.Function.PrototypeObject;
        _length = PropertyDescriptor.AllForbiddenDescriptor.ForNumber(length);
    }

    private static string ValidateName(string name)
    {
        if (name is null)
        {
            Throw.ArgumentNullException(nameof(name));
        }
        return name;
    }

    protected internal override JsValue Call(JsValue thisObject, JsCallArguments arguments)
    {
        var run = _engine.ActiveHostContinuationRun;
        var frame = _engine.ExecutionContext.HostContinuationFrame;

        if (run is null
            || frame is null
            || !ReferenceEquals(frame.Run, run)
            || !ReferenceEquals(_engine.ExecutionContext.Suspendable, frame))
        {
            Throw.Error(
                _engine,
                "HostContinuationFunction can only be called from the direct synchronous call chain of EvaluateWithHostContinuationsAsync.");
        }

        run.VerifyOwnerThread();

        var operation = run.BeginOperation(_handler, _resultConverter, thisObject, arguments);
        frame.SetPendingOperation(operation);
        return Undefined;
    }

    private static JsValue DefaultResultConverter(Engine engine, object? result)
    {
        if (result is JsValue)
        {
            Throw.InvalidOperationException(
                "A host-continuation operation must return CLR data, not JsValue. " +
                "Provide an explicit owner-thread result converter when returning an existing JavaScript value is intentional.");
        }

        return JsValue.FromObject(engine, result);
    }
}
