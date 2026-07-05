using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.CLR.Method;
using ILRuntime.Runtime;
using ILRuntime.Runtime.Stack;
using ILRuntime.Other;
using ILRuntime.Runtime.Enviorment;
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
using AutoList = System.Collections.Generic.List<object>;
#else
using AutoList = ILRuntime.Other.UncheckedList<object>;
#endif
namespace ILRuntime.Runtime.Intepreter
{
    #region Functions
    class FunctionDelegateAdapter<TResult> : DelegateAdapter
    {
        Func<TResult> action;

        static InvocationTypes[] pTypes;
        static FunctionDelegateAdapter()
        {
            pTypes = new InvocationTypes[]
            {
                InvocationContext.GetInvocationType<TResult>(),
            };
        }
        public FunctionDelegateAdapter()
        {

        }

        private FunctionDelegateAdapter(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
            : base(appdomain, instance, method)
        {
            action = InvokeILMethod;
        }

        public override Type NativeDelegateType
        {
            get
            {
                return typeof(Func<TResult>);
            }
        }
        public override Delegate Delegate
        {
            get
            {
                return action;
            }
        }

        unsafe TResult InvokeILMethod()
        {
#if ENABLE_NEO_MODE
            return (TResult)NeoInvoke(null);
#else
            using (var ctx = BeginInvoke())
            {
                var esp = ILInvoke(ctx.Intepreter, ctx.ESP, ctx.ManagedStack);
                ctx.SetInvoked(esp); 
                return ctx.ReadResult<TResult>(pTypes[0]);
            }
#endif
        }

        public override IDelegateAdapter Instantiate(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
        {
            return new FunctionDelegateAdapter<TResult>(appdomain, instance, method);
        }

        public override IDelegateAdapter Clone()
        {
            var res = new FunctionDelegateAdapter<TResult>(appdomain, instance, method);
            res.isClone = true;
            return res;
        }

        public override void Combine(Delegate dele)
        {
            action += (Func<TResult>)dele;
        }

        public override void Remove(Delegate dele)
        {
            action -= (Func<TResult>)dele;
        }
    }

    class FunctionDelegateAdapter<T1, TResult> : DelegateAdapter
    {
        Func<T1, TResult> action;

        static InvocationTypes[] pTypes;
        static FunctionDelegateAdapter()
        {
            pTypes = new InvocationTypes[]
            {
                InvocationContext.GetInvocationType<T1>(),
                InvocationContext.GetInvocationType<TResult>(),
            };
        }
        public FunctionDelegateAdapter()
        {

        }

        private FunctionDelegateAdapter(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
            : base(appdomain, instance, method)
        {
            action = InvokeILMethod;
        }
        public override Type NativeDelegateType
        {
            get
            {
                return typeof(Func<T1, TResult>);
            }
        }
        public override Delegate Delegate
        {
            get
            {
                return action;
            }
        }

        unsafe TResult InvokeILMethod(T1 p1)
        {
#if ENABLE_NEO_MODE
            return (TResult)NeoInvoke(new object[] { p1 });
#else
            using (var ctx = BeginInvoke())
            {
                ctx.PushParameter(pTypes[0], p1);

                var esp = ILInvoke(ctx.Intepreter, ctx.ESP, ctx.ManagedStack);
                ctx.SetInvoked(esp);
                return ctx.ReadResult<TResult>(pTypes[1]);
            }
#endif
        }

        public override IDelegateAdapter Instantiate(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
        {
            return new FunctionDelegateAdapter<T1, TResult>(appdomain, instance, method);
        }

        public override IDelegateAdapter Clone()
        {
            var res = new FunctionDelegateAdapter<T1, TResult>(appdomain, instance, method);
            res.isClone = true;
            return res;
        }

        public override void Combine(Delegate dele)
        {
            action += (Func<T1, TResult>)dele;
        }

        public override void Remove(Delegate dele)
        {
            action -= (Func<T1, TResult>)dele;
        }
    }

    class FunctionDelegateAdapter<T1, T2, TResult> : DelegateAdapter
    {
        Func<T1, T2, TResult> action;

        static InvocationTypes[] pTypes;
        static FunctionDelegateAdapter()
        {
            pTypes = new InvocationTypes[]
            {
                InvocationContext.GetInvocationType<T1>(),
                InvocationContext.GetInvocationType<T2>(),
                InvocationContext.GetInvocationType<TResult>(),
            };
        }
        public FunctionDelegateAdapter()
        {

        }

        private FunctionDelegateAdapter(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
            : base(appdomain, instance, method)
        {
            action = InvokeILMethod;
        }
        public override Type NativeDelegateType
        {
            get
            {
                return typeof(Func<T1, T2, TResult>);
            }
        }
        public override Delegate Delegate
        {
            get
            {
                return action;
            }
        }

        unsafe TResult InvokeILMethod(T1 p1, T2 p2)
        {
#if ENABLE_NEO_MODE
            return (TResult)NeoInvoke(new object[] { p1, p2 });
#else
            using (var ctx = BeginInvoke())
            {
                ctx.PushParameter(pTypes[0], p1);
                ctx.PushParameter(pTypes[1], p2);

                var esp = ILInvoke(ctx.Intepreter, ctx.ESP, ctx.ManagedStack);
                ctx.SetInvoked(esp);
                return ctx.ReadResult<TResult>(pTypes[2]);
            }
#endif
        }

        public override IDelegateAdapter Instantiate(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
        {
            return new FunctionDelegateAdapter<T1, T2, TResult>(appdomain, instance, method);
        }

        public override IDelegateAdapter Clone()
        {
            var res = new FunctionDelegateAdapter<T1, T2, TResult>(appdomain, instance, method);
            res.isClone = true;
            return res;
        }

        public override void Combine(Delegate dele)
        {
            action += (Func<T1, T2, TResult>)dele;
        }

        public override void Remove(Delegate dele)
        {
            action -= (Func<T1, T2, TResult>)dele;
        }
    }

    class FunctionDelegateAdapter<T1, T2, T3, TResult> : DelegateAdapter
    {
        Func<T1, T2, T3, TResult> action;

        static InvocationTypes[] pTypes;

        static FunctionDelegateAdapter()
        {
            pTypes = new InvocationTypes[]
            {
                InvocationContext.GetInvocationType<T1>(),
                InvocationContext.GetInvocationType<T2>(),
                InvocationContext.GetInvocationType<T3>(),
                InvocationContext.GetInvocationType<TResult>(),
            };
        }
        public FunctionDelegateAdapter()
        {

        }

        private FunctionDelegateAdapter(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
            : base(appdomain, instance, method)
        {
            action = InvokeILMethod;
        }
        public override Type NativeDelegateType
        {
            get
            {
                return typeof(Func<T1, T2, T3, TResult>);
            }
        }
        public override Delegate Delegate
        {
            get
            {
                return action;
            }
        }

        unsafe TResult InvokeILMethod(T1 p1, T2 p2, T3 p3)
        {
#if ENABLE_NEO_MODE
            return (TResult)NeoInvoke(new object[] { p1, p2, p3 });
#else
            using (var ctx = BeginInvoke())
            {
                ctx.PushParameter(pTypes[0], p1);
                ctx.PushParameter(pTypes[1], p2);
                ctx.PushParameter(pTypes[2], p3);

                var esp = ILInvoke(ctx.Intepreter, ctx.ESP, ctx.ManagedStack);
                ctx.SetInvoked(esp);
                return ctx.ReadResult<TResult>(pTypes[3]);
            }
#endif
        }

        public override IDelegateAdapter Instantiate(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
        {
            return new FunctionDelegateAdapter<T1, T2, T3, TResult>(appdomain, instance, method);
        }

        public override IDelegateAdapter Clone()
        {
            var res = new FunctionDelegateAdapter<T1, T2, T3, TResult>(appdomain, instance, method);
            res.isClone = true;
            return res;
        }
        public override void Combine(Delegate dele)
        {
            action += (Func<T1, T2, T3, TResult>)dele;
        }

        public override void Remove(Delegate dele)
        {
            action -= (Func<T1, T2, T3, TResult>)dele;
        }
    }

    class FunctionDelegateAdapter<T1, T2, T3, T4, TResult> : DelegateAdapter
    {
        Func<T1, T2, T3, T4, TResult> action;

        static InvocationTypes[] pTypes;

        static FunctionDelegateAdapter()
        {
            pTypes = new InvocationTypes[]
            {
                InvocationContext.GetInvocationType<T1>(),
                InvocationContext.GetInvocationType<T2>(),
                InvocationContext.GetInvocationType<T3>(),
                InvocationContext.GetInvocationType<T4>(),
                InvocationContext.GetInvocationType<TResult>(),
            };
        }
        public FunctionDelegateAdapter()
        {

        }

        private FunctionDelegateAdapter(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
            : base(appdomain, instance, method)
        {
            action = InvokeILMethod;
        }
        public override Type NativeDelegateType
        {
            get
            {
                return typeof(Func<T1, T2, T3, T4, TResult>);
            }
        }
        public override Delegate Delegate
        {
            get
            {
                return action;
            }
        }

        unsafe TResult InvokeILMethod(T1 p1, T2 p2, T3 p3, T4 p4)
        {
#if ENABLE_NEO_MODE
            return (TResult)NeoInvoke(new object[] { p1, p2, p3, p4 });
#else
            using (var ctx = BeginInvoke())
            {
                ctx.PushParameter(pTypes[0], p1);
                ctx.PushParameter(pTypes[1], p2);
                ctx.PushParameter(pTypes[2], p3);
                ctx.PushParameter(pTypes[3], p4);

                var esp = ILInvoke(ctx.Intepreter, ctx.ESP, ctx.ManagedStack);
                ctx.SetInvoked(esp);
                return ctx.ReadResult<TResult>(pTypes[4]);
            }
#endif
        }

        public override IDelegateAdapter Instantiate(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
        {
            return new FunctionDelegateAdapter<T1, T2, T3, T4, TResult>(appdomain, instance, method);
        }

        public override IDelegateAdapter Clone()
        {
            var res = new FunctionDelegateAdapter<T1, T2, T3, T4, TResult>(appdomain, instance, method);
            res.isClone = true;
            return res;
        }

        public override void Combine(Delegate dele)
        {
            action += (Func<T1, T2, T3, T4, TResult>)dele;
        }

        public override void Remove(Delegate dele)
        {
            action -= (Func<T1, T2, T3, T4, TResult>)dele;
        }
    }
    #endregion

    #region Methods
    class MethodDelegateAdapter<T1> : DelegateAdapter
    {
        Action<T1> action;
        static InvocationTypes pType;

        static MethodDelegateAdapter()
        {
            pType = InvocationContext.GetInvocationType<T1>();
        }

        public MethodDelegateAdapter()
        {
            
        }

        private MethodDelegateAdapter(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
            : base(appdomain, instance, method)
        {
            action = InvokeILMethod;
        }

        public override Type NativeDelegateType
        {
            get
            {
                return typeof(Action<T1>);
            }
        }
        public override Delegate Delegate
        {
            get
            {
                return action;
            }
        }

        unsafe void InvokeILMethod(T1 p1)
        {
#if ENABLE_NEO_MODE
            NeoInvoke(new object[] { p1 });
#else
            using (var ctx = BeginInvoke())
            {
                ctx.PushParameter(pType, p1);
                ILInvoke(ctx.Intepreter, ctx.ESP, ctx.ManagedStack);
            }
#endif
        }

        public override IDelegateAdapter Instantiate(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
        {
            return new MethodDelegateAdapter<T1>(appdomain, instance, method);
        }

        public override IDelegateAdapter Clone()
        {
            var res = new MethodDelegateAdapter<T1>(appdomain, instance, method);
            res.isClone = true;
            return res;
        }

        public override void Combine(Delegate dele)
        {
            action += (Action<T1>)dele;
        }

        public override void Remove(Delegate dele)
        {
            action -= (Action<T1>)dele;
        }
    }

    class MethodDelegateAdapter<T1, T2> : DelegateAdapter
    {
        Action<T1, T2> action;

        static InvocationTypes[] pTypes;

        static MethodDelegateAdapter()
        {
            pTypes = new InvocationTypes[]
            {
                InvocationContext.GetInvocationType<T1>(),
                InvocationContext.GetInvocationType<T2>(),
            };
        }
        public MethodDelegateAdapter()
        {

        }

        private MethodDelegateAdapter(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
            : base(appdomain, instance, method)
        {
            action = InvokeILMethod;
        }
        public override Type NativeDelegateType
        {
            get
            {
                return typeof(Action<T1, T2>);
            }
        }
        public override Delegate Delegate
        {
            get
            {
                return action;
            }
        }

        unsafe void InvokeILMethod(T1 p1, T2 p2)
        {
#if ENABLE_NEO_MODE
            NeoInvoke(new object[] { p1, p2 });
#else
            using (var ctx = BeginInvoke())
            {
                ctx.PushParameter(pTypes[0], p1);
                ctx.PushParameter(pTypes[1], p2);
                ILInvoke(ctx.Intepreter, ctx.ESP, ctx.ManagedStack);
            }
#endif
        }

        public override IDelegateAdapter Instantiate(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
        {
            return new MethodDelegateAdapter<T1, T2>(appdomain, instance, method);
        }

        public override IDelegateAdapter Clone()
        {
            var res = new MethodDelegateAdapter<T1, T2>(appdomain, instance, method);
            res.isClone = true;
            return res;
        }

        public override void Combine(Delegate dele)
        {
            action += (Action<T1, T2>)dele;
        }

        public override void Remove(Delegate dele)
        {
            action -= (Action<T1, T2>)dele;
        }
    }

    class MethodDelegateAdapter<T1, T2, T3> : DelegateAdapter
    {
        Action<T1, T2, T3> action;

        static InvocationTypes[] pTypes;

        static MethodDelegateAdapter()
        {
            pTypes = new InvocationTypes[]
            {
                InvocationContext.GetInvocationType<T1>(),
                InvocationContext.GetInvocationType<T2>(),
                InvocationContext.GetInvocationType<T3>(),
            };
        }
        public MethodDelegateAdapter()
        {

        }

        private MethodDelegateAdapter(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
            : base(appdomain, instance, method)
        {
            action = InvokeILMethod;
        }
        public override Type NativeDelegateType
        {
            get
            {
                return typeof(Action<T1, T2, T3>);
            }
        }
        public override Delegate Delegate
        {
            get
            {
                return action;
            }
        }

        unsafe void InvokeILMethod(T1 p1, T2 p2, T3 p3)
        {
#if ENABLE_NEO_MODE
            NeoInvoke(new object[] { p1, p2, p3 });
#else
            using (var ctx = BeginInvoke())
            {
                ctx.PushParameter(pTypes[0], p1);
                ctx.PushParameter(pTypes[1], p2);
                ctx.PushParameter(pTypes[2], p3);
                ILInvoke(ctx.Intepreter, ctx.ESP, ctx.ManagedStack);
            }
#endif
        }

        public override IDelegateAdapter Instantiate(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
        {
            return new MethodDelegateAdapter<T1, T2, T3>(appdomain, instance, method);
        }

        public override IDelegateAdapter Clone()
        {
            var res = new MethodDelegateAdapter<T1, T2, T3>(appdomain, instance, method);
            res.isClone = true;
            return res;
        }

        public override void Combine(Delegate dele)
        {
            action += (Action<T1, T2, T3>)dele;
        }

        public override void Remove(Delegate dele)
        {
            action -= (Action<T1, T2, T3>)dele;
        }
    }

    class MethodDelegateAdapter<T1, T2, T3, T4> : DelegateAdapter
    {
        Action<T1, T2, T3, T4> action;

        static InvocationTypes[] pTypes;

        static MethodDelegateAdapter()
        {
            pTypes = new InvocationTypes[]
            {
                InvocationContext.GetInvocationType<T1>(),
                InvocationContext.GetInvocationType<T2>(),
                InvocationContext.GetInvocationType<T3>(),
                InvocationContext.GetInvocationType<T4>(),
            };
        }
        public MethodDelegateAdapter()
        {

        }

        private MethodDelegateAdapter(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
            : base(appdomain, instance, method)
        {
            action = InvokeILMethod;
        }
        public override Type NativeDelegateType
        {
            get
            {
                return typeof(Action<T1, T2, T3, T4>);
            }
        }
        public override Delegate Delegate
        {
            get
            {
                return action;
            }
        }

        unsafe void InvokeILMethod(T1 p1, T2 p2, T3 p3, T4 p4)
        {
#if ENABLE_NEO_MODE
            NeoInvoke(new object[] { p1, p2, p3, p4 });
#else
            using (var ctx = BeginInvoke())
            {
                ctx.PushParameter(pTypes[0], p1);
                ctx.PushParameter(pTypes[1], p2);
                ctx.PushParameter(pTypes[2], p3);
                ctx.PushParameter(pTypes[3], p4);
                ILInvoke(ctx.Intepreter, ctx.ESP, ctx.ManagedStack);
            }
#endif
        }

        public override IDelegateAdapter Instantiate(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
        {
            return new MethodDelegateAdapter<T1, T2, T3, T4>(appdomain, instance, method);
        }

        public override IDelegateAdapter Clone()
        {
            var res = new MethodDelegateAdapter<T1, T2, T3, T4>(appdomain, instance, method);
            res.isClone = true;
            return res;
        }

        public override void Combine(Delegate dele)
        {
            action += (Action<T1, T2, T3, T4>)dele;
        }

        public override void Remove(Delegate dele)
        {
            action -= (Action<T1, T2, T3, T4>)dele;
        }
    }

#if NET_4_6 || NET_STANDARD_2_0
    class MethodDelegateAdapter<T1, T2, T3, T4, T5> : DelegateAdapter
    {
        Action<T1, T2, T3, T4, T5> action;

        static InvocationTypes[] pTypes;

        static MethodDelegateAdapter()
        {
            pTypes = new InvocationTypes[]
            {
                InvocationContext.GetInvocationType<T1>(),
                InvocationContext.GetInvocationType<T2>(),
                InvocationContext.GetInvocationType<T3>(),
                InvocationContext.GetInvocationType<T4>(),
                InvocationContext.GetInvocationType<T5>(),
            };
        }
        public MethodDelegateAdapter()
        {

        }

        private MethodDelegateAdapter(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
            : base(appdomain, instance, method)
        {
            action = InvokeILMethod;
        }
        public override Type NativeDelegateType
        {
            get
            {
                return typeof(Action<T1, T2, T3, T4, T5>);
            }
        }
        public override Delegate Delegate
        {
            get
            {
                return action;
            }
        }

        unsafe void InvokeILMethod(T1 p1, T2 p2, T3 p3, T4 p4, T5 p5)
        {
#if ENABLE_NEO_MODE
            NeoInvoke(new object[] { p1, p2, p3, p4, p5 });
#else
            using (var ctx = BeginInvoke())
            {
                ctx.PushParameter(pTypes[0], p1);
                ctx.PushParameter(pTypes[1], p2);
                ctx.PushParameter(pTypes[2], p3);
                ctx.PushParameter(pTypes[3], p4);
                ctx.PushParameter(pTypes[4], p5);
                ILInvoke(ctx.Intepreter, ctx.ESP, ctx.ManagedStack);
            }
#endif
        }

        public override IDelegateAdapter Instantiate(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
        {
            return new MethodDelegateAdapter<T1, T2, T3, T4, T5>(appdomain, instance, method);
        }

        public override IDelegateAdapter Clone()
        {
            var res = new MethodDelegateAdapter<T1, T2, T3, T4, T5>(appdomain, instance, method);
            res.isClone = true;
            return res;
        }

        public override void Combine(Delegate dele)
        {
            action += (Action<T1, T2, T3, T4, T5>)dele;
        }

        public override void Remove(Delegate dele)
        {
            action -= (Action<T1, T2, T3, T4, T5>)dele;
        }
    }
#endif

    class MethodDelegateAdapter : DelegateAdapter
    {
        Action action;
        
        public MethodDelegateAdapter()
        {

        }

        protected MethodDelegateAdapter(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
            : base(appdomain, instance, method)
        {
            action = InvokeILMethod;
        }
        public override Type NativeDelegateType
        {
            get
            {
                return typeof(Action);
            }
        }
        public override Delegate Delegate
        {
            get
            {
                return action;
            }
        }

        unsafe void InvokeILMethod()
        {
#if ENABLE_NEO_MODE
            NeoInvoke(null);
#else
            using(var ctx = BeginInvoke())
            {
                ILInvoke(ctx.Intepreter, ctx.ESP, ctx.ManagedStack);
            }
#endif
        }

        public override IDelegateAdapter Instantiate(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
        {
            return new MethodDelegateAdapter(appdomain, instance, method);
        }

        public override IDelegateAdapter Clone()
        {
            var res = new MethodDelegateAdapter(appdomain, instance, method);
            res.isClone = true;
            return res;
        }

        public override void Combine(Delegate dele)
        {
            action += (Action)dele;
        }

        public override void Remove(Delegate dele)
        {
            action -= (Action)dele;
        }
    }

    class DummyDelegateAdapter : DelegateAdapter
    {
        public DummyDelegateAdapter()
        {

        }

        protected DummyDelegateAdapter(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
            : base(appdomain, instance, method)
        {
            
        }
        public override Type NativeDelegateType
        {
            get
            {
                ThrowAdapterNotFound(method);
                return null;
            }
        }
        public override Delegate Delegate
        {
            get
            {
                ThrowAdapterNotFound(method);
                return null;
            }
        }

        void InvokeILMethod()
        {
            if (method.HasThis)
                appdomain.Invoke(method, instance, null);
            else
                appdomain.Invoke(method, null, null);
        }

        public override IDelegateAdapter Instantiate(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
        {
            return new DummyDelegateAdapter(appdomain, instance, method);
        }

        public override IDelegateAdapter Clone()
        {
            var res = new DummyDelegateAdapter(appdomain, instance, method);
            res.isClone = true;
            return res;
        }

        public override void Combine(Delegate dele)
        {
            ThrowAdapterNotFound(method);
        }

        public override void Remove(Delegate dele)
        {
            ThrowAdapterNotFound(method);
        }
    }
    #endregion

    abstract class DelegateAdapter : ILTypeInstance, IDelegateAdapter
    {
        protected ILMethod method;
        protected ILTypeInstance instance;
        protected Enviorment.AppDomain appdomain;
        Dictionary<Type, Delegate> converters;
        IDelegateAdapter next;
        protected bool isClone;

        public abstract Delegate Delegate { get; }

        public abstract Type NativeDelegateType { get; }

        public IDelegateAdapter Next { get { return next; } }

        public ILTypeInstance Instance { get { return instance; } }

        public ILMethod Method { get { return method; } }

        protected DelegateAdapter() { }

        protected DelegateAdapter(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method)
        {
            this.appdomain = appdomain;
            this.instance = instance;
            this.method = method;
            CLRInstance = this;
        }

        public override bool IsValueType
        {
            get
            {
                return false;
            }
        }

        public unsafe InvocationContext BeginInvoke()
        {
            var ctx = appdomain.BeginInvoke(method);
            *ctx.ESP = default(StackObject);
            ctx.ESP++;//required to simulate delegate invocation
            return ctx;
        }

#if ENABLE_NEO_MODE
        // Step 19: the CLR -> IL callback under the Neo calling convention.
        // Legacy uses BeginInvoke (a fresh interpreter from the pool) + a
        // StackObject push + ExecuteR. Under Neo the StackObject path is the
        // wrong calling convention, so this helper replaces it for the single-
        // invoke path: request a FRESH interpreter (Legacy semantics -- each
        // delegate invocation runs on its own engine stack, so a callback from
        // inside ExecuteNeo does NOT clobber the in-flight frame; Risk 3 as
        // originally framed does not apply), build a Neo `byte*` frame at that
        // interpreter's StackBase, write `this`(slot 0) + each CLR param into
        // the callee param region (the INVERSE of CopyNeoCallArguments), call
        // ExecuteNeo, read the return. The multicast `next`-chain reuses
        // unchanged (D4) -- walk it discarding intermediate returns.
        //
        // `args` are the CLR-side arguments (already converted by the per-arity
        // adapter to their CLR types). Returns null for a void method.
        protected unsafe object NeoInvoke(object[] args)
        {
            return NeoInvokeSub(args);
        }

        // Step 19: public entry for the IL-delegate-Invoke callvirt arm (the
        // Callvirt_IL delegate-invoke branch in ExecuteNeo routes `del(args)`
        // through here). Equivalent to the per-arity InvokeILMethod bodies.
        public unsafe object NeoInvokePublic(object[] args)
        {
            return NeoInvokeSub(args);
        }

        internal unsafe object NeoInvokeSub(object[] args)
        {
            // Request a fresh interpreter (Legacy BeginInvoke semantics).
            // Wrapped in try/finally so the interpreter is returned to the pool
            // on EVERY exit path (mirrors Legacy
            // `using (var ctx = BeginInvoke())` -> InvocationContext.Dispose
            // -> domain.FreeILIntepreter). Without the free, each delegate
            // callback would allocate a NEW ILIntepreter and the pool would
            // starve (unbounded growth on delegate-heavy IL code -- the Step 19
            // hot path). The free runs AFTER ExecuteNeo returns and the result
            // is read; the next-chain recursion does its own balanced
            // request/free pair, so nesting is safe.
            ILIntepreter intp = appdomain.RequestILIntepreter();
            try
            {
            var stack = intp.Stack;
            AutoList mStack = stack.ManagedStack;
            int mStackBase = mStack.Count;
            stack.ResetValueTypePointer();

            ref readonly var nf = ref method.CompiledFrame;
            var paramInfos = nf.ParamInfos;
            int paramCnt = method.ParameterCount;
            bool hasThis = method.HasThis;

            // Build the Neo frame at StackBase (the fresh interpreter has no
            // in-flight frame).
            byte* frameBase = (byte*)stack.StackBase;
            byte* esp = frameBase;
            int frameSize = nf.TotalStructSize;
            byte* newEsp = esp + frameSize;

            // Zero the locals primitive region (mirrors ExecuteNeo's own zeroing).
            if (nf.LocalsPrimitiveSize > 0)
                System.Runtime.CompilerServices.Unsafe.InitBlock(frameBase + nf.ParamPrimitiveSize, 0, (uint)nf.LocalsPrimitiveSize);
            // Zero-init the ref-typed param/local slots that the optimizer marks
            // as ref (so an unassigned ref slot reads as -1 / null).
            var localInfos = nf.LocalInfos;
            var localIsRef = nf.LocalIsReference;
            if (localInfos != null && localIsRef != null)
            {
                for (int i = 0; i < localInfos.Length; i++)
                {
                    if (localIsRef[i])
                        *(int*)(frameBase + localInfos[i].Offset) = -1;
                }
            }

            // Managed-stack reservation for this frame's reference slots.
            int frameRefBase = mStack.Count;
            for (int i = 0; i < nf.TotalRefSize; i++)
                mStack.Add(null);

            // Write `this` (slot 0) for an instance method.
            int argIdx = 0;
            if (hasThis)
            {
                WriteNeoCallSlot(paramInfos[0], frameBase, mStack, frameRefBase, instance);
                argIdx = 1; // slot 0 consumed
            }
            // Write each CLR param into its param-region slot. `args` carries
            // only the explicit params (not `this`).
            for (int i = 0; i < paramCnt; i++)
            {
                object arg = (args != null && i < args.Length) ? args[i] : null;
                WriteNeoCallSlot(paramInfos[argIdx], frameBase, mStack, frameRefBase, arg);
                argIdx++;
            }

            // Return slot.
            int retSize = nf.ReturnPrimitiveSize;
            int retRefCount = nf.ReturnRefCount;
            byte* retDst = newEsp;
            int retRefBase = mStack.Count;
            for (int i = 0; i < retRefCount; i++)
                mStack.Add(null);

            bool unhandled;
            intp.ExecuteNeo(method, frameBase, retDst, retRefBase, out unhandled);

            object result = null;
            if (!method.ReturnType.IsValueType && method.ReturnType != appdomain.VoidType
                && retSize > 0)
            {
                // Reference return: the mStack index is at retDst.
                int retIdx = *(int*)retDst;
                result = (retIdx >= 0) ? mStack[retIdx] : null;
            }
            else if (method.ReturnType != appdomain.VoidType && retSize > 0)
            {
                result = ILIntepreter.NeoBoxReturnValue(method.ReturnType, retDst, retSize);
            }

            // Tear down this invoke's mStack reservation (the frame ref region +
            // the return ref slots). Restore to the base recorded on entry.
            mStack.RemoveRange(mStackBase, mStack.Count - mStackBase);

            if (unhandled)
                throw new Exception("NeoInvoke: unhandled exception in delegate target " + method);

            // Multicast: walk the next-chain, discarding intermediate returns
            // (Legacy ILInvokeSub:965-974 returns the LAST delegate's result).
            if (next != null)
            {
                DelegateAdapter n = (DelegateAdapter)next;
                result = n.NeoInvokeSub(args);
            }
            return result;
            }
            finally
            {
                appdomain.FreeILIntepreter(intp);
            }
        }

        // Write a CLR value into a callee param-region slot per its StackSlotInfo
        // (the inverse of CopyNeoCallArguments). Reference -> mStack index;
        // primitive -> direct typed write; CLR value type -> WriteNeoValueType.
        internal static unsafe void WriteNeoCallSlot(ILRuntime.Runtime.Intepreter.RegisterVM.StackSlotInfo info, byte* frameBase, AutoList mStack, int frameRefBase, object value)
        {
            int off = info.Offset;
            if (info.RefCount > 0 && info.Size == 4)
            {
                // Reference slot (object / string / ILTypeInstance / IMethod):
                // store the object on mStack, write the index.
                int idx = frameRefBase + info.RefOffset;
                mStack[idx] = value;
                *(int*)(frameBase + off) = idx;
                return;
            }
            if (value == null)
                return;
            // Primitive or CLR value type.
            switch (info.Size)
            {
                case 1: *(byte*)(frameBase + off) = (byte)value; break;
                case 2: *(short*)(frameBase + off) = (short)value; break;
                case 4:
                    if (value.GetType().IsEnum)
                        *(int*)(frameBase + off) = Convert.ToInt32(value);
                    else if (value is float f)
                        *(float*)(frameBase + off) = f;
                    else
                        *(int*)(frameBase + off) = Convert.ToInt32(value);
                    break;
                case 8:
                    if (value is double d)
                        *(double*)(frameBase + off) = d;
                    else if (value is long l)
                        *(long*)(frameBase + off) = l;
                    else
                        *(long*)(frameBase + off) = Convert.ToInt64(value);
                    break;
                default:
                    // CLR value type (struct) with managed size > 8: write its
                    // flat managed bytes via the area4 helper.
                    ILIntepreter.WriteNeoValueType(value, frameBase + off, info.Size);
                    break;
            }
        }
#endif

        public unsafe StackObject* ILInvoke(ILIntepreter intp, StackObject* esp, AutoList mStack)
        {
            var ebp = esp;
            esp = ILInvokeSub(intp, esp, mStack);
            return ClearStack(intp, esp, ebp, mStack);
        }

        unsafe StackObject* ILInvokeSub(ILIntepreter intp, StackObject* esp, AutoList mStack)
        {
            var ebp = esp;
            bool unhandled;
            if (method.HasThis)
                esp = ILIntepreter.PushObject(esp, mStack, instance);
            int paramCnt = method.ParameterCount;
            if (method.IsExtend && instance != null)
            {
                esp = ILIntepreter.PushObject(esp, mStack, instance);
                paramCnt--;
            }
            bool useRegister = method.ShouldUseRegisterVM;
            for (int i = paramCnt; i > 0; i--)
            {
                intp.CopyToStack(esp, Minus(ebp, i), mStack);
                if (esp->ObjectType < ObjectTypes.Object && useRegister)
                    mStack.Add(null);
                esp++;
            }
            StackObject* ret;
            if (useRegister)
                ret = intp.ExecuteR(method, esp, out unhandled);
            else
                ret = intp.Execute(method, esp, out unhandled);
            if (next != null)
            {
                if (method.ReturnType != appdomain.VoidType)
                {
                    intp.Free(ret - 1);//Return value for multicast delegate doesn't make sense, only return the last one's value
                }
                DelegateAdapter n = (DelegateAdapter)next;
                ret = n.ILInvokeSub(intp, ebp, mStack);

            }
            return ret;
        }

        unsafe StackObject* ClearStack(ILIntepreter intp, StackObject* esp, StackObject* ebp, AutoList mStack)
        {
            int paramCnt = method.ParameterCount;
            if (method.IsExtend && instance != null)//如果是拓展方法，退一位
            {
                paramCnt--;
            }
            object retObj = null;
            StackObject retSObj = StackObject.Null;
            bool hasReturn = method.ReturnType != appdomain.VoidType;
            if (hasReturn)
            {
                var ret = esp - 1;
                retSObj = *ret;
                if(ret->ObjectType>= ObjectTypes.Object)
                {
                    retObj = mStack[ret->Value];
                    if(retObj == null)
                    {
                        retSObj.ObjectType = ObjectTypes.Null;
                        retSObj.Value = -1;
                        retSObj.ValueLow = 0;
                    }

                    intp.Free(ret);
                }
            }
            for (int i = 1; i <= paramCnt; i++)
            {
                intp.Free(ebp - i);
            }
            var returnVal = Minus(ebp, paramCnt + 1);
            intp.Free(returnVal);//Free delegateInstance
            if (hasReturn)
            {
                *returnVal = retSObj;
                if(retObj != null)
                {
                    returnVal->Value = mStack.Count;
                    mStack.Add(retObj);
                }
                returnVal++;
            }
            return returnVal;
        }

        public abstract IDelegateAdapter Instantiate(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method);

        public new abstract IDelegateAdapter Clone();

        public bool IsClone
        {
            get
            {
                return isClone;
            }
        }

        public virtual void Combine(IDelegateAdapter adapter)
        {
            if (next != null)
                next.Combine(adapter);
            else
                next = adapter;
        }

        public abstract void Combine(Delegate dele);

        public virtual void Remove(IDelegateAdapter adapter)
        {
            if (next != null)
            {
                if (next.Equals(adapter))
                {
                    next = ((DelegateAdapter)next).next;
                }
                else
                    next.Remove(adapter);
            }
        }

        public abstract void Remove(Delegate dele);

        public virtual bool Equals(IDelegateAdapter adapter)
        {
            if (adapter is DelegateAdapter)
            {
                DelegateAdapter b = (DelegateAdapter)adapter;
                return instance == b.instance && method == b.method;
            }
            else
                return false;
        }

        public override bool Equals(object obj)
        {
            if (obj is DelegateAdapter)
            {
                DelegateAdapter b = (DelegateAdapter)obj;
                return instance == b.instance && method == b.method;
            }
            return false;
        }

        public virtual bool Equals(Delegate dele)
        {
            return Delegate == dele;
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }

        public override string ToString()
        {
            return method.ToString();
        }

        public override bool CanAssignTo(IType type)
        {
            if (type.IsDelegate)
            {
                var method_count = method.IsExtend ? method.ParameterCount - 1 : method.ParameterCount;
                var im = type.GetMethod("Invoke", method_count);
                if (im == null)
                {
                    return false;
                }
                var ret_type = im.ReturnType;
                if (im.ReturnType != appdomain.VoidType && type.IsGenericInstance)
                {
                    ret_type = type.GenericArguments[im.ParameterCount].Value;
                }
                if (im.IsDelegateInvoke)
                {
                    if (im.ParameterCount == method_count && method.ReturnType.CanAssignTo(ret_type))
                    {

                        for (int i = 0; i < im.ParameterCount; i++)
                        {
                            var index = method.IsExtend ? i + 1 : i;
                            if (im.Parameters[i] != method.Parameters[index] && (!(im is CLRMethod) || (im.Parameters[i].TypeForCLR != method.Parameters[index].TypeForCLR)))
                                return false;
                        }

                        return true;
                    }
                    else
                        return false;
                }
                else
                    return false;
            }
            else
                return false;
        }

        public Delegate GetConvertor(Type type)
        {
            if (type.IsAssignableFrom(NativeDelegateType))
                return Delegate;
            if (converters == null)
                converters = new Dictionary<System.Type, Delegate>(new ByReferenceKeyComparer<Type>());
            Delegate res;
            if (converters.TryGetValue(type, out res))
                return res;
            else
            {
                res = appdomain.DelegateManager.ConvertToDelegate(type, this);
                converters[type] = res;
                return res;
            }
        }

        unsafe StackObject* Minus(StackObject* a, int b)
        {
            return (StackObject*)((long)a - sizeof(StackObject) * b);
        }

        public static void ThrowAdapterNotFound(IMethod method)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Cannot find Delegate Adapter for:");
            sb.Append(method.ToString());
            string clsName, rName;
            bool isByRef;
            if (method.ReturnType.Name != "Void" || method.ParameterCount > 0)
            {
                sb.AppendLine(", Please add following code:");
                if (method.ReturnType.Name == "Void")
                {
                    sb.Append("appdomain.DelegateManager.RegisterMethodDelegate<");
                    bool first = true;
                    foreach(var i in method.Parameters)
                    {
                        if (first)
                        {
                            first = false;
                        }
                        else
                        {
                            sb.Append(", ");
                        }
                        i.TypeForCLR.GetClassName(out clsName, out rName, out isByRef);
                        sb.Append(rName);                        
                    }
                    sb.AppendLine(">();");
                }
                else
                {
                    sb.Append("appdomain.DelegateManager.RegisterFunctionDelegate<");
                    bool first = true;
                    foreach (var i in method.Parameters)
                    {
                        if (first)
                        {
                            first = false;
                        }
                        else
                        {
                            sb.Append(", ");
                        }
                        i.TypeForCLR.GetClassName(out clsName, out rName, out isByRef);
                        sb.Append(rName);
                    }
                    if (!first)
                        sb.Append(", ");
                    method.ReturnType.TypeForCLR.GetClassName(out clsName, out rName, out isByRef);
                    sb.Append(rName);
                    sb.AppendLine(">();");
                }
            }
            throw new KeyNotFoundException(sb.ToString());
        }
    }

    public unsafe interface IDelegateAdapter
    {        
        Type NativeDelegateType { get; }
        Delegate Delegate { get; }
        IDelegateAdapter Next { get; }
        ILTypeInstance Instance { get; }
        ILMethod Method { get; }

        InvocationContext BeginInvoke();
        StackObject* ILInvoke(ILIntepreter intp, StackObject* esp, AutoList mStack);
        IDelegateAdapter Instantiate(Enviorment.AppDomain appdomain, ILTypeInstance instance, ILMethod method);
        bool IsClone { get; }
        IDelegateAdapter Clone();
        Delegate GetConvertor(Type type);
        void Combine(IDelegateAdapter adapter);
        void Combine(Delegate dele);
        void Remove(IDelegateAdapter adapter);
        void Remove(Delegate dele);
        bool Equals(IDelegateAdapter adapter);
        bool Equals(Delegate dele);
    }
}
