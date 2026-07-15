using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;

using ILRuntime.CLR.TypeSystem;
using ILRuntime.CLR.Method;
using ILRuntime.CLR.Utils;
using ILRuntime.Runtime.Intepreter;
using ILRuntime.Runtime.Stack;
using ILRuntime.Reflection;
using System.Collections;
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
using AutoList = System.Collections.Generic.List<object>;
#else
using AutoList = ILRuntime.Other.UncheckedList<object>;
#endif
namespace ILRuntime.Runtime.Enviorment
{
    unsafe static class CLRRedirections
    {
        public static StackObject* GetCurrentStackTrace(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            StackObject* ret = esp - 2;
            intp.Free(esp - 1);

            return ILIntepreter.PushObject(ret, mStack, intp.AppDomain.DebugService.GetStackTrace(intp));
        }
        public static StackObject* CreateInstance(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            IType[] genericArguments = method.GenericArguments;
            if (genericArguments != null && genericArguments.Length == 1)
            {
                var t = genericArguments[0];
                if (t is ILType)
                {
                    if (t.IsValueType && !t.IsEnum)
                    {
                        intp.AllocValueType(esp++, t);
                        return esp;
                    }
                    else
                        return ILIntepreter.PushObject(esp, mStack, ((ILType)t).Instantiate());
                }
                else
                {
                    if (intp.AppDomain.ValueTypeBinders.ContainsKey(t.TypeForCLR))
                    {
                        intp.AllocValueType(esp++, t);
                        return esp;
                    }
                    else
                        return ILIntepreter.PushObject(esp, mStack, ((CLRType)t).CreateDefaultInstance());
                }
            }
            else
                throw new EntryPointNotFoundException();
        }
        /*public static object CreateInstance(ILContext ctx, object instance, object[] param, IType[] genericArguments)
        {
            if (genericArguments != null && genericArguments.Length == 1)
            {
                var t = genericArguments[0];
                if (t is ILType)
                {
                    return ((ILType)t).Instantiate();
                }
                else
                    return Activator.CreateInstance(t.TypeForCLR);
            }
            else
                throw new EntryPointNotFoundException();
        }*/

        public static StackObject* CreateInstance2(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var p = esp - 1;
            var t = mStack[p->Value] as Type;
            intp.Free(p);
            if (t != null)
            {
                if (t is ILRuntimeType)
                {
                    return ILIntepreter.PushObject(p, mStack, ((ILRuntimeType)t).ILType.Instantiate());
                }
                else
                    return ILIntepreter.PushObject(p, mStack, Activator.CreateInstance(t));
            }
            else
                return ILIntepreter.PushNull(p);
        }

        /*public static object CreateInstance2(ILContext ctx, object instance, object[] param, IType[] genericArguments)
        {
            var t = param[0] as Type;
            if (t != null)
            {
                if (t is ILRuntimeType)
                {
                    return ((ILRuntimeType)t).ILType.Instantiate();
                }
                else
                    return Activator.CreateInstance(t);
            }
            else
                return null;
        }*/

        public static StackObject* CreateInstance3(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var p = esp - 2;
            var t = mStack[p->Value] as Type;
            var p2 = esp - 1;
            var t2 = mStack[p2->Value] as Object[];
            intp.Free(p);
            if (t != null)
            {
                for (int i = 0; i < t2.Length; i++)
                {
                    if (t2[i] == null)
                    {
                        throw new ArgumentNullException();
                    }
                }

                if (t is ILRuntimeType)
                {
                    return ILIntepreter.PushObject(p, mStack, ((ILRuntimeType)t).ILType.Instantiate(t2));
                }
                else
                    return ILIntepreter.PushObject(p, mStack, Activator.CreateInstance(t, t2));
            }
            else
                return ILIntepreter.PushNull(p);
        }

        public static StackObject* GetType(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var p = esp - 1;
            AppDomain dommain = intp.AppDomain;
            string fullname = (string)StackObject.ToObject(p, dommain, mStack); ;
            intp.Free(p);
            var t = intp.AppDomain.GetType(fullname);
            if (t != null)
                return ILIntepreter.PushObject(p, mStack, t.ReflectionType);
            else
                return ILIntepreter.PushNull(p);
        }

        public static StackObject* TypeEquals(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var ret = ILIntepreter.Minus(esp, 2);
            var p = esp - 1;
            AppDomain dommain = intp.AppDomain;
            var other = StackObject.ToObject(p, dommain, mStack);
            intp.Free(p);
            p = ILIntepreter.Minus(esp, 2);
            var instance = StackObject.ToObject(p, dommain, mStack);
            intp.Free(p);
            if (instance is ILRuntimeType)
            {
                if (other is ILRuntimeType)
                {
                    if (((ILRuntimeType)instance).ILType == ((ILRuntimeType)other).ILType)
                        return ILIntepreter.PushOne(ret);
                    else
                        return ILIntepreter.PushZero(ret);
                }
                else
                    return ILIntepreter.PushZero(ret);
            }
            else
            {
                if (((Type)typeof(Type).CheckCLRTypes(instance)).Equals(((Type)typeof(Type).CheckCLRTypes(other))))
                    return ILIntepreter.PushOne(ret);
                else
                    return ILIntepreter.PushZero(ret);
            }
        }

        /*public static object GetType(ILContext ctx, object instance, object[] param, IType[] genericArguments)
        {
            var t = ctx.AppDomain.GetType((string)param[0]);
            if (t != null)
                return t.ReflectionType;
            else
                return null;
        }*/

        public static StackObject* IsAssignableFrom(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var ret = ILIntepreter.Minus(esp, 2);
            var p = esp - 1;
            AppDomain dommain = intp.AppDomain;
            var other = StackObject.ToObject(p, dommain, mStack);
            intp.Free(p);
            p = ILIntepreter.Minus(esp, 2);
            var instance = StackObject.ToObject(p, dommain, mStack);
            intp.Free(p);
            if (instance is ILRuntimeType)
            {
                if (other is ILRuntimeType)
                {
                    if (((ILRuntimeType)instance).IsAssignableFrom((ILRuntimeType)other))
                        return ILIntepreter.PushOne(ret);
                    else
                        return ILIntepreter.PushZero(ret);
                }
                else
                    return ILIntepreter.PushZero(ret);
            }
            else
            {
                if (instance is ILRuntimeWrapperType)
                {
                    if (((ILRuntimeWrapperType)instance).IsAssignableFrom((Type)other))
                        return ILIntepreter.PushOne(ret);
                    else
                        return ILIntepreter.PushZero(ret);
                }
                else
                {
                    if (((Type)instance).IsAssignableFrom((Type)other))
                        return ILIntepreter.PushOne(ret);
                    else
                        return ILIntepreter.PushZero(ret);
                }
            }
        }

        public unsafe static StackObject* InitializeArray(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var ret = esp - 2;
            AppDomain domain = intp.AppDomain;
            var param = esp - 1;
            byte[] data = StackObject.ToObject(param, domain, mStack) as byte[];
            intp.Free(param);
            param = esp - 2;
            object array = StackObject.ToObject(param, domain, mStack);
            intp.Free(param);

            if (data == null)
                return ret;
            fixed (byte* p = data)
            {
                /*Array oArr = (Array)array;
                if (oArr.Rank == 1)
                {
                    if (array is int[])
                    {
                        int[] arr = array as int[];
                        int* ptr = (int*)p;
                        for (int i = 0; i < arr.Length; i++)
                        {
                            arr[i] = ptr[i];
                        }
                    }
                    else if (array is byte[])
                    {
                        byte[] arr = array as byte[];
                        for (int i = 0; i < arr.Length; i++)
                        {
                            arr[i] = p[i];
                        }
                    }
                    else if (array is sbyte[])
                    {
                        sbyte[] arr = array as sbyte[];
                        sbyte* ptr = (sbyte*)p;
                        for (int i = 0; i < arr.Length; i++)
                        {
                            arr[i] = ptr[i];
                        }
                    }
                    else if (array is short[])
                    {
                        short[] arr = array as short[];
                        short* ptr = (short*)p;
                        for (int i = 0; i < arr.Length; i++)
                        {
                            arr[i] = ptr[i];
                        }
                    }
                    else if (array is ushort[])
                    {
                        ushort[] arr = array as ushort[];
                        ushort* ptr = (ushort*)p;
                        for (int i = 0; i < arr.Length; i++)
                        {
                            arr[i] = ptr[i];
                        }
                    }
                    else if (array is char[])
                    {
                        char[] arr = array as char[];
                        char* ptr = (char*)p;
                        for (int i = 0; i < arr.Length; i++)
                        {
                            arr[i] = ptr[i];
                        }
                    }
                    else if (array is uint[])
                    {
                        uint[] arr = array as uint[];
                        uint* ptr = (uint*)p;
                        for (int i = 0; i < arr.Length; i++)
                        {
                            arr[i] = ptr[i];
                        }
                    }
                    else if (array is Int64[])
                    {
                        long[] arr = array as long[];
                        long* ptr = (long*)p;
                        for (int i = 0; i < arr.Length; i++)
                        {
                            arr[i] = ptr[i];
                        }
                    }
                    else if (array is UInt64[])
                    {
                        ulong[] arr = array as ulong[];
                        ulong* ptr = (ulong*)p;
                        for (int i = 0; i < arr.Length; i++)
                        {
                            arr[i] = ptr[i];
                        }
                    }
                    else if (array is float[])
                    {
                        float[] arr = array as float[];
                        float* ptr = (float*)p;
                        for (int i = 0; i < arr.Length; i++)
                        {
                            arr[i] = ptr[i];
                        }
                    }
                    else if (array is double[])
                    {
                        double[] arr = array as double[];
                        double* ptr = (double*)p;
                        for (int i = 0; i < arr.Length; i++)
                        {
                            arr[i] = ptr[i];
                        }
                    }
                    else if (array is bool[])
                    {
                        bool[] arr = array as bool[];
                        bool* ptr = (bool*)p;
                        for (int i = 0; i < arr.Length; i++)
                        {
                            arr[i] = ptr[i];
                        }
                    }
                    else
                    {
                        throw new NotImplementedException("array=" + array.GetType());
                    }
                }
                else
                {
                    int* dst = (int*)System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(oArr, 0).ToPointer();

                        int* src = (int*)p;
                        int len = data.Length / sizeof(int);
                        for (int i = 0; i < len; i++)
                            dst[i] = src[i];
                    
                }*/
                Array arr = (Array)array;
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(arr, System.Runtime.InteropServices.GCHandleType.Pinned);
                var dst = System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(arr, 0);
                System.Runtime.InteropServices.Marshal.Copy(data, 0, dst, data.Length);
                handle.Free();
            }

            return ret;
        }

