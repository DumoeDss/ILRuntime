using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.CLR.Utils;
using ILRuntime.Runtime.Intepreter;
using ILRuntime.Runtime.Stack;
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
using AutoList = System.Collections.Generic.List<object>;
#else
using AutoList = ILRuntime.Other.UncheckedList<object>;
#endif
namespace ILRuntime.Runtime.Enviorment
{
    public static class PrimitiveConverter<T>
    {
        public static Func<T, int> ToInteger;
        public static Func<int, T> FromInteger;
        public static Func<T, long> ToLong;
        public static Func<long, T> FromLong;
        public static Func<T, float> ToFloat;
        public static Func<float, T> FromFloat;
        public static Func<T, double> ToDouble;
        public static Func<double, T> FromDouble;

        public static int CheckAndInvokeToInteger(T val)
        {
            if (ToInteger != null)
                return ToInteger(val);
            else
                throw new InvalidCastException(string.Format("Cannot cast {0} to System.Int32", typeof(T).FullName));
        }

        public static T CheckAndInvokeFromInteger(int val)
        {
            if (FromInteger != null)
                return FromInteger(val);
            else
                throw new InvalidCastException(string.Format("Cannot cast System.Int32 to {0}", typeof(T).FullName));
        }

        public static long CheckAndInvokeToLong(T val)
        {
            if (ToLong != null)
                return ToLong(val);
            else
                throw new InvalidCastException(string.Format("Cannot cast {0} to System.Int64", typeof(T).FullName));
        }

        public static T CheckAndInvokeFromLong(long val)
        {
            if (FromLong != null)
                return FromLong(val);
            else
                throw new InvalidCastException(string.Format("Cannot cast System.Int64 to {0}", typeof(T).FullName));
        }

        public static float CheckAndInvokeToFloat(T val)
        {
            if (ToFloat != null)
                return ToFloat(val);
            else
                throw new InvalidCastException(string.Format("Cannot cast {0} to System.Single", typeof(T).FullName));
        }

        public static T CheckAndInvokeFromFloat(float val)
        {
            if (FromFloat != null)
                return FromFloat(val);
            else
                throw new InvalidCastException(string.Format("Cannot cast System.Single to {0}", typeof(T).FullName));
        }

        public static double CheckAndInvokeToDouble(T val)
        {
            if (ToDouble != null)
                return ToDouble(val);
            else
                throw new InvalidCastException(string.Format("Cannot cast {0} to System.Double", typeof(T).FullName));
        }

        public static T CheckAndInvokeFromDouble(double val)
        {
            if (FromDouble != null)
                return FromDouble(val);
            else
                throw new InvalidCastException(string.Format("Cannot cast System.Double to {0}", typeof(T).FullName));
        }
    }

    enum InvocationTypes
    {
        Integer,
        Long,
        Float,
        Double,
        Enum,
        ValueType,
        Object,
    }
    public unsafe struct InvocationContext : IDisposable
    {
        StackObject* ebp;
        StackObject* esp;
        AppDomain domain;
        ILIntepreter intp;
        ILMethod method;
        AutoList mStack;
        bool invocated;
        int paramCnt;
        bool hasReturn;
        bool useRegister;

        static bool defaultConverterIntialized = false;
        internal static void InitializeDefaultConverters()
        {
            if (!defaultConverterIntialized)
            {
                PrimitiveConverter<int>.ToInteger = (a) => a;
                PrimitiveConverter<int>.FromInteger = (a) => a;
                PrimitiveConverter<short>.ToInteger = (a) => a;
                PrimitiveConverter<short>.FromInteger = (a) => (short)a;
                PrimitiveConverter<byte>.ToInteger = (a) => a;
                PrimitiveConverter<byte>.FromInteger = (a) => (byte)a;
                PrimitiveConverter<sbyte>.ToInteger = (a) => a;
                PrimitiveConverter<sbyte>.FromInteger = (a) => (sbyte)a;
                PrimitiveConverter<ushort>.ToInteger = (a) => a;
                PrimitiveConverter<ushort>.FromInteger = (a) => (ushort)a;
                PrimitiveConverter<char>.ToInteger = (a) => a;
                PrimitiveConverter<char>.FromInteger = (a) => (char)a;
                PrimitiveConverter<uint>.ToInteger = (a) => (int)a;
                PrimitiveConverter<uint>.FromInteger = (a) => (uint)a;
                PrimitiveConverter<bool>.ToInteger = (a) => a ? 1 : 0;
                PrimitiveConverter<bool>.FromInteger = (a) => a == 1;
                PrimitiveConverter<long>.ToLong = (a) => a;
                PrimitiveConverter<long>.FromLong = (a) => a;
                PrimitiveConverter<ulong>.ToLong = (a) => (long)a;
                PrimitiveConverter<ulong>.FromLong = (a) => (ulong)a;
                PrimitiveConverter<float>.ToFloat = (a) => a;
                PrimitiveConverter<float>.FromFloat = (a) => a;
                PrimitiveConverter<double>.ToDouble = (a) => a;
                PrimitiveConverter<double>.FromDouble = (a) => a;

                defaultConverterIntialized = true;
            }
        }

