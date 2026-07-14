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

        // child-14 (neo-byref-clr2il-delegate): the byref-aware CLR->IL delegate
        // callback entry. Runs the IL target with byref params marshaled as
        // self-referencing scratch cells (so the callee's mutation lands in a
        // cell this method reads back) and writes the mutated values BACK into
        // `args` after the run. The CLR RegisterDelegateByRefConvertor converter
        // calls this with the delegate's byref-typed args and copies the
        // write-back into its `ref`/`out` locals. Returns the boxed result (null
        // for void). Defined on IDelegateAdapter so a cross-assembly converter
        // (ILRuntimeTestBase) can invoke it without internals visibility.
        public unsafe object NeoInvokeByRef(object[] args)
        {
            return NeoInvokeSub(args, true);
        }

        internal unsafe object NeoInvokeSub(object[] args)
        {
            return NeoInvokeSub(args, false);
        }

        // child-14 (neo-byref-clr2il-delegate): the byref-aware entry. When
        // marshalByRef is true, a `ref`/`out` param's value is staged in a
        // SCRATCH cell (reserved past TotalStructSize) and the param's 8-byte
        // Ref Slot is made SELF-REFERENCING -- `(objIdx == -1, off == scratch
        // cell offset)` -- so the IL callee's ldind/stind deref the byref
        // against THIS interpreter's frameBase + scratchOff, reading/writing
        // the staged value. After ExecuteNeo returns, the scratch cell's
        // (possibly mutated) value is read BACK into args[i], giving the CLR
        // converter the write-back channel. Non-byref params use the existing
        // WriteNeoCallSlot path byte-for-byte (marshalByRef == false is the
        // legacy path -- NeoInvoke/NeoInvokePublic never read args[] back).
        internal unsafe object NeoInvokeSub(object[] args, bool marshalByRef)
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

            // child-14: detect byref params and reserve a scratch cell per
            // byref param (past TotalStructSize). The scratch cell holds the
            // CLR value; the param's Ref Slot self-references it so the IL
            // callee's ldind/stind land on the staged value.
            int[] byRefScratchOff = null;
            IType[] byRefElemType = null;
            int scratchBase = nf.TotalStructSize;
            int scratchCur = scratchBase;
            if (marshalByRef && paramCnt > 0)
            {
                byRefScratchOff = new int[paramCnt];
                byRefElemType = new IType[paramCnt];
                var mParams = method.Parameters;
                int firstParam = hasThis ? 1 : 0;
                for (int i = 0; i < paramCnt; i++)
                {
                    byRefScratchOff[i] = -1;
                    var pt = (mParams != null && i < mParams.Count) ? mParams[i] : null;
                    if (pt == null || !pt.IsByRef)
                        continue;
                    int slotIdx = firstParam + i;
                    if (slotIdx >= paramInfos.Length) break;
                    if (paramInfos[slotIdx].Size != 8) continue; // not an 8-byte Ref Slot
                    IType elemType = pt.ElementType;
                    byRefElemType[i] = elemType;
                    int elemSize = NeoByrefElemSize(appdomain, elemType);
                    // 4-align the scratch cell (matches the Ref Slot resolution
                    // granularity; ExecuteNeo reads frame cells as int-aligned).
                    scratchCur = (scratchCur + 3) & ~3;
                    byRefScratchOff[i] = scratchCur;
                    scratchCur += elemSize;
                }
            }

            // Build the Neo frame at StackBase (the fresh interpreter has no
            // in-flight frame). Grow the frame by the scratch region so the
            // self-referencing byrefs stay in-bounds for the whole run.
            byte* frameBase = (byte*)stack.StackBase;
            byte* esp = frameBase;
            int frameSize = scratchCur;
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

            // Write `this` (slot 0) for an instance method. ALSO write the bound
            // `instance` as slot 0 for a static EXTENSION method
            // (neo-delegate-dispatch-arg-marshal): a delegate bound to a static
            // extension method `obj.ExtMethod(...)` must pass the bound `obj` as
            // the target's param 0 (the `this` extension param). Mirrors Legacy
            // ILInvokeSub's `if (method.IsExtend && instance != null) {
            // PushObject(instance); paramCnt--; }` -- the bound instance consumes
            // the first target slot, and the delegate's explicit args map to the
            // remaining slots. Without this, the delegate's first explicit arg is
            // written into the extension-`this` slot (e.g. the `int a` lands where
            // `obj` belongs -> `obj.AddValue` ldfld.i4 sees an Int32 owner ->
            // Step-17/13b NIE).
            bool extendBound = !hasThis && method.IsExtend && instance != null;

            int argIdx = 0;
            if (hasThis || extendBound)
            {
                WriteNeoCallSlot(paramInfos[0], frameBase, mStack, frameRefBase, instance);
                argIdx = 1; // slot 0 consumed
            }
            // Number of explicit delegate args to copy. For an extension method,
            // ParameterCount INCLUDES the bound-this param (verified: pCnt=2 for
            // `IntTest(this T obj, int a)`), so subtract one (Legacy paramCnt-- ).
            int argCount = extendBound ? paramCnt - 1 : paramCnt;
            // Write each CLR param into its param-region slot. `args` carries
            // only the explicit params (not `this`/bound-instance).
            for (int i = 0; i < argCount; i++)
            {
                object arg = (args != null && i < args.Length) ? args[i] : null;
                // byRefScratchOff/byRefElemType are indexed by TARGET param slot
                // (filled from method.Parameters), so offset by the consumed
                // instance slot to align with the delegate-arg index `i`.
                int brIdx = i + (extendBound ? 1 : 0);
                if (marshalByRef && byRefScratchOff != null && brIdx < byRefScratchOff.Length && byRefScratchOff[brIdx] >= 0)
                {
                    // child-14: stage the CLR value in the scratch cell and make
                    // the param's 8-byte Ref Slot self-referencing so the IL
                    // callee's ldind/stind read+write the staged value through
                    // this frame. (objIdx == -1, off == scratchOff).
                    WriteNeoByrefScratchValue(appdomain, frameBase + byRefScratchOff[brIdx], byRefElemType[brIdx], arg);
                    int slotIdx = argIdx;
                    *(int*)(frameBase + paramInfos[slotIdx].Offset + 0) = -1;
                    *(int*)(frameBase + paramInfos[slotIdx].Offset + 4) = byRefScratchOff[brIdx];
                }
                else
                {
                    WriteNeoCallSlot(paramInfos[argIdx], frameBase, mStack, frameRefBase, arg);
                }
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

            // child-14: read the (possibly mutated) byref scratch cells BACK
            // into args[] so the CLR converter sees the callee's write-back.
            if (marshalByRef && byRefScratchOff != null && args != null)
            {
                for (int i = 0; i < paramCnt && i < args.Length; i++)
                {
                    if (byRefScratchOff[i] < 0) continue;
                    args[i] = ReadNeoByrefScratchValue(appdomain, frameBase + byRefScratchOff[i], byRefElemType[i]);
                }
            }

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
            // child-14: for a byref delegate, each subsequent target re-reads
            // args[] (now carrying the prior target's write-back) so multicast
            // ref-semantics hold (each target sees the accumulated mutation).
            if (next != null)
            {
                DelegateAdapter n = (DelegateAdapter)next;
                result = n.NeoInvokeSub(args, marshalByRef);
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

        // ---- child-14 (neo-byref-clr2il-delegate): the byref scratch-cell
        //      helpers for the CLR->IL delegate callback. The IL callee's
        //      ldind/stind deref the self-referencing byref against frameBase +
        //      scratchOff; these stage the CLR value before the run and read it
        //      back after. ----

        // The managed byte size of a byref param's element type (the de-byref'd
        // referent). Primitives use GetPrimitiveSize; a CLR/IL value type uses
        // TotalPrimitiveSize; a reference type is 4 (an mStack index).
        internal static unsafe int NeoByrefElemSize(Enviorment.AppDomain appdomain, IType elemType)
        {
            if (elemType == null) return 4;
            if (elemType.IsPrimitive)
            {
                int sz = appdomain.GetPrimitiveSize(elemType);
                return sz > 0 ? sz : 4;
            }
            if (elemType.IsValueType)
            {
                if (elemType is CLR.TypeSystem.ILType ilt)
                {
                    int sz = ilt.TotalPrimitiveSize;
                    return sz > 0 ? sz : 4;
                }
                // CLR value type: use the CLR managed size (TypeForCLR).
                try { return System.Runtime.InteropServices.Marshal.SizeOf(elemType.TypeForCLR); }
                catch { return 4; }
            }
            return 4; // reference type -> an mStack index (4 bytes)
        }

        // Stage a CLR value into a byref scratch cell (the inverse of the read).
        // Primitives write typed bytes; CLR value types write flat managed bytes;
        // reference types write -1 (the callee treats an unseeded ref slot as
        // null); the converter's ref-byref shape is out of scope for the scratch
        // path (a reference-typed referent would need an mStack slot -- recorded
        // as a sequencing note; the common `ref int`/`out int` case is the core).
        internal static unsafe void WriteNeoByrefScratchValue(Enviorment.AppDomain appdomain, byte* dst, IType elemType, object value)
        {
            if (value == null)
            {
                *(int*)dst = -1;
                return;
            }
            if (elemType != null && elemType.IsPrimitive)
            {
                var clr = elemType.TypeForCLR;
                if (clr == typeof(int) || clr.IsEnum) *(int*)dst = Convert.ToInt32(value);
                else if (clr == typeof(uint)) *(uint*)dst = Convert.ToUInt32(value);
                else if (clr == typeof(long)) *(long*)dst = Convert.ToInt64(value);
                else if (clr == typeof(ulong)) *(ulong*)dst = Convert.ToUInt64(value);
                else if (clr == typeof(short)) *(short*)dst = Convert.ToInt16(value);
                else if (clr == typeof(ushort)) *(ushort*)dst = Convert.ToUInt16(value);
                else if (clr == typeof(byte)) *dst = Convert.ToByte(value);
                else if (clr == typeof(sbyte)) *(sbyte*)dst = Convert.ToSByte(value);
                else if (clr == typeof(bool)) *(int*)dst = Convert.ToBoolean(value) ? 1 : 0;
                else if (clr == typeof(char)) *(char*)dst = Convert.ToChar(value);
                else if (clr == typeof(float)) *(float*)dst = Convert.ToSingle(value);
                else if (clr == typeof(double)) *(double*)dst = Convert.ToDouble(value);
                else *(int*)dst = Convert.ToInt32(value);
                return;
            }
            // Value type or reference: write flat bytes via the area4 helper
            // (mirrors WriteNeoCallSlot's default arm for value types).
            int sz = NeoByrefElemSize(appdomain, elemType);
            ILIntepreter.WriteNeoValueType(value, dst, sz);
        }

        // Read the (possibly mutated) scratch cell back to a boxed CLR object
        // (the inverse of WriteNeoByrefScratchValue). Primitives read typed
        // bytes; value types read flat bytes; reference types return the stored
        // mStack index's object (the scratch path stores -1 for null).
        internal static unsafe object ReadNeoByrefScratchValue(Enviorment.AppDomain appdomain, byte* src, IType elemType)
        {
            if (elemType != null && elemType.IsPrimitive)
            {
                var clr = elemType.TypeForCLR;
                if (clr == typeof(int) || clr.IsEnum) return *(int*)src;
                if (clr == typeof(uint)) return *(uint*)src;
                if (clr == typeof(long)) return *(long*)src;
                if (clr == typeof(ulong)) return *(ulong*)src;
                if (clr == typeof(short)) return *(short*)src;
                if (clr == typeof(ushort)) return *(ushort*)src;
                if (clr == typeof(byte)) return *src;
                if (clr == typeof(sbyte)) return *(sbyte*)src;
                if (clr == typeof(bool)) return *src != 0;
                if (clr == typeof(char)) return *(char*)src;
                if (clr == typeof(float)) return *(float*)src;
                if (clr == typeof(double)) return *(double*)src;
                return *(int*)src;
            }
            int sz = NeoByrefElemSize(appdomain, elemType);
            int cur = 0;
            var clrT = elemType != null ? elemType.TypeForCLR : typeof(int);
            return ILIntepreter.ReadNeoValueType(clrT, src, ref cur, sz);
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
            // child-14 (neo-byref-clr2il-delegate): a byref-aware converter
            // (RegisterDelegateByRefConvertor) routes a `ref`/`out`-carrying
            // custom delegate type through NeoInvokeByRef. It is keyed on the
            // delegate TYPE, not on a per-arity adapter match, so it must be
            // consulted BEFORE the NativeDelegateType access below -- the IL
            // method may have bound to a DummyDelegateAdapter (no by-value
            // per-arity match for a `ref int` param), whose NativeDelegateType
            // throws. DelegateManager.ConvertToDelegate handles the Dummy
            // bypass for a byref-registered type.
            if (appdomain.DelegateManager.HasByRefConvertor(type))
            {
                if (converters == null)
                    converters = new Dictionary<System.Type, Delegate>(new ByReferenceKeyComparer<Type>());
                Delegate resBr;
                if (converters.TryGetValue(type, out resBr))
                    return resBr;
                resBr = appdomain.DelegateManager.ConvertToDelegate(type, this);
                converters[type] = resBr;
                return resBr;
            }
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
#if ENABLE_NEO_MODE
        // child-14: byref-aware CLR->IL delegate callback (writes byref param
        // mutations back into args). See DelegateAdapter.NeoInvokeByRef.
        object NeoInvokeByRef(object[] args);
#endif
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