        /*public unsafe static object InitializeArray(ILContext ctx, object instance, object[] param, IType[] genericArguments)
        {
            object array = param[0];
            byte[] data = param[1] as byte[];
            if (data == null)
                return null;
            fixed (byte* p = data)
            {
                if (array is int[])
                {
                    int[] arr = array as int[];
                    int* ptr = (int*)p;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        arr[i] = ptr[i];
                    }
                }
                else if (array is byte[])
                {
                    byte[] arr = array as byte[];
                    for (int i = 0; i < arr.Length; i++)
                    {
                        arr[i] = p[i];
                    }
                }
                else if (array is sbyte[])
                {
                    sbyte[] arr = array as sbyte[];
                    sbyte* ptr = (sbyte*)p;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        arr[i] = ptr[i];
                    }
                }
                else if (array is short[])
                {
                    short[] arr = array as short[];
                    short* ptr = (short*)p;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        arr[i] = ptr[i];
                    }
                }
                else if (array is ushort[])
                {
                    ushort[] arr = array as ushort[];
                    ushort* ptr = (ushort*)p;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        arr[i] = ptr[i];
                    }
                }
                else if (array is char[])
                {
                    char[] arr = array as char[];
                    char* ptr = (char*)p;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        arr[i] = ptr[i];
                    }
                }
                else if (array is uint[])
                {
                    uint[] arr = array as uint[];
                    uint* ptr = (uint*)p;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        arr[i] = ptr[i];
                    }
                }
                else if (array is Int64[])
                {
                    long[] arr = array as long[];
                    long* ptr = (long*)p;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        arr[i] = ptr[i];
                    }
                }
                else if (array is UInt64[])
                {
                    ulong[] arr = array as ulong[];
                    ulong* ptr = (ulong*)p;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        arr[i] = ptr[i];
                    }
                }
                else if (array is float[])
                {
                    float[] arr = array as float[];
                    float* ptr = (float*)p;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        arr[i] = ptr[i];
                    }
                }
                else if (array is double[])
                {
                    double[] arr = array as double[];
                    double* ptr = (double*)p;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        arr[i] = ptr[i];
                    }
                }
                else if (array is bool[])
                {
                    bool[] arr = array as bool[];
                    bool* ptr = (bool*)p;
                    for (int i = 0; i < arr.Length; i++)
                    {
                        arr[i] = ptr[i];
                    }
                }
                else
                {
                    throw new NotImplementedException("array=" + array.GetType());
                }
            }

            return null;
        }*/

#if ENABLE_NEO_MODE
        // child-6 (neo-arrays): Neo redirect for
        // System.Runtime.CompilerServices.RuntimeHelpers.InitializeArray --
        // the C# array-initializer lowering
        //   newarr; dup; ldtoken <PrivateImplementationDetails blob>;
        //   call RuntimeHelpers.InitializeArray
        // Reads param 0 (the destination Array) and param 1 (the initializer
        // byte[]) as Neo references, then bulk-copies the blob into the pinned
        // array via Marshal.Copy (mirrors the Legacy CLRRedirections.Initialize
        // Array body). Void method -- no retDst write. The byte[] reaches
        // param 1 because the Neo ldtoken field path surfaces the
        // <PrivateImplementationDetails> RVA blob as a reference; CopyNeoCall-
        // Arguments copies each param by the callee's declared size (param 1 is
        // RuntimeFieldHandle, ~8 managed bytes), but ReadNeoReference consumes
        // only the leading 4-byte mStack index, so the byte[] is recovered
        // correctly regardless of the slot width. Cursor discipline mirrors
        // DelegateCombineNeo: param 0 is a 4-byte ref (Array), so param 1's
        // index sits at frameBase+4.
        public unsafe static void InitializeArrayNeo(ILIntepreter intp, byte* frameBase, AutoList mStack, CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            int curPrim = 0;
            object array = ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            object data = ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            if (data is byte[] bytes && array is Array arr && bytes.Length > 0)
            {
                var h = System.Runtime.InteropServices.GCHandle.Alloc(arr, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    var dst = System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(arr, 0);
                    System.Runtime.InteropServices.Marshal.Copy(bytes, 0, dst, bytes.Length);
                }
                finally
                {
                    h.Free();
                }
            }
            // void method -> no retDst write.
        }

        // Step 19: Neo redirect for System.Delegate.Combine (the C# `+=`
        // multicast lowering). Reads two Delegate params from the Neo param
        // region (each a 4-byte mStack index -- the object is an IDelegateAdapter
        // or a real Delegate), applies the multicast (adapter-next-chain OR the
        // native Delegate.Combine), and writes the result into the caller's dest
        // ref slot. Mirrors the Legacy StackObject DelegateCombine semantics.
        public unsafe static void DelegateCombineNeo(ILIntepreter intp, byte* frameBase, AutoList mStack, CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            int curPrim = 0;
            // Neo param region is laid out in source (declaration) order: param 0
            // = Delegate.Combine's first arg (dele1/source), param 1 = dele2/value.
            object dele1 = ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            object dele2 = ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            object result;
            if (dele1 != null)
            {
                if (dele2 != null)
                {
                    if (dele1 is IDelegateAdapter d1)
                    {
                        if (dele2 is IDelegateAdapter d2)
                        {
                            if (!d1.IsClone) d1 = d1.Clone();
                            if (!d2.IsClone) dele2 = d2.Clone();
                            d1.Combine((IDelegateAdapter)dele2);
                            result = d1;
                        }
                        else
                        {
                            if (!d1.IsClone) dele1 = d1.Clone();
                            ((IDelegateAdapter)dele1).Combine((Delegate)dele2);
                            result = dele1;
                        }
                    }
                    else
                    {
                        if (dele2 is IDelegateAdapter d2b)
                            result = Delegate.Combine((Delegate)dele1, d2b.GetConvertor(dele1.GetType()));
                        else
                            result = Delegate.Combine((Delegate)dele1, (Delegate)dele2);
                    }
                }
                else
                    result = dele1;
            }
            else
                result = dele2;
            WriteNeoDelegateResult(mStack, retDst, retRefBase, result);
        }

        // Step 19: Neo redirect for System.Delegate.Remove (the C# `-=` lowering).
        public unsafe static void DelegateRemoveNeo(ILIntepreter intp, byte* frameBase, AutoList mStack, CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            int curPrim = 0;
            object dele1 = ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            object dele2 = ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            object result;
            if (dele1 != null)
            {
                if (dele2 != null)
                {
                    if (dele1 is IDelegateAdapter d1)
                    {
                        if (dele2 is IDelegateAdapter d2)
                        {
                            if (d1.Equals(d2))
                                result = d1.Next;
                            else
                            {
                                d1.Remove(d2);
                                result = dele1;
                            }
                        }
                        else
                        {
                            d1.Remove((Delegate)dele2);
                            result = dele1;
                        }
                    }
                    else
                    {
                        if (dele2 is IDelegateAdapter d2b)
                            result = Delegate.Remove((Delegate)dele1, d2b.GetConvertor(dele1.GetType()));
                        else
                            result = Delegate.Remove((Delegate)dele1, (Delegate)dele2);
                    }
                }
                else
                    result = dele1;
            }
            else
                result = null;
            WriteNeoDelegateResult(mStack, retDst, retRefBase, result);
        }

        // Store a delegate-typed Combine/Remove result (an IDelegateAdapter or a
        // real Delegate) into the caller's dest ref slot + write the index.
        static unsafe void WriteNeoDelegateResult(AutoList mStack, byte* retDst, int retRefBase, object result)
        {
            if (retDst == null) return;
            if (retRefBase >= mStack.Count) mStack.Add(result);
            else mStack[retRefBase] = result;
            *(int*)retDst = retRefBase;
        }

        // Store an object-typed redirect result (e.g. an Activator.CreateInstance
        // return) into the caller's dest ref slot. Null-aware: a null result is
        // encoded as the Neo null sentinel (-1), matching ReadNeoReference
        // (idx >= 0 ? mStack[idx] : null) and InvokeNeoClrMethod's null-return
        // convention (ILIntepreter.Neo.cs:1082-1084). WriteNeoDelegateResult is
        // NOT null-aware (a Combine can yield a valid null-valued index), so
        // this is a distinct helper for redirects whose null means "no object".
        static unsafe void WriteNeoObjectResult(AutoList mStack, byte* retDst, int retRefBase, object result)
        {
            if (retDst == null) return;
            if (result == null)
            {
                *(int*)retDst = -1;
                return;
            }
            if (retRefBase >= mStack.Count) mStack.Add(result);
            else mStack[retRefBase] = result;
            *(int*)retDst = retRefBase;
        }