        internal static InvocationTypes GetInvocationType<T>()
        {
            var type = typeof(T);
            if (type.IsPrimitive)
            {
                if (type == typeof(int))
                    return InvocationTypes.Integer;
                if (type == typeof(short))
                    return InvocationTypes.Integer;
                if (type == typeof(bool))
                    return InvocationTypes.Integer;
                if (type == typeof(long))
                    return InvocationTypes.Long;
                if (type == typeof(float))
                    return InvocationTypes.Float;
                if (type == typeof(double))
                    return InvocationTypes.Double;
                if (type == typeof(char))
                    return InvocationTypes.Integer;
                if (type == typeof(ushort))
                    return InvocationTypes.Integer;
                if (type == typeof(uint))
                    return InvocationTypes.Integer;
                if (type == typeof(ulong))
                    return InvocationTypes.Long;
                if (type == typeof(byte))
                    return InvocationTypes.Integer;
                if (type == typeof(sbyte))
                    return InvocationTypes.Integer;
                else
                    throw new NotImplementedException(string.Format("Not supported type:{0}", type.FullName));
            }
            else if (type.IsEnum)
            {
                if (PrimitiveConverter<T>.ToInteger != null && PrimitiveConverter<T>.FromInteger != null)
                    return InvocationTypes.Integer;
                if (PrimitiveConverter<T>.ToLong != null && PrimitiveConverter<T>.FromLong != null)
                    return InvocationTypes.Long;
                return InvocationTypes.Enum;
            }
            else if (type.IsValueType)
                return InvocationTypes.ValueType;
            else
                return InvocationTypes.Object;
        }

        internal InvocationContext(ILIntepreter intp, ILMethod method)
        {
            var stack = intp.Stack;
            mStack = stack.ManagedStack;
            esp = stack.StackBase;
            ebp = esp;
            stack.ResetValueTypePointer();

            this.domain = intp.AppDomain;
            this.intp = intp;
            this.method = method;

            invocated = false;
            paramCnt = 0;
            hasReturn = method.ReturnType != domain.VoidType;
            useRegister = method.ShouldUseRegisterVM;
        }

        internal void SetInvoked(StackObject* esp)
        {
            this.esp = esp - 1;
            invocated = true;
        }

        public StackObject* ESP
        {
            get
            {
                return esp;
            }
            set
            {
                esp = value;
            }
        }

        public ILIntepreter Intepreter
        {
            get
            {
                return intp;
            }
        }

        public AutoList ManagedStack
        {
            get
            {
                return mStack;
            }
        }

        public void PushBool(bool val)
        {
            PushInteger(val ? 1 : 0);
        }

        public void PushInteger<T>(T val)
        {
            PushInteger(PrimitiveConverter<T>.CheckAndInvokeToInteger(val));
        }

        public void PushLong<T>(T val)
        {
            PushInteger(PrimitiveConverter<T>.CheckAndInvokeToLong(val));
        }

        public void PushInteger(int val)
        {
            esp->ObjectType = ObjectTypes.Integer;
            esp->Value = val;
            esp->ValueLow = 0;

            if (useRegister)
                mStack.Add(null);
            esp++;
            paramCnt++;
        }

        public void PushInteger(long val)
        {
            esp->ObjectType = ObjectTypes.Long;
            *(long*)&esp->Value = val;

            if (useRegister)
                mStack.Add(null);
            esp++;
            paramCnt++;
        }

        public void PushFloat<T>(T val)
        {
            PushFloat(PrimitiveConverter<T>.CheckAndInvokeToFloat(val));
        }

        public void PushFloat(float val)
        {
            esp->ObjectType = ObjectTypes.Float;
            *(float*)&esp->Value = val;

            if (useRegister)
                mStack.Add(null);
            esp++;
            paramCnt++;
        }

