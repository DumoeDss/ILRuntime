using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

using ILRuntime.CLR.TypeSystem;
using ILRuntime.CLR.Method;
using ILRuntime.Runtime;
using ILRuntime.Runtime.Enviorment;
using ILRuntime.Runtime.Intepreter;
using ILRuntime.Runtime.Stack;
using ILRuntime.Reflection;
using ILRuntime.CLR.Utils;
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
using AutoList = System.Collections.Generic.List<object>;
#else
using AutoList = ILRuntime.Other.UncheckedList<object>;
#endif
namespace ILRuntime.Runtime.Generated
{
    [ILRuntimePatchIgnore]
    unsafe class System_Enum_Binding
    {
        public static void Register(ILRuntime.Runtime.Enviorment.AppDomain app)
        {
            BindingFlags flag = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            MethodBase method;
            Type[] args;
            Type type = typeof(System.Enum);
            args = new Type[]{typeof(System.Type)};
            method = type.GetMethod("GetValues", flag, null, args, null);
#if ENABLE_NEO_MODE
            app.RegisterCLRMethodRedirectionNeo(method, GetValues_0_Neo);
#else
            app.RegisterCLRMethodRedirection(method, GetValues_0);
#endif
            args = new Type[]{typeof(System.Type)};
            method = type.GetMethod("GetNames", flag, null, args, null);
#if ENABLE_NEO_MODE
            app.RegisterCLRMethodRedirectionNeo(method, GetNames_1_Neo);
#else
            app.RegisterCLRMethodRedirection(method, GetNames_1);
#endif
            args = new Type[]{typeof(System.Enum)};
            method = type.GetMethod("HasFlag", flag, null, args, null);
#if ENABLE_NEO_MODE
            app.RegisterCLRMethodRedirectionNeo(method, HasFlag_2_Neo);
#else
            app.RegisterCLRMethodRedirection(method, HasFlag_2);
#endif
            args = new Type[]{typeof(System.Type), typeof(System.Int32)};
            method = type.GetMethod("ToObject", flag, null, args, null);
#if ENABLE_NEO_MODE
            app.RegisterCLRMethodRedirectionNeo(method, ToObject_3_Neo);
#else
            app.RegisterCLRMethodRedirection(method, ToObject_3);
#endif
            args = new Type[]{typeof(System.Object)};
            method = type.GetMethod("CompareTo", flag, null, args, null);
#if ENABLE_NEO_MODE
            app.RegisterCLRMethodRedirectionNeo(method, CompareTo_4_Neo);
#else
            app.RegisterCLRMethodRedirection(method, CompareTo_4);
#endif


        }


#if ENABLE_NEO_MODE
        static void GetValues_0_Neo(ILIntepreter __intp, byte* __frameBase, AutoList __mStack, CLRMethod __method, bool isNewObj, byte* __retDst, int __retRefBase)
        {
            ILRuntime.Runtime.Enviorment.AppDomain __domain = __intp.AppDomain;
            int __curPrim = 0;
            System.Type @enumType = (System.Type)ILIntepreter.ReadNeoReference(__frameBase, ref __curPrim, __mStack);
            var result_of_this_method = System.Enum.GetValues(@enumType);
            if (__retDst != null)
            {
                if (__retRefBase >= __mStack.Count)
                    __mStack.Add(result_of_this_method);
                else
                    __mStack[__retRefBase] = result_of_this_method;
                *(int*)__retDst = __retRefBase;
            }
        }
#else
        static StackObject* GetValues_0(ILIntepreter __intp, StackObject* __esp, AutoList __mStack, CLRMethod __method, bool isNewObj)
        {
            ILRuntime.Runtime.Enviorment.AppDomain __domain = __intp.AppDomain;
            StackObject* ptr_of_this_method;
            StackObject* __ret = __esp - 1;

            ptr_of_this_method = __esp - 1;
            System.Type @enumType = (System.Type)typeof(System.Type).CheckCLRTypes(StackObject.ToObject(ptr_of_this_method, __domain, __mStack), (ILRuntime.CLR.Utils.Extensions.TypeFlags)0);
            __intp.Free(ptr_of_this_method);


            var result_of_this_method = System.Enum.GetValues(@enumType);

            return ILIntepreter.PushObject(__ret, __mStack, result_of_this_method);
        }
#endif

#if ENABLE_NEO_MODE
        static void GetNames_1_Neo(ILIntepreter __intp, byte* __frameBase, AutoList __mStack, CLRMethod __method, bool isNewObj, byte* __retDst, int __retRefBase)
        {
            ILRuntime.Runtime.Enviorment.AppDomain __domain = __intp.AppDomain;
            int __curPrim = 0;
            System.Type @enumType = (System.Type)ILIntepreter.ReadNeoReference(__frameBase, ref __curPrim, __mStack);
            var result_of_this_method = System.Enum.GetNames(@enumType);
            if (__retDst != null)
            {
                if (__retRefBase >= __mStack.Count)
                    __mStack.Add(result_of_this_method);
                else
                    __mStack[__retRefBase] = result_of_this_method;
                *(int*)__retDst = __retRefBase;
            }
        }
#else
        static StackObject* GetNames_1(ILIntepreter __intp, StackObject* __esp, AutoList __mStack, CLRMethod __method, bool isNewObj)
        {
            ILRuntime.Runtime.Enviorment.AppDomain __domain = __intp.AppDomain;
            StackObject* ptr_of_this_method;
            StackObject* __ret = __esp - 1;

            ptr_of_this_method = __esp - 1;
            System.Type @enumType = (System.Type)typeof(System.Type).CheckCLRTypes(StackObject.ToObject(ptr_of_this_method, __domain, __mStack), (ILRuntime.CLR.Utils.Extensions.TypeFlags)0);
            __intp.Free(ptr_of_this_method);


            var result_of_this_method = System.Enum.GetNames(@enumType);

            return ILIntepreter.PushObject(__ret, __mStack, result_of_this_method);
        }
#endif

#if ENABLE_NEO_MODE
        // C3 (neo-enum-cluster-residual): read an IL enum's underlying value out of
        // its boxed ILTypeInstance's Primitives byte[] as a sign-extended long. An IL
        // enum boxed value is an ILTypeInstance (the ILEnumTypeInstance derived class,
        // internal to ILRuntime) carrying the underlying value in Primitives -- it is
        // NOT a real System.Enum, so the autogen `(System.Enum)ReadNeoReference` cast
        // threw InvalidCastException on HasFlag (EnumTest Test20) and CompareTo
        // (EnumTest Test22). The stubs now detect an IL enum (ILTypeInstance with
        // Type.IsEnum) and compute the result directly on this value. Sign-extension
        // is correct for signed underlying types (sbyte/short/int/long) and for
        // unsigned types whose value fits in the positive signed range (the common
        // case); HasFlag uses only the low bits so it is unaffected.
        static long NeoEnumRawLong(byte[] p)
        {
            if (p == null || p.Length == 0)
                return 0;
            switch (p.Length)
            {
                case 1: return (sbyte)p[0];
                case 2: return (short)(p[0] | (p[1] << 8));
                case 4: return (int)((uint)p[0] | ((uint)p[1] << 8) | ((uint)p[2] << 16) | ((uint)p[3] << 24));
                default:
                    {
                        ulong u = 0;
                        int n = p.Length < 8 ? p.Length : 8;
                        for (int i = 0; i < n; i++)
                            u |= (ulong)p[i] << (i * 8);
                        return (long)u;
                    }
            }
        }