        // Neo redirect for the generic System.Activator.CreateInstance<T>().
        // Mirrors the Legacy CLRRedirections.CreateInstance (CLRRedirections.cs:30):
        // the type is the generic argument -- there is NO frame param. For an IL
        // type -> ILType.Instantiate(); for a CLR type -> CLRType.CreateDefault-
        // Instance(). Registered on RedirectMapNeo for the generic DEFINITION,
        // so CLRMethod.TryGetRedirection (which tries GetGenericMethodDefinition
        // first) serves this for every instantiation -- preempting the autogen
        // per-instantiation CreateInstance_*_Neo stubs (the generic stub calls
        // host Activator.CreateInstance<ILTypeInstance>() -> MissingMethodException).
        // The Legacy VT AllocValueType branches are unreachable in the current
        // smoke (no Activator.CreateInstance<VT> call sites); under Neo the
        // produced box is written as a reference, matching the autogen reference-
        // return convention.
        public unsafe static void CreateInstanceNeo(ILIntepreter intp, byte* frameBase, AutoList mStack, CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            IType[] genericArguments = method.GenericArguments;
            object result;
            if (genericArguments != null && genericArguments.Length == 1)
            {
                var t = genericArguments[0];
                if (t is ILType ilt)
                    result = ilt.Instantiate();
                else
                    result = ((CLRType)t).CreateDefaultInstance();
            }
            else
                throw new EntryPointNotFoundException();
            WriteNeoObjectResult(mStack, retDst, retRefBase, result);
        }

        // Neo redirect for System.Activator.CreateInstance(Type). Mirrors the
        // Legacy CLRRedirections.CreateInstance2 (CLRRedirections.cs:76). Param 0
        // is the Type. For an ILRuntimeType -> ILType.Instantiate(); else host
        // Activator.CreateInstance(type); a null Type -> null (Neo sentinel).
        public unsafe static void CreateInstance2Neo(ILIntepreter intp, byte* frameBase, AutoList mStack, CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            int curPrim = 0;
            Type t = (Type)ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            object result;
            if (t != null)
            {
                if (t is ILRuntimeType ilrt)
                    result = ilrt.ILType.Instantiate();
                else
                    result = Activator.CreateInstance(t);
            }
            else
                result = null;
            WriteNeoObjectResult(mStack, retDst, retRefBase, result);
        }