        public void PushDouble<T>(T val)
        {
            PushDouble(PrimitiveConverter<T>.CheckAndInvokeToDouble(val));
        }

        public void PushDouble(double val)
        {
            esp->ObjectType = ObjectTypes.Double;
            *(double*)&esp->Value = val;
            if (useRegister)
                mStack.Add(null);
            esp++;
            paramCnt++;
        }

        internal static StackObject* PushValueTypeSub<T>(ref T obj, StackObject* esp, Runtime.Enviorment.AppDomain domain, ILIntepreter intp, AutoList mStack, bool useRegister)
        {
            Type t = typeof(T);
            bool needPush = false;
            StackObject* res = default(StackObject*);
            ValueTypeBinder binder;
            if (domain.ValueTypeBinders.TryGetValue(t, out binder))
            {
                var binderT = binder as ValueTypeBinder<T>;
                if (binderT != null)
                {
                    binderT.PushValue(ref obj, intp, esp, mStack);
                    if (useRegister)
                        mStack.Add(null);
                    res = esp + 1;
                }
                else
                    needPush = true;
            }
            else
                needPush = true;
            if (needPush)
            {
                res = ILIntepreter.PushObject(esp, mStack, obj, true);
            }
            return res;
        }

        public void PushValueType<T>(ref T obj)
        {
            esp = PushValueTypeSub(ref obj, esp, domain, intp, mStack, useRegister);
            paramCnt++;
        }

        public void PushObject(object obj, bool isBox = true)
        {
            if (obj is CrossBindingAdaptorType)
                obj = ((CrossBindingAdaptorType)obj).ILInstance;
            var res = ILIntepreter.PushObject(esp, mStack, obj, isBox);
            if (esp->ObjectType < ObjectTypes.Object && useRegister)
                mStack.Add(null);
            esp = res;
            paramCnt++;
        }

        public void PushReference(int index)
        {
            var dst = ebp + index;
            esp->ObjectType = ObjectTypes.StackObjectReference;
            *(long*)&esp->Value = (long)dst;
            if (useRegister)
                mStack.Add(null);
            esp++;
        }

        public void PushParameter<T>(T val)
        {
            PushParameter(GetInvocationType<T>(), val);
        }

        internal void PushParameter<T>(InvocationTypes type, T val)
        {
            switch (type)
            {
                case InvocationTypes.Integer:
                    PushInteger(val);
                    break;
                case InvocationTypes.Long:
                    PushLong(val);
                    break;
                case InvocationTypes.Float:
                    PushFloat(val);
                    break;
                case InvocationTypes.Double:
                    PushDouble(val);
                    break;
                case InvocationTypes.Enum:
                    PushObject(val, false);
                    break;
                case InvocationTypes.ValueType:
                    PushValueType(ref val);
                    break;
                default:
                    PushObject(val);
                    break;
            }
        }

        public T ReadResult<T>()
        {
            return ReadResult<T>(GetInvocationType<T>());
        }

        public T ReadResult<T>(int index)
        {
            var type = GetInvocationType<T>();
            switch (type)
            {
                case InvocationTypes.Integer:
                    return PrimitiveConverter<T>.CheckAndInvokeFromInteger(ReadInteger(index));
                case InvocationTypes.Long:
                    return PrimitiveConverter<T>.CheckAndInvokeFromLong(ReadLong(index));
                case InvocationTypes.Float:
                    return PrimitiveConverter<T>.CheckAndInvokeFromFloat(ReadFloat(index));
                case InvocationTypes.Double:
                    return PrimitiveConverter<T>.CheckAndInvokeFromDouble(ReadDouble(index));
                case InvocationTypes.ValueType:
                    return ReadValueType<T>(index);
                default:
                    return ReadObject<T>(index);
            }
        }

