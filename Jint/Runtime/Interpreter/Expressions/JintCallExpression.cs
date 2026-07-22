using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime.CallStack;
using Jint.Runtime.Continuations;
using Jint.Runtime.Environments;
using Environment = Jint.Runtime.Environments.Environment;

namespace Jint.Runtime.Interpreter.Expressions;

internal sealed class JintCallExpression : JintExpression
{
    private readonly ExpressionCache _arguments = new();
    private readonly JintExpression _calleeExpression;
    private readonly object _hostCallStateKey = new();

    public JintCallExpression(CallExpression expression) : base(expression)
    {
        _arguments.Initialize(expression.Arguments.AsSpan());
        _calleeExpression = Build(expression.Callee);
    }

    protected override object EvaluateInternal(EvaluationContext context)
    {
        var engine = context.Engine;
        if (!engine._stackGuard.TryEnterOnCurrentStack())
        {
            // StackGuard.RunOnEmptyStack executes on TaskScheduler.Default and synchronously waits.
            // Both behaviors violate the owner-thread and non-blocking invariants of this mode.
            if (engine.ActiveHostContinuationRun is not null)
            {
                Throw.RangeError(
                    engine.Realm,
                    "The implicit host-continuation runtime cannot migrate JavaScript execution to another CLR thread when the native stack is exhausted.");
            }
            return StackGuard.RunOnEmptyStack(EvaluateInternal, context);
        }

        if (_calleeExpression._expression.Type == NodeType.Super)
        {
            return SuperCall(context);
        }

        // The frame's suspendable is fixed for this expression evaluation. Ordinary child calls
        // temporarily push their own frame, then restore this one before returning.
        var suspendable = engine.ExecutionContext.Suspendable;
        var hostFrame = suspendable as HostContinuationFrame;

        HostCallSuspendData? hostCallData = null;
        if (hostFrame is { IsResuming: true }
            && hostFrame.Data.TryGet(_hostCallStateKey, out hostCallData))
        {
            if (hostCallData!.Stage is HostCallSuspendStage.Operation or HostCallSuspendStage.Child)
            {
                return ResumeHostCall(context, hostFrame, hostCallData);
            }
        }

        object reference;
        Reference? referenceRecord;
        JsValue func;
        JsValue thisObject;

        if (hostCallData?.Stage == HostCallSuspendStage.Arguments)
        {
            reference = hostCallData.Reference!;
            referenceRecord = hostCallData.ReferenceRecord;
            func = hostCallData.Function!;
            thisObject = hostCallData.ThisObject;
        }
        else
        {
            // Fast path: obj.method() / this.method() where the receiver is a plain identifier or
            // `this` and the property is a literal name.
            JsValue? fastFunc = null;
            var fastThis = JsValue.Undefined;
            if (_calleeExpression is JintMemberExpression member
                && member.IsFastCallEligible
                && !engine._customResolver)
            {
                fastFunc = member.GetCalleeForCall(context, out fastThis);
                if (suspendable is not null && suspendable.IsSuspended)
                {
                    return fastFunc;
                }
                if (fastFunc is not ICallable)
                {
                    fastFunc = null;
                }
            }

            if (fastFunc is not null)
            {
                func = fastFunc;
                thisObject = fastThis;
                referenceRecord = null;
                reference = fastFunc;
            }
            else
            {
                var calleeReference = _calleeExpression.Evaluate(context);
                if (suspendable is not null && suspendable.IsSuspended)
                {
                    return calleeReference as JsValue ?? JsValue.Undefined;
                }

                if (ReferenceEquals(calleeReference, JsValue.Undefined))
                {
                    return JsValue.Undefined;
                }

                func = engine.GetValue(calleeReference, false);
                if (func.IsNullOrUndefined() && _expression.IsOptional())
                {
                    return JsValue.Undefined;
                }

                referenceRecord = calleeReference as Reference;
                var isDirectEval = ReferenceEquals(func, engine.Realm.Intrinsics.Eval)
                    && referenceRecord is not null
                    && !referenceRecord.IsPropertyReference
                    && CommonProperties.Eval.Equals(referenceRecord.ReferencedName);

                if (isDirectEval)
                {
                    if (hostFrame is not null)
                    {
                        engine._referencePool.Return(referenceRecord);
                        Throw.Error(
                            engine,
                            "Direct eval is not supported inside an implicit host-continuation call chain.");
                    }
                    return HandleEval(context, func, engine, referenceRecord!);
                }

                if (hostFrame is not null && ReferenceEquals(func, engine.Realm.Intrinsics.Eval))
                {
                    engine._referencePool.Return(referenceRecord);
                    Throw.Error(
                        engine,
                        "Indirect eval is not supported inside an implicit host-continuation call chain.");
                }

                if (referenceRecord is not null)
                {
                    if (referenceRecord.IsPropertyReference)
                    {
                        thisObject = referenceRecord.ThisValue;
                    }
                    else
                    {
                        var baseValue = referenceRecord.Base;
                        if (baseValue.IsNullOrUndefined()
                            && engine._referenceResolver.TryUnresolvableReference(engine, referenceRecord, out var value))
                        {
                            thisObject = value;
                        }
                        else
                        {
                            var refEnv = (Environment) baseValue;
                            thisObject = refEnv.WithBaseObject();
                        }
                    }
                }
                else
                {
                    thisObject = JsValue.Undefined;
                }

                reference = calleeReference;
            }
        }

        var arguments = _arguments.ArgumentListEvaluation(context, this, out var rented);
        if (suspendable is not null && suspendable.IsSuspended)
        {
            if (hostFrame is not null)
            {
                hostCallData ??= hostFrame.Data.GetOrCreate<HostCallSuspendData>(_hostCallStateKey);
                if (hostCallData.Stage == HostCallSuspendStage.None)
                {
                    hostCallData.Stage = HostCallSuspendStage.Arguments;
                    hostCallData.Reference = reference;
                    hostCallData.ReferenceRecord = referenceRecord;
                    hostCallData.Function = func;
                    hostCallData.ThisObject = thisObject;
                }
            }

            // ExpressionCache owns a suspended argument buffer and deliberately reports rented=false.
            if (rented)
            {
                engine._jsValueArrayPool.ReturnArray(arguments);
            }
            return func;
        }

        if (!func.IsObject() && !engine._referenceResolver.TryGetCallable(engine, reference, out func))
        {
            ReleaseCallEvaluationResources(engine, referenceRecord, arguments, rented);
            ClearHostArgumentCallData(hostFrame, hostCallData);
            ThrowMemberIsNotFunction(referenceRecord, reference, engine);
        }

        var callable = func as ICallable;
        if (callable is null)
        {
            ReleaseCallEvaluationResources(engine, referenceRecord, arguments, rented);
            ClearHostArgumentCallData(hostFrame, hostCallData);
            ThrowReferenceNotFunction(referenceRecord, reference, engine);
        }

        HostOperation? pendingOperation = null;
        HostContinuationFrame? pendingChild = null;
        JsValue result;
        try
        {
            if (callable is Function functionInstance)
            {
                var callStack = engine.CallStack;
                var recursionDepth = callStack.Push(functionInstance, _calleeExpression, engine.ExecutionContext);
                if (recursionDepth > engine.Options.Constraints.MaxRecursionDepth)
                {
                    Throw.RecursionDepthOverflowException(callStack);
                }

                try
                {
                    result = hostFrame is not null
                        && functionInstance is ScriptFunction { CanUseHostContinuation: true } scriptFunction
                            ? scriptFunction.CallWithHostContinuation(thisObject, arguments, hostFrame)
                            : functionInstance.Call(thisObject, arguments);
                }
                finally
                {
                    if (callStack.Count > 0)
                    {
                        callStack.Pop();
                    }
                }
            }
            else
            {
                result = callable.Call(thisObject, arguments);
            }

            if (hostFrame is not null)
            {
                if (hostFrame.TryTakePendingOperation(out var operation))
                {
                    pendingOperation = operation;
                }
                else if (hostFrame.TryTakePendingChild(out var child))
                {
                    pendingChild = child;
                }
                else if (hostFrame.IsSuspended)
                {
                    Throw.InvalidOperationException(
                        "A host continuation frame suspended without an operation or child logical frame.");
                }
            }
        }
        catch
        {
            ClearHostArgumentCallData(hostFrame, hostCallData);
            throw;
        }
        finally
        {
            if (rented)
            {
                engine._jsValueArrayPool.ReturnArray(arguments);
            }
            engine._referencePool.Return(referenceRecord);
        }

        if (hostFrame is not null && pendingOperation is not null)
        {
            hostCallData ??= hostFrame.Data.GetOrCreate<HostCallSuspendData>(_hostCallStateKey);
            hostCallData.ClearCallTarget();
            hostCallData.Stage = HostCallSuspendStage.Operation;
            hostCallData.Operation = pendingOperation;
            hostFrame.TrackSuspendedOperation(pendingOperation);
            hostFrame.Suspend(this);
            return result;
        }

        if (hostFrame is not null && pendingChild is not null)
        {
            hostCallData ??= hostFrame.Data.GetOrCreate<HostCallSuspendData>(_hostCallStateKey);
            hostCallData.ClearCallTarget();
            hostCallData.Stage = HostCallSuspendStage.Child;
            hostCallData.ChildFrame = pendingChild;
            hostFrame.TrackSuspendedChild(pendingChild);
            hostFrame.Suspend(this);
            return result;
        }

        ClearHostArgumentCallData(hostFrame, hostCallData);
        return result;
    }