        // Neo redirect for System.Activator.CreateInstance(Type, object[]).
        // Mirrors the Legacy CLRRedirections.CreateInstance3 (CLRRedirections.cs:110).
        // Param 0 = Type, param 1 = object[] (the params array, itself a reference
        // recovered via ReadNeoReference). Each args element is null-checked
        // (Legacy throws ArgumentNullException). For an ILRuntimeType ->
        // ILType.Instantiate(object[]); else host Activator.CreateInstance(type, args).
        public unsafe static void CreateInstance3Neo(ILIntepreter intp, byte* frameBase, AutoList mStack, CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            int curPrim = 0;
            Type t = (Type)ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            object[] args = (object[])ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            object result;
            if (t != null)
            {
                if (args != null)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (args[i] == null)
                            throw new ArgumentNullException();
                    }
                }
                if (t is ILRuntimeType ilrt)
                    result = ilrt.ILType.Instantiate(args);
                else
                    result = Activator.CreateInstance(t, args);
            }
            else
                result = null;
            WriteNeoObjectResult(mStack, retDst, retRefBase, result);
        }

        // neo-typeof-generic-param: Neo redirect for System.Convert.ChangeType
        // (object, Type). Mirrors Legacy's autogen ChangeType_1, which reads its
        // System.Type param through typeof(System.Type).CheckCLRTypes -- that
        // unwraps an ILRuntimeWrapperType -> RealType before the framework call.
        // The Neo autogen ChangeType_1_Neo stub does a RAW (System.Type) cast on
        // ReadNeoReference, handing the framework the ILRuntimeWrapperType (what
        // CLRType.ReflectionType, the Neo ldtoken type-path push, yields) instead
        // of the real System.Type -> Convert.DefaultToType rejects it ("Invalid
        // cast String->Int32"). Registered on RedirectMapNeo (first-registered-
        // wins; this ctor runs before the test-harness System_Convert_Binding
        // autogen Register) so every Convert.ChangeType call under Neo unwraps the
        // conversion Type (and the value when it is likewise wrapped) before
        // delegating to the host. NOTE: a typeof(T) on a generic-method param
        // ADDITIONALLY requires the Step-22 generic-method template to re-resolve
        // the ldtoken type-token for the concrete T (the ExtractPatches Ldtoken
        // case in GenericMethodTemplate.cs) -- without it typeof(B) for B=double
        // keeps the capture-T int hash and unwraps to Int32, so ChangeType parses
        // "345.678" as int -> FormatException. The two fixes are complementary:
        // this redirect handles the wrapper, the template patch handles the
        // generic-param identity.
        public unsafe static void ChangeTypeNeo(ILIntepreter intp, byte* frameBase, AutoList mStack, CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            int curPrim = 0;
            object value = ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            Type conversionType = (Type)ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            // Unwrap ILRuntime reflection Type wrappers -> the real System.Type
            // the framework Convert.DefaultToType understands (mirrors Legacy
            // CheckCLRTypes' ILRuntimeWrapperType -> RealType branch).
            if (conversionType is ILRuntimeWrapperType wt) conversionType = wt.RealType;
            else if (conversionType is ILRuntimeType rt) conversionType = rt.ILType.TypeForCLR;
            if (value is ILRuntimeWrapperType wv) value = wv.RealType;
            var result = Convert.ChangeType(value, conversionType);
            WriteNeoObjectResult(mStack, retDst, retRefBase, result);
        }

        // Neo redirect for System.Object.GetType(). Mirrors the Legacy
        // CLRRedirections.ObjectGetType (CLRRedirections.cs:1250). The `this`
        // (param 0, a 4-byte mStack index at frameBase offset 0 -- the callvirt
        // thisArgOffset is always 0) is recovered via ReadNeoReference. For an IL
        // instance (ILTypeInstance / ILEnumTypeInstance) the raw CLR GetType would
        // return typeof(ILTypeInstance); instead return the IL type's ReflectionType
        // (the ILRuntimeType), matching Legacy + C# `typeof` semantics for IL types.
        // Any other (CLR) instance -> its real CLR Type. Registered on RedirectMapNeo
        // so InvokeNeoClrMethod serves it for every obj.GetType() callvirt (IL or CLR
        // receiver). Reached after the ResolveNeoCallvirtILTarget CLR-fallback routes
        // the inherited non-virtual GetType (no VTable slot) to CLR dispatch.
        public unsafe static void ObjectGetTypeNeo(ILIntepreter intp, byte* frameBase, AutoList mStack, CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            int curPrim = 0;
            object instance = ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            object result;
            if (instance == null)
            {
                // A null `this` is guarded upstream (ReadNeoCallThis throws NRE before
                // the redirect); defensively emit the null sentinel if ever reached.
                result = null;
            }
            else
            {
                var type = instance.GetType();
                if (type == typeof(ILTypeInstance) || type == typeof(ILEnumTypeInstance))
                    result = ((ILTypeInstance)instance).Type.ReflectionType;
                else
                    result = type;
            }
            WriteNeoObjectResult(mStack, retDst, retRefBase, result);
        }

        // C4-residual (neo-callvirt-this-null): Neo redirect for the STATIC
        // System.Type.GetType(string[, bool, bool]) overloads. Mirrors the Legacy
        // CLRRedirections.GetType (CLRRedirections.cs:138). Under Neo the Legacy
        // redirect (registered on RedirectMap only at AppDomain.cs:201) never runs
        // -- Neo dispatch consults RedirectMapNeo exclusively (child-2/6/22). With
        // no Neo entry the call fell through to the host System.Type.GetType,
        // which cannot resolve an IL type name -> returned null -> the IL code then
        // did `t.GetMethod(...)` on a null `this` -> "Neo callvirt this is null"
        // at ResolveNeoCallvirtCLRTarget (ReflectionTest04/19). Param 0 (DECLARATION
        // order under the Neo cursor) is the fullname string for every overload;
        // the extra bool params (throwOnError / ignoreCase) are ignored (we return
        // the Neo null sentinel when the type is not found, matching the
        // throwOnError=false behavior). Registered on RedirectMapNeo in the
        // AppDomain ctor (first-registered-wins preempts any autogen stub).
        public unsafe static void GetTypeNeo(ILIntepreter intp, byte* frameBase, AutoList mStack, CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            int curPrim = 0;
            string fullname = (string)ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            object result = null;
            if (fullname != null)
            {
                var t = intp.AppDomain.GetType(fullname);
                if (t != null)
                    result = t.ReflectionType;
            }
            WriteNeoObjectResult(mStack, retDst, retRefBase, result);
        }

        // C12 (neo-ilruntimetype-runtime-bridge): Neo redirect for
        // Delegate.CreateDelegate(Type, MethodInfo). Mirrors the Legacy
        // DelegateCreateDelegate (CLRRedirections.cs:1607). Under Neo the
        // Legacy redirect (on RedirectMap only) never runs -> the reflection
        // fallback passed the ILRuntimeType raw to the framework
        // Delegate.CreateDelegate, throwing "Type must be a runtime Type".
        // Params are read in DECLARATION order via the Neo cursor (param 0 =
        // Type, param 1 = MethodInfo); Legacy read them stack-reverse. For an
        // ILRuntimeType delegate + ILRuntimeMethodInfo -> build the IL delegate
        // adapter (DelegateManager.FindDelegateAdapter); for an
        // ILRuntimeWrapperType -> adapter or host Delegate.CreateDelegate; else
        // host Delegate.CreateDelegate. Registered on RedirectMapNeo in the
        // AppDomain ctor (first-registered-wins preempts any autogen stub).
        public unsafe static void DelegateCreateDelegateNeo(ILIntepreter intp, byte* frameBase, AutoList mStack, CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            AppDomain domain = intp.AppDomain;
            int curPrim = 0;
            Type t = (Type)ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            MethodInfo mi = (MethodInfo)ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            object result;
            if (t is ILRuntimeType)
            {
                ILType it = ((ILRuntimeType)t).ILType;
                if (it.IsDelegate)
                {
                    var m = it.GetMethod("Invoke") as ILMethod;
                    if (mi is ILRuntimeMethodInfo)
                    {
                        ILRuntimeMethodInfo imi = (ILRuntimeMethodInfo)mi;
                        var ilMethod = imi.ILMethod;
                        if (ilMethod.DelegateAdapter == null)
                        {
                            ilMethod.DelegateAdapter = domain.DelegateManager.FindDelegateAdapter(null, ilMethod, m);
                        }
                        result = ilMethod.DelegateAdapter;
                    }
                    else
                    {
                        throw new NotSupportedException();
                    }
                }
                else
                    throw new NotSupportedException(string.Format("{0} is not Delegate", t.FullName));
            }
            else if (t is ILRuntimeWrapperType)
            {
                ILRuntimeWrapperType iwt = (ILRuntimeWrapperType)t;
                if (mi is ILRuntimeMethodInfo)
                {
                    ILRuntimeMethodInfo imi = (ILRuntimeMethodInfo)mi;
                    result = domain.DelegateManager.FindDelegateAdapter(iwt.CLRType, null, imi.ILMethod);
                }
                else
                {
                    result = Delegate.CreateDelegate(iwt.RealType, mi);
                }
            }
            else
                result = Delegate.CreateDelegate(t, mi);
            WriteNeoObjectResult(mStack, retDst, retRefBase, result);
        }

        // C12: Neo redirect for Delegate.CreateDelegate(Type, object, string).
        // Mirrors Legacy DelegateCreateDelegate2 (CLRRedirections.cs:1666).
        public unsafe static void DelegateCreateDelegate2Neo(ILIntepreter intp, byte* frameBase, AutoList mStack, CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            AppDomain domain = intp.AppDomain;
            int curPrim = 0;
            Type t = (Type)ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            object obj = (object)ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            string name = (string)ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            if (obj == null)
                throw new ArgumentNullException("Argument target cannot be null");
            object result;
            if (t is ILRuntimeType)
            {
                ILType it = ((ILRuntimeType)t).ILType;
                if (it.IsDelegate)
                {
                    var m = it.GetMethod("Invoke") as ILMethod;
                    if (obj is ILTypeInstance)
                    {
                        ILTypeInstance ii = (ILTypeInstance)obj;
                        var ilMethod = ii.Type.GetMethod(name) as ILMethod;
                        if (ilMethod == null)
                            throw new ArgumentException(string.Format("Cannot find method \"{0}\" in type {1}", name, it.FullName));
                        if (ilMethod.DelegateAdapter == null)
                        {
                            ilMethod.DelegateAdapter = domain.DelegateManager.FindDelegateAdapter(ii, ilMethod, m);
                        }
                        result = ilMethod.DelegateAdapter;
                    }
                    else
                        throw new NotSupportedException();
                }
                else
                    throw new NotSupportedException(string.Format("{0} is not Delegate", t.FullName));
            }
            else if (t is ILRuntimeWrapperType)
            {
                ILRuntimeWrapperType iwt = (ILRuntimeWrapperType)t;
                if (obj is ILTypeInstance)
                {
                    ILTypeInstance ii = (ILTypeInstance)obj;
                    var ilMethod = ii.Type.GetMethod(name) as ILMethod;
                    if (ilMethod == null)
                        throw new ArgumentException(string.Format("Cannot find method \"{0}\" in type {1}", name, ii.Type.FullName));
                    result = domain.DelegateManager.FindDelegateAdapter(iwt.CLRType, ii, ilMethod);
                }
                else
                {
                    result = Delegate.CreateDelegate(iwt.RealType, obj, name);
                }
            }
            else
                result = Delegate.CreateDelegate(t, obj, name);
            WriteNeoObjectResult(mStack, retDst, retRefBase, result);
        }

        // C12: Neo redirect for Delegate.CreateDelegate(Type, object, MethodInfo).
        // Mirrors Legacy DelegateCreateDelegate3 (CLRRedirections.cs:1733).
        public unsafe static void DelegateCreateDelegate3Neo(ILIntepreter intp, byte* frameBase, AutoList mStack, CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            AppDomain domain = intp.AppDomain;
            int curPrim = 0;
            Type t = (Type)ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            object obj = (object)ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            MethodInfo mi = (MethodInfo)ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            object result;
            if (t is ILRuntimeType)
            {
                ILType it = ((ILRuntimeType)t).ILType;
                if (it.IsDelegate)
                {
                    if (mi is ILRuntimeMethodInfo)
                    {
                        ILRuntimeMethodInfo imi = (ILRuntimeMethodInfo)mi;
                        var ilMethod = imi.ILMethod;
                        if (obj != null)
                        {
                            result = ((ILTypeInstance)obj).GetDelegateAdapter(ilMethod);
                            if (result == null)
                            {
                                var invokeMethod = it.GetMethod("Invoke", ilMethod.ParameterCount);
                                if (invokeMethod == null && ilMethod.IsExtend)
                                {
                                    invokeMethod = it.GetMethod("Invoke", ilMethod.ParameterCount - 1);
                                }
                                result = domain.DelegateManager.FindDelegateAdapter(
                                    (ILTypeInstance)obj, ilMethod, invokeMethod);
                            }
                        }
                        else
                        {
                            if (ilMethod.DelegateAdapter == null)
                            {
                                var m = it.GetMethod("Invoke") as ILMethod;
                                ilMethod.DelegateAdapter = domain.DelegateManager.FindDelegateAdapter(null, ilMethod, m);
                            }
                            result = ilMethod.DelegateAdapter;
                        }
                    }
                    else
                    {
                        throw new NotSupportedException();
                    }
                }
                else
                    throw new NotSupportedException(string.Format("{0} is not Delegate", t.FullName));
            }
            else if (t is ILRuntimeWrapperType)
            {
                ILRuntimeWrapperType iwt = (ILRuntimeWrapperType)t;
                if (mi is ILRuntimeMethodInfo)
                {
                    ILRuntimeMethodInfo imi = (ILRuntimeMethodInfo)mi;
                    result = domain.DelegateManager.FindDelegateAdapter(iwt.CLRType, obj as ILTypeInstance, imi.ILMethod);
                }
                else
                {
                    result = Delegate.CreateDelegate(iwt.RealType, obj, mi);
                }
            }
            else
                result = Delegate.CreateDelegate(t, obj, mi);
            WriteNeoObjectResult(mStack, retDst, retRefBase, result);
        }

        // C12: Neo redirect for Enum.ToObject(Type, int). Mirrors Legacy
        // EnumToObject (CLRRedirections.cs:1509). Under Neo the autogen
        // ToObject_3_Neo stub (System_Enum_Binding.cs:173) passes the
        // ILRuntimeType raw to the framework Enum.ToObject -> "Type must be a
        // type provided by the runtime". This redirect bridges an IL enum type
        // to an ILEnumTypeInstance carrying the underlying value. Param 0 =
        // Type (reference), param 1 = int (primitive, via ReadNeoInt32). Under
        // Neo an ILEnumTypeInstance stores its value as raw bytes in
        // `fields` (sized to the underlying primitive), so the int is written
        // as the underlying-type bytes (sign-extended for a long-backed enum,
        // truncated for byte/short).
        public unsafe static void EnumToObjectNeo(ILIntepreter intp, byte* frameBase, AutoList mStack, CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            int curPrim = 0;
            Type t = (Type)ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            int val = ILIntepreter.ReadNeoInt32(frameBase, ref curPrim);
            object result;
            if (t is ILRuntimeType)
            {
                ILType it = ((ILRuntimeType)t).ILType;
                if (it.IsEnum)
                {
                    ILEnumTypeInstance ins = new ILEnumTypeInstance(it);
                    byte[] fields = ins.Primitives;
                    byte[] raw = fields.Length == 8 ? BitConverter.GetBytes((long)val) : BitConverter.GetBytes(val);
                    int n = fields.Length < raw.Length ? fields.Length : raw.Length;
                    Buffer.BlockCopy(raw, 0, fields, 0, n);
                    result = ins;
                }
                else
                    throw new Exception(string.Format("{0} is not Enum", t.FullName));
            }
            else if (t is ILRuntimeWrapperType)
            {
                result = Enum.ToObject(((ILRuntimeWrapperType)t).RealType, val);
            }
            else
                result = Enum.ToObject(t, val);
            WriteNeoObjectResult(mStack, retDst, retRefBase, result);
        }