        internal T ReadResult<T>(InvocationTypes type)
        {
            switch (type)
            {
                case InvocationTypes.Integer:
                    return ReadInteger<T>();
                case InvocationTypes.Long:
                    return ReadLong<T>();
                case InvocationTypes.Float:
                    return ReadFloat<T>();
                case InvocationTypes.Double:
                    return ReadDouble<T>();
                case InvocationTypes.ValueType:
                    return ReadValueType<T>();
                default:
                    return ReadObject<T>();
            }
        }
        public void Invoke()
        {
            if (invocated)
                throw new NotSupportedException("A invocation context can only be used once");
            invocated = true;
            var cnt = method.HasThis ? method.ParameterCount + 1 : method.ParameterCount;
            if (cnt != paramCnt)
                throw new ArgumentException("Argument count mismatch");
#if ENABLE_NEO_MODE
            if (CanInvokeNeo())
            {
                InvokeNeo();
                return;
            }
            // Else fall through to the Legacy arm: the Neo re-entry path does not
            // yet faithfully marshal ctors (HasThis=false, `this` as param 0),
            // byref (StackObjectReference) or binder value-type args
            // (ValueTypeObjectReference). The IL method body in those cases still
            // runs on Legacy ExecuteR exactly as before this change -- no
            // regression -- and the typed-opcode methods among them remain a
            // follow-up (the byref/value-type Neo marshalling sub-problem).
#endif
            bool unhandledException;
            if (useRegister)
                esp = intp.ExecuteR(method, esp, out unhandledException);
            else
                esp = intp.Execute(method, esp, out unhandledException);
            esp--;
        }

#if ENABLE_NEO_MODE
        // The Neo re-entry path is only correct for the "simple" arg shape:
        // instance/static methods (not ctors) whose pushed args are all
        // primitives or plain references (no byref, no binder value type). The
        // arg slots live in the LAST `paramCnt` StackObjects before `esp`
        // (mirrors ExecuteR's `r = LocalVarPointer - ParameterCount`, `r--` for
        // HasThis) -- byref/value-type conventions interleave extra storage
        // slots before them, which is exactly what this guard rejects.
        unsafe bool CanInvokeNeo()
        {
            if (!useRegister)
                return false; // non-register body: keep the IL Execute arm
            if (method.IsConstructor)
                return false; // ctor frames are built by newobj, not Run
            StackObject* p = esp - paramCnt;
            for (int i = 0; i < paramCnt; i++)
            {
                var ot = (p + i)->ObjectType;
                if (ot == ObjectTypes.StackObjectReference ||
                    ot == ObjectTypes.ValueTypeObjectReference)
                    return false;
            }
            return true;
        }

        unsafe void InvokeNeo()
        {
            // Under Neo the IL method body is JIT'd to Neo typed opcodes
            // (Ldfld_I4 / Addi_R4 / Muli_R4 / Add_I8 / ...). The Legacy ExecuteR
            // does not recognize them and throws "Not supported opcode {Muli_R4/..}"
            // when an IL method is reached via a CLR->IL callback
            // (CrossBindingFunctionInfo / CrossBindingMethodInfo for inheritance
            // overrides) or a reflection invoke (ILRuntimePropertyInfo.GetValue
            // via PropertyInfo.GetValue). Re-enter the IL method through the Neo
            // `Run` machinery -- the proven CLR->IL re-entry point already used by
            // ILRuntimeMethodInfo.Invoke (AppDomain.Invoke -> Run -> ExecuteNeo) --
            // then place the result back onto the StackObject stack so the typed
            // readers (ReadInteger / ReadObject / ...), which dereference `esp`,
            // keep working unchanged.

            // The args occupy the LAST `paramCnt` slots before `esp` (the slot
            // order ExecuteR reads via r = LocalVarPointer - ParameterCount).
            object instance = null;
            int pCnt = method.ParameterCount;
            object[] args = new object[pCnt];
            {
                StackObject* p = esp - paramCnt;
                int slot = 0;
                if (method.HasThis)
                {
                    instance = StackObject.ToObject(p, domain, mStack);
                    slot = 1;
                }
                for (int i = 0; i < pCnt; i++)
                {
                    args[i] = StackObject.ToObject(p + slot + i, domain, mStack);
                }
            }

            // Neo re-entry: Run builds the Neo frame at StackBase (== ebp),
            // marshals instance/args via DelegateAdapter.WriteNeoCallSlot, runs
            // ExecuteNeo, and returns the (boxed) result with type discrimination.
            object result = intp.Run(method, instance, args);

            if (hasReturn)
            {
                // Write the result to the StackObject stack at ebp so every typed
                // reader (which reads `esp`) sees it; mirror the Legacy arm's
                // trailing `esp--` so esp points AT the result slot.
                //
                // isBox MUST be false: the typed readers (ReadFloat/ReadInteger/
                // ReadLong/ReadDouble, dispatched by ReadResult<T> per the return
                // type) read the value INLINE from `esp->Value`
                // (e.g. ReadFloat is `*(float*)&esp->Value`). The Legacy ExecuteR
                // arm leaves a primitive return INLINE (ObjectType=Float, Value=
                // float bits). PushObject(isBox=true) would instead store
                // ObjectType=Object + Value=mStack index for EVERY value (incl.
                // primitives), so ReadFloat would reinterpret the mStack index
                // (e.g. 3) as a float -> a garbage denormal (3E-45). isBox=false
                // routes primitives through UnboxObject, which writes them inline
                // (matching Legacy); references / value types are pushed as Object
                // slots identically to isBox=true (PushObject's !isBox branch only
                // diverges for primitives/enums). See neo-float-arith-residual.
                StackObject* newEsp = ILIntepreter.PushObject(ebp, mStack, result, false);
                esp = newEsp - 1;
            }
            else
                esp = ebp;
        }
#endif