        static void HasFlag_2_Neo(ILIntepreter __intp, byte* __frameBase, AutoList __mStack, CLRMethod __method, bool isNewObj, byte* __retDst, int __retRefBase)
        {
            ILRuntime.Runtime.Enviorment.AppDomain __domain = __intp.AppDomain;
            int __curPrim = 0;
            object instanceRaw = ILIntepreter.ReadNeoReference(__frameBase, ref __curPrim, __mStack);
            object flagRaw = ILIntepreter.ReadNeoReference(__frameBase, ref __curPrim, __mStack);
            bool result_of_this_method;
            if (instanceRaw is ILTypeInstance enumThis && enumThis.Type != null && enumThis.Type.IsEnum
                && flagRaw is ILTypeInstance enumFlag && enumFlag.Type != null && enumFlag.Type.IsEnum)
            {
                if (enumThis.Type != enumFlag.Type)
                    result_of_this_method = false;
                else
                {
                    long a = NeoEnumRawLong(enumThis.Primitives), b = NeoEnumRawLong(enumFlag.Primitives);
                    result_of_this_method = (a & b) == b;
                }
            }
            else
                result_of_this_method = ((System.Enum)instanceRaw).HasFlag((System.Enum)flagRaw);
            if (__retDst != null) *(int*)__retDst = result_of_this_method ? 1 : 0;
        }
#else
        static StackObject* HasFlag_2(ILIntepreter __intp, StackObject* __esp, AutoList __mStack, CLRMethod __method, bool isNewObj)
        {
            ILRuntime.Runtime.Enviorment.AppDomain __domain = __intp.AppDomain;
            StackObject* ptr_of_this_method;
            StackObject* __ret = __esp - 2;

            ptr_of_this_method = __esp - 1;
            System.Enum @flag = (System.Enum)typeof(System.Enum).CheckCLRTypes(StackObject.ToObject(ptr_of_this_method, __domain, __mStack), (ILRuntime.CLR.Utils.Extensions.TypeFlags)0);
            __intp.Free(ptr_of_this_method);

            ptr_of_this_method = __esp - 2;
            System.Enum instance_of_this_method = (System.Enum)typeof(System.Enum).CheckCLRTypes(StackObject.ToObject(ptr_of_this_method, __domain, __mStack), (ILRuntime.CLR.Utils.Extensions.TypeFlags)0);
            __intp.Free(ptr_of_this_method);

            var result_of_this_method = instance_of_this_method.HasFlag(@flag);

            __ret->ObjectType = ObjectTypes.Integer;
            __ret->Value = result_of_this_method ? 1 : 0;
            return __ret + 1;
        }
#endif

#if ENABLE_NEO_MODE
        static void ToObject_3_Neo(ILIntepreter __intp, byte* __frameBase, AutoList __mStack, CLRMethod __method, bool isNewObj, byte* __retDst, int __retRefBase)
        {
            ILRuntime.Runtime.Enviorment.AppDomain __domain = __intp.AppDomain;
            int __curPrim = 0;
            System.Type @enumType = (System.Type)ILIntepreter.ReadNeoReference(__frameBase, ref __curPrim, __mStack);
            System.Int32 @value = (System.Int32)ILIntepreter.ReadNeoInt32(__frameBase, ref __curPrim);
            var result_of_this_method = System.Enum.ToObject(@enumType, @value);
            if (__retDst != null)
            {
                if (__retRefBase >= __mStack.Count)
                    __mStack.Add(result_of_this_method);
                else
                    __mStack[__retRefBase] = result_of_this_method;
                *(int*)__retDst = __retRefBase;
            }
        }
#else
        static StackObject* ToObject_3(ILIntepreter __intp, StackObject* __esp, AutoList __mStack, CLRMethod __method, bool isNewObj)
        {
            ILRuntime.Runtime.Enviorment.AppDomain __domain = __intp.AppDomain;
            StackObject* ptr_of_this_method;
            StackObject* __ret = __esp - 2;

            ptr_of_this_method = __esp - 1;
            System.Int32 @value = ptr_of_this_method->Value;

            ptr_of_this_method = __esp - 2;
            System.Type @enumType = (System.Type)typeof(System.Type).CheckCLRTypes(StackObject.ToObject(ptr_of_this_method, __domain, __mStack), (ILRuntime.CLR.Utils.Extensions.TypeFlags)0);
            __intp.Free(ptr_of_this_method);


            var result_of_this_method = System.Enum.ToObject(@enumType, @value);

            object obj_result_of_this_method = result_of_this_method;
            if(obj_result_of_this_method is CrossBindingAdaptorType)
            {    
                return ILIntepreter.PushObject(__ret, __mStack, ((CrossBindingAdaptorType)obj_result_of_this_method).ILInstance, true);
            }
            return ILIntepreter.PushObject(__ret, __mStack, result_of_this_method, true);
        }
#endif

#if ENABLE_NEO_MODE
        static void CompareTo_4_Neo(ILIntepreter __intp, byte* __frameBase, AutoList __mStack, CLRMethod __method, bool isNewObj, byte* __retDst, int __retRefBase)
        {
            ILRuntime.Runtime.Enviorment.AppDomain __domain = __intp.AppDomain;
            int __curPrim = 0;
            object instanceRaw = ILIntepreter.ReadNeoReference(__frameBase, ref __curPrim, __mStack);
            object targetRaw = ILIntepreter.ReadNeoReference(__frameBase, ref __curPrim, __mStack);
            int result_of_this_method;
            if (instanceRaw is ILTypeInstance enumThis && enumThis.Type != null && enumThis.Type.IsEnum
                && targetRaw is ILTypeInstance enumTarget && enumTarget.Type != null && enumTarget.Type.IsEnum)
            {
                // C3: an IL enum boxed value is not a System.Enum -> autogen cast threw.
                // Compare underlying values directly (same-type enforced, mirroring
                // System.Enum.CompareTo which throws on a type mismatch).
                if (enumThis.Type != enumTarget.Type)
                    throw new System.ArgumentException("Object must be of the same Enum type as this instance.");
                long a = NeoEnumRawLong(enumThis.Primitives), b = NeoEnumRawLong(enumTarget.Primitives);
                result_of_this_method = a.CompareTo(b);
            }
            else
                result_of_this_method = ((System.Enum)instanceRaw).CompareTo(targetRaw);
            if (__retDst != null) *(int*)__retDst = result_of_this_method;
        }
#else
        static StackObject* CompareTo_4(ILIntepreter __intp, StackObject* __esp, AutoList __mStack, CLRMethod __method, bool isNewObj)
        {
            ILRuntime.Runtime.Enviorment.AppDomain __domain = __intp.AppDomain;
            StackObject* ptr_of_this_method;
            StackObject* __ret = __esp - 2;

            ptr_of_this_method = __esp - 1;
            System.Object @target = (System.Object)typeof(System.Object).CheckCLRTypes(StackObject.ToObject(ptr_of_this_method, __domain, __mStack), (ILRuntime.CLR.Utils.Extensions.TypeFlags)0);
            __intp.Free(ptr_of_this_method);

            ptr_of_this_method = __esp - 2;
            System.Enum instance_of_this_method = (System.Enum)typeof(System.Enum).CheckCLRTypes(StackObject.ToObject(ptr_of_this_method, __domain, __mStack), (ILRuntime.CLR.Utils.Extensions.TypeFlags)0);
            __intp.Free(ptr_of_this_method);

            var result_of_this_method = instance_of_this_method.CompareTo(@target);

            __ret->ObjectType = ObjectTypes.Integer;
            __ret->Value = result_of_this_method;
            return __ret + 1;
        }
#endif



    }
}