#endif

        public unsafe static StackObject* DelegateCombine(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            //Don't ask me why not esp -2, unity won't return the right result
            var ret = esp - 2;
            AppDomain domain = intp.AppDomain;
            var param = esp - 1;
            object dele2 = StackObject.ToObject(param, domain, mStack);
            intp.Free(param);

            param = esp - 2;
            object dele1 = StackObject.ToObject(param, domain, mStack);
            intp.Free(param);

            if (dele1 != null)
            {
                if (dele2 != null)
                {
                    if (dele1 is IDelegateAdapter)
                    {
                        if (dele2 is IDelegateAdapter)
                        {
                            var dele = ((IDelegateAdapter)dele1);
                            //This means it's the original delegate which should be untouch
                            if (!dele.IsClone)
                            {
                                dele = dele.Clone();
                            }
                            if (!((IDelegateAdapter)dele2).IsClone)
                            {
                                dele2 = ((IDelegateAdapter)dele2).Clone();
                            }
                            dele.Combine((IDelegateAdapter)dele2);
                            return ILIntepreter.PushObject(ret, mStack, dele);
                        }
                        else
                        {
                            if (!((IDelegateAdapter)dele1).IsClone)
                            {
                                dele1 = ((IDelegateAdapter)dele1).Clone();
                            }
                            ((IDelegateAdapter)dele1).Combine((Delegate)dele2);
                            return ILIntepreter.PushObject(ret, mStack, dele1);
                        }
                    }
                    else
                    {
                        if (dele2 is IDelegateAdapter)
                            return ILIntepreter.PushObject(ret, mStack, Delegate.Combine((Delegate)dele1, ((IDelegateAdapter)dele2).GetConvertor(dele1.GetType())));
                        else
                            return ILIntepreter.PushObject(ret, mStack, Delegate.Combine((Delegate)dele1, (Delegate)dele2));
                    }
                }
                else
                    return ILIntepreter.PushObject(ret, mStack, dele1);
            }
            else
                return ILIntepreter.PushObject(ret, mStack, dele2);
        }

        /*public unsafe static object DelegateCombine(ILContext ctx, object instance, object[] param, IType[] genericArguments)
        {
            var esp = ctx.ESP;
            var mStack = ctx.ManagedStack;
            var domain = ctx.AppDomain;

            //Don't ask me why not esp -2, unity won't return the right result
            var dele1 = StackObject.ToObject((esp - 2), domain, mStack);
            var dele2 = StackObject.ToObject((esp - 1), domain, mStack);

            if (dele1 != null)
            {
                if (dele2 != null)
                {
                    if (dele1 is IDelegateAdapter)
                    {
                        if (dele2 is IDelegateAdapter)
                        {
                            var dele = ((IDelegateAdapter)dele1);
                            //This means it's the default delegate which should be singleton to support == operator
                            if (dele.Next == null)
                            {
                                dele = dele.Instantiate(domain, dele.Instance, dele.Method);
                            }
                            dele.Combine((IDelegateAdapter)dele2);
                            return dele;
                        }
                        else
                        {
                            ((IDelegateAdapter)dele1).Combine((Delegate)dele2);
                            return dele1;
                        }
                    }
                    else
                    {
                        if (dele2 is IDelegateAdapter)
                            return Delegate.Combine((Delegate)dele1, ((IDelegateAdapter)dele2).GetConvertor(dele1.GetType()));
                        else
                            return Delegate.Combine((Delegate)dele1, (Delegate)dele2);
                    }
                }
                else
                    return dele1;
            }
            else
                return dele2;
        }*/

        public unsafe static StackObject* DelegateRemove(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            //Don't ask me why not esp -2, unity won't return the right result
            var ret = esp - 2;
            AppDomain domain = intp.AppDomain;
            var param = esp - 1;
            object dele2 = StackObject.ToObject(param, domain, mStack);
            intp.Free(param);

            param = esp - 2;
            object dele1 = StackObject.ToObject(param, domain, mStack);
            intp.Free(param);

            if (dele1 != null)
            {
                if (dele2 != null)
                {
                    if (dele1 is IDelegateAdapter)
                    {
                        if (dele2 is IDelegateAdapter)
                        {
                            if (((IDelegateAdapter)dele1).Equals((IDelegateAdapter)dele2))
                                return ILIntepreter.PushObject(ret, mStack, ((IDelegateAdapter)dele1).Next);
                            else
                                ((IDelegateAdapter)dele1).Remove((IDelegateAdapter)dele2);
                        }
                        else
                            ((IDelegateAdapter)dele1).Remove((Delegate)dele2);
                        return ILIntepreter.PushObject(ret, mStack, dele1);
                    }
                    else
                    {
                        if (dele2 is IDelegateAdapter)
                            return ILIntepreter.PushObject(ret, mStack, Delegate.Remove((Delegate)dele1, ((IDelegateAdapter)dele2).GetConvertor(dele1.GetType())));
                        else
                            return ILIntepreter.PushObject(ret, mStack, Delegate.Remove((Delegate)dele1, (Delegate)dele2));
                    }
                }
                else
                    return ILIntepreter.PushObject(ret, mStack, dele1);
            }
            else
                return ILIntepreter.PushNull(ret);
        }

        /*public unsafe static object DelegateRemove(ILContext ctx, object instance, object[] param, IType[] genericArguments)
        {
            var esp = ctx.ESP;
            var mStack = ctx.ManagedStack;
            var domain = ctx.AppDomain;

            var dele1 = StackObject.ToObject((esp - 2), domain, mStack);
            var dele2 = StackObject.ToObject((esp - 1), domain, mStack);

            if (dele1 != null)
            {
                if (dele2 != null)
                {
                    if (dele1 is IDelegateAdapter)
                    {
                        if (dele2 is IDelegateAdapter)
                        {
                            if (dele1 == dele2)
                                return ((IDelegateAdapter)dele1).Next;
                            else
                                ((IDelegateAdapter)dele1).Remove((IDelegateAdapter)dele2);
                        }
                        else
                            ((IDelegateAdapter)dele1).Remove((Delegate)dele2);
                        return dele1;
                    }
                    else
                    {
                        if (dele2 is IDelegateAdapter)
                            return Delegate.Remove((Delegate)dele1, ((IDelegateAdapter)dele2).GetConvertor(dele1.GetType()));
                        else
                            return Delegate.Remove((Delegate)dele1, (Delegate)dele2);
                    }
                }
                else
                    return dele1;
            }
            else
                return null;
        }*/
        public unsafe static StackObject* DelegateGetTarget(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var ret = esp - 1;
            AppDomain domain = intp.AppDomain;
            var param = esp - 1;
            object dele = StackObject.ToObject(param, domain, mStack);
            intp.Free(param);
            if (dele != null)
            {
                if (dele is IDelegateAdapter)
                {
                    return ILIntepreter.PushObject(ret, mStack, ((IDelegateAdapter)dele).Instance);
                }
                else
                {
                    return ILIntepreter.PushObject(ret, mStack, ((Delegate)dele).Target);
                }
            }
            else
                throw new NullReferenceException();
        }

        public unsafe static StackObject* DelegateEqulity(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            //Don't ask me why not esp -2, unity won't return the right result
            var ret = esp - 2;
            AppDomain domain = intp.AppDomain;
            var param = esp - 1;
            object dele2 = StackObject.ToObject(param, domain, mStack);
            intp.Free(param);

            param = esp - 2;
            object dele1 = StackObject.ToObject(param, domain, mStack);
            intp.Free(param);

            bool res = false;
            if (dele1 != null)
            {
                if (dele2 != null)
                {
                    if (dele1 is IDelegateAdapter)
                    {
                        if (dele2 is IDelegateAdapter)
                            res = ((IDelegateAdapter)dele1).Equals((IDelegateAdapter)dele2);
                        else
                            res = ((IDelegateAdapter)dele1).Equals((Delegate)dele2);
                    }
                    else
                    {
                        if (dele2 is IDelegateAdapter)
                        {
                            res = (Delegate)dele1 == ((IDelegateAdapter)dele2).GetConvertor(dele1.GetType());
                        }
                        else
                            res = (Delegate)dele1 == (Delegate)dele2;
                    }
                }
                else
                    res = dele1 == null;
            }
            else
                res = dele2 == null;

            if (res)
                return ILIntepreter.PushOne(ret);
            else
                return ILIntepreter.PushZero(ret);
        }

        /*public unsafe static object DelegateEqulity(ILContext ctx, object instance, object[] param, IType[] genericArguments)
        {
            //op_Equality,op_Inequality
            var esp = ctx.ESP;
            var mStack = ctx.ManagedStack;
            var domain = ctx.AppDomain;

            var dele1 = StackObject.ToObject((esp - 2), domain, mStack);
            var dele2 = StackObject.ToObject((esp - 1), domain, mStack);

            if (dele1 != null)
            {
                if (dele2 != null)
                {
                    if (dele1 is IDelegateAdapter)
                    {
                        if (dele2 is IDelegateAdapter)
                            return ((IDelegateAdapter)dele1).Equals((IDelegateAdapter)dele2);
                        else
                            return ((IDelegateAdapter)dele1).Equals((Delegate)dele2);
                    }
                    else
                    {
                        if (dele2 is IDelegateAdapter)
                        {
                            return (Delegate)dele1 == ((IDelegateAdapter)dele2).GetConvertor(dele1.GetType());
                        }
                        else
                            return (Delegate)dele1 == (Delegate)dele2;
                    }
                }
                else
                    return dele1 == null;
            }
            else
                return dele2 == null;
        }*/

        public unsafe static StackObject* DelegateInequlity(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            //Don't ask me why not esp -2, unity won't return the right result
            var ret = esp - 2;
            AppDomain domain = intp.AppDomain;
            var param = esp - 1;
            object dele2 = StackObject.ToObject(param, domain, mStack);
            intp.Free(param);

            param = esp - 2;
            object dele1 = StackObject.ToObject(param, domain, mStack);
            intp.Free(param);

            bool res = false;
            if (dele1 != null)
            {
                if (dele2 != null)
                {
                    if (dele1 is IDelegateAdapter)
                    {
                        if (dele2 is IDelegateAdapter)
                            res = !((IDelegateAdapter)dele1).Equals((IDelegateAdapter)dele2);
                        else
                            res = !((IDelegateAdapter)dele1).Equals((Delegate)dele2);
                    }
                    else
                    {
                        if (dele2 is IDelegateAdapter)
                            res = (Delegate)dele1 != ((IDelegateAdapter)dele2).GetConvertor(dele1.GetType());
                        else
                            res = (Delegate)dele1 != (Delegate)dele2;
                    }
                }
                else
                    res = dele1 != null;
            }
            else
                res = dele2 != null;
            if (res)
                return ILIntepreter.PushOne(ret);
            else
                return ILIntepreter.PushZero(ret);
        }

        /*public unsafe static object DelegateInequlity(ILContext ctx, object instance, object[] param, IType[] genericArguments)
        {
            //op_Equality,op_Inequality
            var esp = ctx.ESP;
            var mStack = ctx.ManagedStack;
            var domain = ctx.AppDomain;

            var dele1 = StackObject.ToObject((esp - 2), domain, mStack);
            var dele2 = StackObject.ToObject((esp - 1), domain, mStack);

            if (dele1 != null)
            {
                if (dele2 != null)
                {
                    if (dele1 is IDelegateAdapter)
                    {
                        if (dele2 is IDelegateAdapter)
                            return !((IDelegateAdapter)dele1).Equals((IDelegateAdapter)dele2);
                        else
                            return !((IDelegateAdapter)dele1).Equals((Delegate)dele2);
                    }
                    else
                    {
                        if (dele2 is IDelegateAdapter)
                            return (Delegate)dele1 != ((IDelegateAdapter)dele2).GetConvertor(dele1.GetType());
                        else
                            return (Delegate)dele1 != (Delegate)dele2;
                    }
                }
                else
                    return dele1 != null;
            }
            else
                return dele2 != null;
        }*/

        public static StackObject* GetTypeFromHandle(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            //Nothing to do
            return esp;
        }

        /*public static object GetTypeFromHandle(ILContext ctx, object instance, object[] param, IType[] genericArguments)
        {
            return param[0];
        }*/

        public unsafe static StackObject* MethodInfoInvoke(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            AppDomain domain = intp.AppDomain;
            //Don't ask me why not esp - 3, unity won't return the right result
            var ret = ILIntepreter.Minus(esp, 3);
            var param = esp - 1;
            var p = StackObject.ToObject(param, domain, mStack);
            intp.Free(param);

            param = esp - 2;
            var obj = StackObject.ToObject(param, domain, mStack);
            intp.Free(param);

            param = ILIntepreter.Minus(esp, 3);
            object instance = CheckCrossBindingAdapter(StackObject.ToObject(param, domain, mStack));
            intp.Free(param);

            if (instance is ILRuntimeMethodInfo)
            {
                if (obj != null)
                    esp = ILIntepreter.PushObject(ret, mStack, obj);
                else
                    esp = ret;
                var ilmethod = ((ILRuntimeMethodInfo)instance).ILMethod;
                bool useRegister = ilmethod.ShouldUseRegisterVM;
                if (p != null)
                {
                    object[] arr = (object[])p;
                    for (int i = 0; i < ilmethod.ParameterCount; i++)
                    {
                        var res = ILIntepreter.PushObject(esp, mStack, CheckCrossBindingAdapter(arr[i]));
                        if (esp->ObjectType < ObjectTypes.Object && useRegister)
                            mStack.Add(null);
                        esp = res;
                    }
                }
                bool unhandled;
                if (useRegister)
                    ret = intp.ExecuteR(ilmethod, esp, out unhandled);
                else
                    ret = intp.Execute(ilmethod, esp, out unhandled);
                ILRuntimeMethodInfo imi = (ILRuntimeMethodInfo)instance;
                var rt = imi.ILMethod.ReturnType;
                if (rt != domain.VoidType)
                {
                    var res = ret - 1;
                    if (res->ObjectType < ObjectTypes.Object)
                    {
                        return ILIntepreter.PushObject(res, mStack, rt.TypeForCLR.CheckCLRTypes(StackObject.ToObject(res, domain, mStack)), true);
                    }
                    else
                        return ret;
                }
                else
                    return ILIntepreter.PushNull(ret);
            }
            else
                return ILIntepreter.PushObject(ret, mStack, ((MethodInfo)instance).Invoke(obj, (object[])p));
        }

        static object CheckCrossBindingAdapter(object obj)
        {
            if (obj is CrossBindingAdaptorType)
            {
                return ((CrossBindingAdaptorType)obj).ILInstance;
            }
            return obj;
        }

        /*public unsafe static object MethodInfoInvoke(ILContext ctx, object instance, object[] param, IType[] genericArguments)
        {
            var esp = ctx.ESP;
            var mStack = ctx.ManagedStack;
            var domain = ctx.AppDomain;
            var intp = ctx.Interpreter;
            //Don't ask me why not esp - 3, unity won't return the right result
            var obj = param[0];
            var p = param[1];

            if (instance is ILRuntimeMethodInfo)
            {
                if (obj != null)
                    esp = ILIntepreter.PushObject(esp, mStack, obj);
                if (p != null)
                {
                    object[] arr = (object[])p;
                    foreach (var i in arr)
                    {
                        esp = ILIntepreter.PushObject(esp, mStack, i);
                    }
                }
                bool unhandled;
                var ilmethod = ((ILRuntimeMethodInfo)instance).ILMethod;
                esp = intp.Execute(ilmethod, esp, out unhandled);
                object res = null;
                if (ilmethod.ReturnType != domain.VoidType)
                {
                    res = StackObject.ToObject((esp - 1),domain, mStack);
                    intp.Free(esp - 1);
                }
                return res;
            }
            else
                return ((MethodInfo)instance).Invoke(obj, (object[])p);
        }*/

        public unsafe static StackObject* ObjectGetType(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            AppDomain domain = intp.AppDomain;
            var ret = esp - 1;
            var param = esp - 1;
            var instance = StackObject.ToObject(param, domain, mStack);
            intp.Free(param);

            var type = instance.GetType();
            if (type == typeof(ILTypeInstance) || type == typeof(ILEnumTypeInstance))
            {
                return ILIntepreter.PushObject(ret, mStack, ((ILTypeInstance)instance).Type.ReflectionType);
            }
            else
                return ILIntepreter.PushObject(ret, mStack, type);
        }

        /*public unsafe static object ObjectGetType(ILContext ctx, object instance, object[] param, IType[] genericArguments)
        {
            var type = instance.GetType();
            if (type == typeof(ILTypeInstance))
            {
                return ((ILTypeInstance)instance).Type.ReflectionType;
            }
            else
                return type;
        }*/
        public static StackObject* EnumParse(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var ret = esp - 2;
            AppDomain domain = intp.AppDomain;

            var p = esp - 1;
            string name = (string)StackObject.ToObject(p, domain, mStack);
            intp.Free(p);

            p = esp - 2;
            Type t = (Type)StackObject.ToObject(p, domain, mStack);
            intp.Free(p);
            if (t is ILRuntimeType)
            {
                ILType it = ((ILRuntimeType)t).ILType;
                if (it.IsEnum)
                {
                    var fields = it.TypeDefinition.Fields;
                    for (int i = 0; i < fields.Count; i++)
                    {
                        var f = fields[i];
                        if (f.IsStatic)
                        {
                            if (f.Name == name)
                            {
                                ILEnumTypeInstance ins = new ILEnumTypeInstance(it);
                                ins[0] = f.Constant;
                                ins.Boxed = true;

                                return ILIntepreter.PushObject(ret, mStack, ins, true);
                            }
                            else
                            {
                                int val;
                                if (int.TryParse(name, out val))
                                {
                                    if ((int)f.Constant == val)
                                    {
                                        ILEnumTypeInstance ins = new ILEnumTypeInstance(it);
                                        ins[0] = f.Constant;
                                        ins.Boxed = true;

                                        return ILIntepreter.PushObject(ret, mStack, ins, true);
                                    }
                                }
                            }
                        }
                    }
                    return ILIntepreter.PushNull(ret);
                }
                else
                    throw new Exception(string.Format("{0} is not Enum", t.FullName));
            }
            else if (t is ILRuntimeWrapperType)
            {
                return ILIntepreter.PushObject(ret, mStack, Enum.Parse(((ILRuntimeWrapperType)t).RealType, name), true);
            }
            else
                return ILIntepreter.PushObject(ret, mStack, Enum.Parse(t, name), true);
        }

        public static StackObject* EnumGetValues(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var ret = esp - 1;
            AppDomain domain = intp.AppDomain;

            var p = esp - 1;
            Type t = (Type)StackObject.ToObject(p, domain, mStack);
            intp.Free(p);
            if (t is ILRuntimeType)
            {
                ILType it = ((ILRuntimeType)t).ILType;
                object res;
                //List<ILTypeInstance> res = new List<ILTypeInstance>();
                if (it.IsEnum)
                {
                    IList list = null;
                    bool islong = false;
                    var fields = it.TypeDefinition.Fields;
                    for (int i = 0; i < fields.Count; i++)
                    {
                        var f = fields[i];
                        if (f.IsStatic)
                        {
                            if (list == null)
                            {
                                if (f.Constant is long)
                                {
                                    list = new List<long>();
                                    islong = true;
                                }
                                else
                                    list = new List<int>();
                            }
                            list.Add(f.Constant);
                        }
                    }
                    if (islong)
                        res = ((List<long>)list).ToArray();
                    else
                    {
                        if (list == null)
                            res = new int[0];
                        else
                            res = ((List<int>)list).ToArray();
                    }
                    return ILIntepreter.PushObject(ret, mStack, res, true);
                }
                else
                    throw new Exception(string.Format("{0} is not Enum", t.FullName));
            }
            else if (t is ILRuntimeWrapperType)
            {
                return ILIntepreter.PushObject(ret, mStack, Enum.GetValues(((ILRuntimeWrapperType)t).RealType), true);
            }
            else
                return ILIntepreter.PushObject(ret, mStack, Enum.GetValues(t), true);
        }

        public static StackObject* EnumGetNames(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var ret = esp - 1;
            AppDomain domain = intp.AppDomain;
            var p = esp - 1;
            Type t = (Type)StackObject.ToObject(p, domain, mStack);
            intp.Free(p);
            if (t is ILRuntimeType)
            {
                ILType it = ((ILRuntimeType)t).ILType;
                List<string> res = new List<string>();
                if (it.IsEnum)
                {
                    var fields = it.TypeDefinition.Fields;
                    for (int i = 0; i < fields.Count; i++)
                    {
                        var f = fields[i];
                        if (f.IsStatic)
                        {
                            res.Add(f.Name);
                        }
                    }
                    return ILIntepreter.PushObject(ret, mStack, res.ToArray(), true);
                }
                else
                    throw new Exception(string.Format("{0} is not Enum", t.FullName));
            }
            else if (t is ILRuntimeWrapperType)
            {
                return ILIntepreter.PushObject(ret, mStack, Enum.GetNames(((ILRuntimeWrapperType)t).RealType), true);
            }
            else
                return ILIntepreter.PushObject(ret, mStack, Enum.GetNames(t), true);
        }

        public static StackObject* EnumGetName(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var ret = esp - 2;
            AppDomain domain = intp.AppDomain;

            var p = esp - 1;
            object val = StackObject.ToObject(p, domain, mStack);
            intp.Free(p);

            p = esp - 2;
            Type t = (Type)StackObject.ToObject(p, domain, mStack);
            intp.Free(p);

            if (t is ILRuntimeType)
            {
                ILType it = ((ILRuntimeType)t).ILType;

                List<string> res = new List<string>();
                if (it.IsEnum)
                {
                    if (val is ILEnumTypeInstance)
                    {
                        ILEnumTypeInstance ins = (ILEnumTypeInstance)val;
                        return ILIntepreter.PushObject(ret, mStack, ins.ToString(), true);
                    }
                    else if (val.GetType().IsPrimitive)
                    {
                        ILEnumTypeInstance ins = new ILEnumTypeInstance(it);
                        ins[0] = val;
                        return ILIntepreter.PushObject(ret, mStack, ins.ToString(), true);
                    }
                    else
                        throw new NotImplementedException();
                }
                else
                    throw new Exception(string.Format("{0} is not Enum", t.FullName));
            }
            else if (t is ILRuntimeWrapperType)
            {
                return ILIntepreter.PushObject(ret, mStack, Enum.GetName(((ILRuntimeWrapperType)t).RealType, val), true);
            }
            else
                return ILIntepreter.PushObject(ret, mStack, Enum.GetName(t, val), true);
        }

        public static StackObject* EnumToObject(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var ret = esp - 2;
            AppDomain domain = intp.AppDomain;

            var p = esp - 1;
            int val = p->Value;
            intp.Free(p);

            p = esp - 2;
            Type t = (Type)StackObject.ToObject(p, domain, mStack);
            intp.Free(p);

            if (t is ILRuntimeType)
            {
                ILType it = ((ILRuntimeType)t).ILType;

                List<string> res = new List<string>();
                if (it.IsEnum)
                {
                    ILEnumTypeInstance ins = new ILEnumTypeInstance(it);
                    ins[0] = val;
                    return ILIntepreter.PushObject(ret, mStack, ins, true);
                }
                else
                    throw new Exception(string.Format("{0} is not Enum", t.FullName));
            }
            else if (t is ILRuntimeWrapperType)
            {
                return ILIntepreter.PushObject(ret, mStack, Enum.GetName(((ILRuntimeWrapperType)t).RealType, val), true);
            }
            else
                return ILIntepreter.PushObject(ret, mStack, Enum.GetName(t, val), true);
        }