    private JsValue ResumeHostCall(
        EvaluationContext context,
        HostContinuationFrame hostFrame,
        HostCallSuspendData data)
    {
        var engine = context.Engine;
        if (data.Stage == HostCallSuspendStage.Operation)
        {
            var operation = data.Operation
                ?? throw new InvalidOperationException("The suspended host call has no operation.");

            hostFrame.Data.Clear(_hostCallStateKey);
            hostFrame.ReleaseSuspendedOperation(operation);
            hostFrame.ConsumeResumePoint(this);
            return operation.ConsumeAndConvert(engine);
        }

        if (data.Stage != HostCallSuspendStage.Child || data.ChildFrame is null)
        {
            throw new InvalidOperationException("The suspended host call has an invalid resume state.");
        }

        var childFrame = data.ChildFrame;
        var function = childFrame.Function
            ?? throw new InvalidOperationException("The suspended child frame has no script function.");
        var callStack = engine.CallStack;
        var recursionDepth = callStack.Push(function, _calleeExpression, engine.ExecutionContext);
        if (recursionDepth > engine.Options.Constraints.MaxRecursionDepth)
        {
            Throw.RecursionDepthOverflowException(callStack);
        }

        JsValue result;
        try
        {
            result = function.ResumeHostContinuation(childFrame);
        }
        catch
        {
            hostFrame.Data.Clear(_hostCallStateKey);
            hostFrame.ReleaseSuspendedChild(childFrame);
            hostFrame.ConsumeResumePoint(this);
            throw;
        }
        finally
        {
            if (callStack.Count > 0)
            {
                callStack.Pop();
            }
        }

        if (childFrame.IsSuspended)
        {
            // The child reached another host effect. Keep the same call-site state and unwind the
            // parent again; the next owner-thread turn will re-enter this branch recursively.
            hostFrame.Suspend(this);
            return JsValue.Undefined;
        }

        hostFrame.Data.Clear(_hostCallStateKey);
        hostFrame.ReleaseSuspendedChild(childFrame);
        hostFrame.ConsumeResumePoint(this);
        return result;
    }

