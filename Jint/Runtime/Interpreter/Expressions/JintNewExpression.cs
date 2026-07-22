using Jint.Native;
using Jint.Native.Function;
using Jint.Runtime.Continuations;

namespace Jint.Runtime.Interpreter.Expressions;

internal sealed class JintNewExpression : JintExpression
{
    private readonly ExpressionCache _arguments = new();
    private readonly JintExpression _calleeExpression;
    private readonly bool _zeroArgs;
    private readonly object _hostNewStateKey = new();

    // Monomorphic call-site cache: the constructor object seen by the last evaluation.
    // Identity pins the realm and the immutable per-instance IsConstructor /
    // IsZeroArgLeafConstructor verdicts, so a hit skips the constructor type-test — and for
    // zero-argument leaf built-ins (default-time-system Date) the whole Engine.Construct
    // call-stack ceremony, which such constructors can neither observe nor throw through.
    // A miss (callee reassigned, different engine sharing this prepared node) falls through
    // to the generic path and re-caches — never a permanent decline.
    private Function? _cachedConstructor;
    private bool _cachedLeafZeroArg;

    public JintNewExpression(NewExpression expression) : base(expression)
    {
        _arguments.Initialize(expression.Arguments.AsSpan());
        _calleeExpression = Build(expression.Callee);
        _zeroArgs = expression.Arguments.Count == 0;
    }

    protected override object EvaluateInternal(EvaluationContext context)
    {
        var engine = context.Engine;

        var hostFrame = engine.ExecutionContext.Suspendable as HostContinuationFrame;
        HostNewSuspendData? hostData = null;

        JsValue jsValue;
        if (hostFrame is { IsResuming: true }
            && hostFrame.Data.TryGet(_hostNewStateKey, out hostData))
        {
            jsValue = hostData!.Constructor;
        }
        else
        {
            // todo: optimize by defining a common abstract class or interface
            jsValue = _calleeExpression.GetValue(context);
        }

        var isCachedConstructor = ReferenceEquals(jsValue, _cachedConstructor);
        if (isCachedConstructor
            && _cachedLeafZeroArg
            && !engine._isDebugMode
            && engine.ExecutionContext.Suspendable is null)
        {
            // Error objects cannot be produced, but a custom .NET-level failure would still
            // resolve its script location from the last syntax element.
            context.LastSyntaxElement = _expression;
            return ((IConstructor) jsValue).Construct(Arguments.Empty, jsValue);
        }

        if (context.IsSuspended())
        {
            return jsValue;
        }

        var arguments = _arguments.ArgumentListEvaluation(context, this, out var rented);

        // Reset the location to the "new" keyword so that if an Error object is
        // constructed below, the stack trace will capture the correct location.
        context.LastSyntaxElement = _expression;

        if (context.IsSuspended())
        {
            // Argument list suspended mid-evaluation. ExpressionCache keeps the buffer alive.
            // Preserve the already-resolved constructor as well, so a getter/proxy callee is not
            // observed a second time when the owner thread resumes.
            if (hostFrame is not null)
            {
                hostData ??= hostFrame.Data.GetOrCreate<HostNewSuspendData>(_hostNewStateKey);
                hostData.Constructor = jsValue;
            }
            return jsValue;
        }

        if (!isCachedConstructor && !jsValue.IsConstructor)
        {
            hostFrame?.Data.Clear(_hostNewStateKey);
            Throw.TypeError(engine.Realm, $"{_calleeExpression.SourceText} is not a constructor");
        }

        // construct the new instance using the Function's constructor method
        var instance = engine.Construct(jsValue, arguments, jsValue, _calleeExpression);

        if (rented)
        {
            engine._jsValueArrayPool.ReturnArray(arguments);
        }

        if (!isCachedConstructor && jsValue is Function function)
        {
            _cachedConstructor = function;
            _cachedLeafZeroArg = _zeroArgs && function.IsZeroArgLeafConstructor && function is IConstructor;
        }

        hostFrame?.Data.Clear(_hostNewStateKey);
        return instance;
    }
}