#if NET_4_6 || NET_STANDARD_2_0
        public static StackObject* EnumHasFlag(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var ret = esp - 2;
            AppDomain domain = intp.AppDomain;

            var p = esp - 1;
            object val = StackObject.ToObject(p, domain, mStack);
            intp.Free(p);

            p = esp - 2;
            object ins = StackObject.ToObject(p, domain, mStack);
            intp.Free(p);

            bool res = false;
            if (ins is ILEnumTypeInstance)
            {
                ILEnumTypeInstance enumIns = (ILEnumTypeInstance)ins;
                int num = enumIns.Fields[0].Value;
                int valNum = ((ILEnumTypeInstance)val).Fields[0].Value;
                res = (num & valNum) == valNum;
            }
            else
            {
                res = ((Enum)ins).HasFlag((Enum)val);
            }
            if (res)
                return ILIntepreter.PushOne(ret);
            else
                return ILIntepreter.PushZero(ret);
        }

        public static StackObject* EnumCompareTo(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var ret = esp - 2;
            AppDomain domain = intp.AppDomain;

            var p = esp - 1;
            object val = StackObject.ToObject(p, domain, mStack);
            intp.Free(p);

            p = esp - 2;
            object ins = StackObject.ToObject(p, domain, mStack);
            intp.Free(p);

            int res = 0;
            if (ins is ILEnumTypeInstance)
            {
                ILEnumTypeInstance enumIns = (ILEnumTypeInstance)ins;
                int num = enumIns.Fields[0].Value;
                int valNum = ((ILEnumTypeInstance)val).Fields[0].Value;
                res = (num - valNum);
            }
            else
            {
                res = ((Enum)ins).CompareTo(val);
            }

            ret->ObjectType = ObjectTypes.Integer;
            ret->Value = res;
            return ret + 1;
        }