    private static void ReleaseCallEvaluationResources(
        Engine engine,
        Reference? referenceRecord,
        JsValue[] arguments,
        bool rented)
    {
        if (rented)
        {
            engine._jsValueArrayPool.ReturnArray(arguments);
        }
        engine._referencePool.Return(referenceRecord);
    }

    private void ClearHostArgumentCallData(
        HostContinuationFrame? hostFrame,
        HostCallSuspendData? data)
    {
        if (hostFrame is not null && data?.Stage == HostCallSuspendStage.Arguments)
        {
            hostFrame.Data.Clear(_hostCallStateKey);
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowReferenceNotFunction(Reference? referenceRecord1, object reference, Engine engine)
    {
        var message = $"{referenceRecord1?.ReferencedName ?? reference} is not a function";
        Throw.TypeError(engine.Realm, message);
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMemberIsNotFunction(Reference? referenceRecord1, object reference, Engine engine)
    {
        var message = referenceRecord1 == null
            ? reference + " is not a function"
            : $"Property '{referenceRecord1.ReferencedName}' of object is not a function";
        Throw.TypeError(engine.Realm, message);
    }

    private JsValue HandleEval(EvaluationContext context, JsValue func, Engine engine, Reference referenceRecord)
    {
        var argList = _arguments.ArgumentListEvaluation(context, this, out var rented);

        if (argList.Length == 0)
        {
            return JsValue.Undefined;
        }

        var evalFunctionInstance = (EvalFunction) func;
        var evalArg = argList[0];
        var strictCaller = StrictModeScope.IsStrictModeCode;
        var evalRealm = evalFunctionInstance._realm;
        var direct = !_expression.IsOptional();
        var value = evalFunctionInstance.PerformEval(evalArg, evalRealm, strictCaller, direct);

        if (rented)
        {
            engine._jsValueArrayPool.ReturnArray(argList);
        }
        engine._referencePool.Return(referenceRecord);

        return value;
    }

    private ObjectInstance SuperCall(EvaluationContext context)
    {
        var engine = context.Engine;
        var thisEnvironment = (FunctionEnvironment) engine.ExecutionContext.GetThisEnvironment();
        var newTarget = engine.GetNewTarget(thisEnvironment);
        var func = GetSuperConstructor(thisEnvironment);

        var rented = false;
        var defaultSuperCall = ReferenceEquals(_expression, ClassDefinition._defaultSuperCall);

        var argList = defaultSuperCall
            ? _arguments.DefaultSuperCallArgumentListEvaluation(context)
            : _arguments.ArgumentListEvaluation(context, this, out rented);

        if (func is null || !func.IsConstructor)
        {
            if (rented)
            {
                engine._jsValueArrayPool.ReturnArray(argList);
            }
            Throw.TypeError(engine.Realm, "Not a constructor");
        }

        var result = ((IConstructor) func).Construct(argList, newTarget);

        var thisER = (FunctionEnvironment) engine.ExecutionContext.GetThisEnvironment();
        thisER.BindThisValue(result);
        var F = thisER._functionObject;

        result.InitializeInstanceElements((ScriptFunction) F);

        if (rented)
        {
            engine._jsValueArrayPool.ReturnArray(argList);
        }

        return result;
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-getsuperconstructor
    /// </summary>
    private static ObjectInstance? GetSuperConstructor(FunctionEnvironment thisEnvironment)
    {
        var envRec = thisEnvironment;
        var activeFunction = envRec._functionObject;
        var superConstructor = activeFunction.GetPrototypeOf();
        return superConstructor;
    }

    /// <summary>
    /// https://tc39.es/ecma262/#sec-isintailposition
    /// </summary>
    private static bool IsInTailPosition(CallExpression call)
    {
        // TODO tail calls
        return false;
    }
}