        void CheckReturnValue()
        {
            if (!invocated)
                throw new NotSupportedException("You have to invocate first before you try to read the return value");
            if (!hasReturn)
                throw new NotSupportedException("The target method does not have a return value");
        }
        public int ReadInteger()
        {
            CheckReturnValue();
            return esp->Value;
        }

        public int ReadInteger(int index)
        {
            var esp = ebp + index;
            return esp->Value;
        }
        public T ReadInteger<T>()
        {
            return PrimitiveConverter<T>.CheckAndInvokeFromInteger(ReadInteger());
        }

        public long ReadLong()
        {
            CheckReturnValue();
            return *(long*)&esp->Value;
        }
        public long ReadLong(int index)
        {
            var esp = ebp + index;
            return *(long*)&esp->Value;
        }
        public T ReadLong<T>()
        {
            return PrimitiveConverter<T>.CheckAndInvokeFromLong(ReadLong());
        }

        public float ReadFloat()
        {
            CheckReturnValue();
            return *(float*)&esp->Value;
        }

        public float ReadFloat(int index)
        {
            var esp = ebp + index;
            return *(float*)&esp->Value;
        }

        public T ReadFloat<T>()
        {
            return PrimitiveConverter<T>.CheckAndInvokeFromFloat(ReadFloat());
        }

        public double ReadDouble()
        {
            CheckReturnValue();
            return *(double*)&esp->Value;
        }
        public double ReadDouble(int index)
        {
            var esp = ebp + index;
            return *(double*)&esp->Value;
        }
        public T ReadDouble<T>()
        {
            return PrimitiveConverter<T>.CheckAndInvokeFromDouble(ReadDouble());
        }

        public bool ReadBool()
        {
            CheckReturnValue();
            return esp->Value == 1;
        }
        public bool ReadBool(int index)
        {
            var esp = ebp + index;
            return esp->Value == 1;
        }

        public T ReadValueType<T>(int index)
        {
            var esp = ebp + index;
            return ReadValueTypeSub<T>(esp, domain, intp, mStack);
        }

        internal static T ReadValueTypeSub<T>(StackObject* val, Runtime.Enviorment.AppDomain domain, ILIntepreter intp, AutoList mStack)
        {
            Type t = typeof(T);
            T res = default(T);
            ValueTypeBinder binder;
            if (domain.ValueTypeBinders.TryGetValue(t, out binder))
            {
                var binderT = binder as ValueTypeBinder<T>;
                if (binderT != null)
                {
                    binderT.ParseValue(ref res, intp, val, mStack);
                }
                else
                    res = (T)t.CheckCLRTypes(StackObject.ToObject(val, domain, mStack));
            }
            else
                res = (T)t.CheckCLRTypes(StackObject.ToObject(val, domain, mStack));
            return res;
        }

        public T ReadValueType<T>()
        {
            CheckReturnValue();
            return ReadValueTypeSub<T>(esp, domain, intp, mStack);
        }

        public T ReadObject<T>()
        {
            CheckReturnValue();
            return (T)typeof(T).CheckCLRTypes(StackObject.ToObject(esp, domain, mStack));
        }

        public object ReadObject(Type type)
        {
            CheckReturnValue();
            return type.CheckCLRTypes(StackObject.ToObject(esp, domain, mStack));
        }
        public T ReadObject<T>(int index)
        {
            var esp = ebp + index;
            return (T)typeof(T).CheckCLRTypes(StackObject.ToObject(esp, domain, mStack));
        }

        public void Dispose()
        {
            domain.FreeILIntepreter(intp);

            esp = null;
            intp = null;
            domain = null;
            method = null;
            mStack = null;
        }
    }
}