#endif

        public static StackObject* DelegateCreateDelegate(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var ret = esp - 2;
            AppDomain domain = intp.AppDomain;

            var p = esp - 1;
            MethodInfo mi = (MethodInfo)StackObject.ToObject(p, domain, mStack);
            intp.Free(p);

            p = esp - 2;
            Type t = (Type)StackObject.ToObject(p, domain, mStack);
            intp.Free(p);

            if (t is ILRuntimeType)
            {
                ILType it = ((ILRuntimeType)t).ILType;
                if (it.IsDelegate)
                {
                    var m = it.GetMethod("Invoke") as ILMethod;
                    object ins = null;
                    if (mi is ILRuntimeMethodInfo)
                    {
                        ILRuntimeMethodInfo imi = (ILRuntimeMethodInfo)mi;
                        var ilMethod = imi.ILMethod;
                        if (ilMethod.DelegateAdapter == null)
                        {
                            ilMethod.DelegateAdapter = domain.DelegateManager.FindDelegateAdapter(null, ilMethod, m);
                        }
                        ins = ilMethod.DelegateAdapter;
                    }
                    else
                    {
                        throw new NotSupportedException();
                    }
                    return ILIntepreter.PushObject(ret, mStack, ins, true);
                }
                else
                    throw new NotSupportedException(string.Format("{0} is not Delegate", t.FullName));
            }
            else if (t is ILRuntimeWrapperType)
            {
                ILRuntimeWrapperType iwt = (ILRuntimeWrapperType)t;
                object ins = null;
                if (mi is ILRuntimeMethodInfo)
                {
                    ILRuntimeMethodInfo imi = (ILRuntimeMethodInfo)mi;

                    ins = domain.DelegateManager.FindDelegateAdapter(iwt.CLRType, null, imi.ILMethod);
                }
                else
                {
                    ins = Delegate.CreateDelegate(iwt.RealType, mi);
                }
                return ILIntepreter.PushObject(ret, mStack, ins, true);
            }
            else
                return ILIntepreter.PushObject(ret, mStack, Delegate.CreateDelegate(t, mi), true);
        }

        public static StackObject* DelegateCreateDelegate2(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var ret = esp - 3;
            AppDomain domain = intp.AppDomain;

            var p = esp - 1;
            string name = (string)StackObject.ToObject(p, domain, mStack);
            intp.Free(p);

            p = esp - 2;
            object obj = (object)StackObject.ToObject(p, domain, mStack);
            intp.Free(p);

            p = esp - 3;
            Type t = (Type)StackObject.ToObject(p, domain, mStack);
            intp.Free(p);

            if (obj == null)
                throw new ArgumentNullException("Argument target cannot be null");

            if (t is ILRuntimeType)
            {
                ILType it = ((ILRuntimeType)t).ILType;
                if (it.IsDelegate)
                {
                    var m = it.GetMethod("Invoke") as ILMethod;
                    if (obj is ILTypeInstance)
                    {
                        ILTypeInstance ii = (ILTypeInstance)obj;
                        var ilMethod = ii.Type.GetMethod(name) as ILMethod;
                        if (ilMethod == null)
                            throw new ArgumentException(string.Format("Cannot find method \"{0}\" in type {1}", name, it.FullName));
                        if (ilMethod.DelegateAdapter == null)
                        {
                            ilMethod.DelegateAdapter = domain.DelegateManager.FindDelegateAdapter(ii, ilMethod, m);
                        }
                        object ins = ilMethod.DelegateAdapter;
                        return ILIntepreter.PushObject(ret, mStack, ins, true);
                    }
                    else
                        throw new NotSupportedException();

                }
                else
                    throw new NotSupportedException(string.Format("{0} is not Delegate", t.FullName));
            }
            else if (t is ILRuntimeWrapperType)
            {
                ILRuntimeWrapperType iwt = (ILRuntimeWrapperType)t;
                object ins = null;
                if (obj is ILTypeInstance)
                {
                    ILTypeInstance ii = (ILTypeInstance)obj;
                    var ilMethod = ii.Type.GetMethod(name) as ILMethod;
                    if (ilMethod == null)
                        throw new ArgumentException(string.Format("Cannot find method \"{0}\" in type {1}", name, ii.Type.FullName));
                    ins = domain.DelegateManager.FindDelegateAdapter(iwt.CLRType, ii, ilMethod);
                }
                else
                {
                    ins = Delegate.CreateDelegate(iwt.RealType, obj, name);
                }
                return ILIntepreter.PushObject(ret, mStack, ins, true);
            }
            else
                return ILIntepreter.PushObject(ret, mStack, Delegate.CreateDelegate(t, obj, name), true);
        }
        public static StackObject* DelegateCreateDelegate3(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var ret = esp - 3;
            AppDomain domain = intp.AppDomain;

            var p = esp - 1;
            MethodInfo mi = (MethodInfo)StackObject.ToObject(p, domain, mStack);
            intp.Free(p);

            p = esp - 2;
            object obj = (object)StackObject.ToObject(p, domain, mStack);
            intp.Free(p);

            p = esp - 3;
            Type t = (Type)StackObject.ToObject(p, domain, mStack);
            intp.Free(p);

            if (t is ILRuntimeType)
            {
                ILType it = ((ILRuntimeType)t).ILType;
                if (it.IsDelegate)
                {
                    object ins = null;
                    if (mi is ILRuntimeMethodInfo)
                    {
                        ILRuntimeMethodInfo imi = (ILRuntimeMethodInfo)mi;
                        var ilMethod = imi.ILMethod;
                        if (obj != null)
                        {
                            ins = ((ILTypeInstance)obj).GetDelegateAdapter(ilMethod);
                            if (ins == null)
                            {
                                var invokeMethod = it.GetMethod("Invoke", ilMethod.ParameterCount);
                                if (invokeMethod == null && ilMethod.IsExtend)
                                {
                                    invokeMethod = it.GetMethod("Invoke", ilMethod.ParameterCount - 1);
                                }
                                ins = domain.DelegateManager.FindDelegateAdapter(
                                    (ILTypeInstance)obj, ilMethod, invokeMethod);
                            }
                        }
                        else
                        {
                            if (ilMethod.DelegateAdapter == null)
                            {
                                var m = it.GetMethod("Invoke") as ILMethod;
                                ilMethod.DelegateAdapter = domain.DelegateManager.FindDelegateAdapter(null, ilMethod, m);
                            }
                            ins = ilMethod.DelegateAdapter;
                        }
                    }
                    else
                    {
                        throw new NotSupportedException();
                    }
                    return ILIntepreter.PushObject(ret, mStack, ins, true);
                }
                else
                    throw new NotSupportedException(string.Format("{0} is not Delegate", t.FullName));
            }
            else if (t is ILRuntimeWrapperType)
            {
                ILRuntimeWrapperType iwt = (ILRuntimeWrapperType)t;
                object ins = null;
                if (mi is ILRuntimeMethodInfo)
                {
                    ILRuntimeMethodInfo imi = (ILRuntimeMethodInfo)mi;

                    ins = domain.DelegateManager.FindDelegateAdapter(iwt.CLRType, obj as ILTypeInstance, imi.ILMethod);
                }
                else
                {
                    ins = Delegate.CreateDelegate(iwt.RealType, obj, mi);
                }
                return ILIntepreter.PushObject(ret, mStack, ins, true);
            }
            else
                return ILIntepreter.PushObject(ret, mStack, Delegate.CreateDelegate(t, obj, mi), true);
        }

        public static StackObject* TypeMakeGenericType(ILIntepreter intp, StackObject* esp, AutoList mStack, CLRMethod method, bool isNewObj)
        {
            var ret = esp - 2;
            AppDomain domain = intp.AppDomain;

            var p = esp - 1;
            Type[] param = (Type[])StackObject.ToObject(p, domain, mStack);
            intp.Free(p);

            p = esp - 2;
            Type type = (Type)StackObject.ToObject(p, domain, mStack);
            IType t = ToIType(type, domain);
            intp.Free(p);

            if (!t.HasGenericParameter)
                throw new NotSupportedException(string.Format("{0} is not a generic type", t.FullName));
            ILType iltype = t as ILType;
            if(iltype != null)
            {
                KeyValuePair<string, IType>[] arr = new KeyValuePair<string, IType>[iltype.TypeDefinition.GenericParameters.Count];
                for(int i = 0; i < arr.Length; i++)
                {
                    arr[i] = new KeyValuePair<string, IType>(iltype.TypeDefinition.GenericParameters[i].Name, ToIType(param[i], domain));
                }
                return ILIntepreter.PushObject(ret, mStack, t.MakeGenericInstance(arr).ReflectionType, true);
            }
            CLRType clrType = t as CLRType;
            if(clrType != null)
            {
                KeyValuePair<string, IType>[] arr = new KeyValuePair<string, IType>[param.Length];
                for (int i = 0; i < arr.Length; i++)
                {
                    arr[i] = new KeyValuePair<string, IType>("!", ToIType(param[i], domain));
                }
                return ILIntepreter.PushObject(ret, mStack, t.MakeGenericInstance(arr).ReflectionType, true);
            }
            throw new NotImplementedException();
        }

        static IType ToIType(Type type, AppDomain domain)
        {
            if (type is ILRuntimeType)
            {
                return ((ILRuntimeType)type).ILType;
            }
            else if (type is ILRuntimeWrapperType)
            {
                return ((ILRuntimeWrapperType)type).CLRType;
            }
            else
                return domain.GetType(type);
        }

        // Neo redirect for System.Type.MakeGenericType(Type[]). Mirrors the Legacy
        // CLRRedirections.TypeMakeGenericType (CLRRedirections.cs:2070). Under Neo the
        // autogen MakeGenericType_4_Neo stub (System_Type_Binding.cs) calls the framework
        // `instance.MakeGenericType(typeArgs)` directly. When the `this` or a type arg is
        // an ILRuntimeType (an IL open-generic type, or an IL method's parameter type
        // reached via reflection -- e.g. DelegateTest36's
        // typeof(Action<>).MakeGenericType(parameterInfos[0].ParameterType) where the
        // parameter type is an IL type), the base System.Type.MakeGenericType throws
        // NotImplementedException "Derived classes must provide an implementation" (the
        // runtime-Type override is missing on ILRuntimeType). The Legacy redirect routes
        // through ToIType + MakeGenericInstance (the ILRuntime-internal generic
        // construction, bypassing the framework call entirely) -- byte-identical logic is
        // mirrored here. Registered on RedirectMapNeo in the AppDomain ctor
        // (first-registered-wins, runs before the test-harness autogen
        // System_Type_Binding.Register), preempting the broken stub (the recurring
        // child-22/6/12 defect class -- same shape as EnumToObjectNeo / CreateInstanceNeo
        // / InitializeArrayNeo).
#if ENABLE_NEO_MODE
        public unsafe static void TypeMakeGenericTypeNeo(ILIntepreter intp, byte* frameBase, AutoList mStack, CLRMethod method, bool isNewObj, byte* retDst, int retRefBase)
        {
            AppDomain domain = intp.AppDomain;
            int curPrim = 0;
            Type type = (Type)ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            Type[] param = (Type[])ILIntepreter.ReadNeoReference(frameBase, ref curPrim, mStack);
            IType t = ToIType(type, domain);
            if (!t.HasGenericParameter)
                throw new NotSupportedException(string.Format("{0} is not a generic type", t.FullName));
            object result;
            ILType iltype = t as ILType;
            if (iltype != null)
            {
                KeyValuePair<string, IType>[] arr = new KeyValuePair<string, IType>[iltype.TypeDefinition.GenericParameters.Count];
                for (int i = 0; i < arr.Length; i++)
                    arr[i] = new KeyValuePair<string, IType>(iltype.TypeDefinition.GenericParameters[i].Name, ToIType(param[i], domain));
                result = t.MakeGenericInstance(arr).ReflectionType;
            }
            else
            {
                CLRType clrType = t as CLRType;
                if (clrType != null)
                {
                    KeyValuePair<string, IType>[] arr = new KeyValuePair<string, IType>[param.Length];
                    for (int i = 0; i < arr.Length; i++)
                        arr[i] = new KeyValuePair<string, IType>("!", ToIType(param[i], domain));
                    result = t.MakeGenericInstance(arr).ReflectionType;
                }
                else
                    throw new NotImplementedException();
            }
            WriteNeoObjectResult(mStack, retDst, retRefBase, result);
        }
#endif
    }
}
