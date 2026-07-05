#if ENABLE_NEO_MODE
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using ILRuntime.CLR.TypeSystem;
using ILRuntime.CLR.Method;
using ILRuntime.Runtime.Stack;
using ILRuntime.Runtime.Intepreter.OpCodes;
using ILRuntime.Runtime.Enviorment;
using ILRuntime.CLR.Utils;
using ILRuntime.Runtime.Intepreter.RegisterVM;

#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
using AutoList = System.Collections.Generic.List<object>;
#else
using AutoList = ILRuntime.Other.UncheckedList<object>;
#endif
namespace ILRuntime.Runtime.Intepreter
{
    public unsafe partial class ILIntepreter
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadNeoInt32(byte* frameBase, ref int curPrim)
        {
            int res = *(int*)(frameBase + curPrim);
            curPrim += 4;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadNeoUInt32(byte* frameBase, ref int curPrim)
        {
            uint res = *(uint*)(frameBase + curPrim);
            curPrim += 4;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static short ReadNeoInt16(byte* frameBase, ref int curPrim)
        {
            short res = *(short*)(frameBase + curPrim);
            curPrim += 2;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort ReadNeoUInt16(byte* frameBase, ref int curPrim)
        {
            ushort res = *(ushort*)(frameBase + curPrim);
            curPrim += 2;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte ReadNeoUInt8(byte* frameBase, ref int curPrim)
        {
            byte res = *(byte*)(frameBase + curPrim);
            curPrim += 1;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static sbyte ReadNeoInt8(byte* frameBase, ref int curPrim)
        {
            sbyte res = *(sbyte*)(frameBase + curPrim);
            curPrim += 1;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ReadNeoBoolean(byte* frameBase, ref int curPrim)
        {
            bool res = *(byte*)(frameBase + curPrim) != 0;
            curPrim += 1;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ReadNeoInt64(byte* frameBase, ref int curPrim)
        {
            long res = *(long*)(frameBase + curPrim);
            curPrim += 8;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReadNeoUInt64(byte* frameBase, ref int curPrim)
        {
            ulong res = *(ulong*)(frameBase + curPrim);
            curPrim += 8;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ReadNeoFloat(byte* frameBase, ref int curPrim)
        {
            float res = *(float*)(frameBase + curPrim);
            curPrim += 4;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double ReadNeoDouble(byte* frameBase, ref int curPrim)
        {
            double res = *(double*)(frameBase + curPrim);
            curPrim += 8;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static char ReadNeoChar(byte* frameBase, ref int curPrim)
        {
            char res = (char)*(int*)(frameBase + curPrim);
            curPrim += 4;
            return res;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static object ReadNeoReference(byte* frameBase, ref int curPrim, AutoList mStack)
        {
            int idx = *(int*)(frameBase + curPrim);
            curPrim += 4;
            return mStack[idx];
        }

        // ---- Step 13b (D4): CLR value-type read/write helpers ----
        // These are the by-type generalization of the ReadNeo* primitive family
        // for a CLR struct param/return slot, which is stored as FLAT BYTES in
        // the callee param region (sized by the managed value-type byte size --
        // see Optimizer.GetNeoValueTypeManagedSize). Both CLR-param readers --
        // the reflection fallback CLRMethod.Invoke(byte*) and the autogen
        // AppendArgumentCodeNeo / GetReturnValueCodeNeo delegate body -- call
        // THESE so the reader layout and the callee layout stay byte-consistent
        // BY CONSTRUCTION (single code path, single size source). For a struct
        // WITH ref fields and NO registered ValueTypeBinder there is no way to
        // map the GC references, so the caller checks for that case and throws a
        // clearly-tagged Step-13b NIE before reaching here; these helpers assume
        // a blittable-or-binder-mapped struct.
        //
        // The typed read/write is emitted once per Type via DynamicMethod (IL
        // calling Unsafe.ReadUnaligned<T>/WriteUnaligned<T>, box/unbox) and
        // cached, so the hot path is a delegate invoke with no reflection. The
        // size used to advance the cursor is Optimizer.GetNeoValueTypeManagedSize.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, NeoVtReaderDelegate> s_neoVtReaders
            = new System.Collections.Concurrent.ConcurrentDictionary<Type, NeoVtReaderDelegate>();
        static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, NeoVtWriterDelegate> s_neoVtWriters
            = new System.Collections.Concurrent.ConcurrentDictionary<Type, NeoVtWriterDelegate>();

        // Custom delegate types: pointer types cannot be generic type arguments
        // (CS0306), so Func<byte*,object>/Action<byte*,object> are illegal. These
        // custom delegates accept the frame byte pointer directly.
        unsafe delegate object NeoVtReaderDelegate(byte* src);
        unsafe delegate void NeoVtWriterDelegate(byte* dst, object value);

        static NeoVtReaderDelegate CreateNeoVtReader(Type t)
        {
            // T ReadUnaligned<T>(void* source) -- the native-pointer overload
            // (matches the byte* arg; the ref-byte overload would need a managed
            // pointer and fail IL verification here).
            MethodInfo readOpen = null;
            foreach (var m in typeof(Unsafe).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var ps = m.GetParameters();
                if (m.Name == "ReadUnaligned" && m.IsGenericMethod && ps.Length == 1
                    && ps[0].ParameterType == typeof(void*))
                { readOpen = m; break; }
            }
            var readMi = readOpen.MakeGenericMethod(t);
            var dm = new System.Reflection.Emit.DynamicMethod("NeoVtReader_" + t.FullName, typeof(object), new[] { typeof(byte*) }, restrictedSkipVisibility: true);
            var il = dm.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.EmitCall(System.Reflection.Emit.OpCodes.Call, readMi, null);
            il.Emit(System.Reflection.Emit.OpCodes.Box, t);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            return (NeoVtReaderDelegate)dm.CreateDelegate(typeof(NeoVtReaderDelegate));
        }

        static NeoVtWriterDelegate CreateNeoVtWriter(Type t)
        {
            // void WriteUnaligned<T>(void* destination, T value) -- native-pointer.
            MethodInfo writeOpen = null;
            foreach (var m in typeof(Unsafe).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var ps = m.GetParameters();
                if (m.Name == "WriteUnaligned" && m.IsGenericMethod && ps.Length == 2
                    && ps[0].ParameterType == typeof(void*))
                { writeOpen = m; break; }
            }
            var writeMi = writeOpen.MakeGenericMethod(t);
            var dm = new System.Reflection.Emit.DynamicMethod("NeoVtWriter_" + t.FullName, null, new[] { typeof(byte*), typeof(object) }, restrictedSkipVisibility: true);
            var il = dm.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
            il.Emit(System.Reflection.Emit.OpCodes.Unbox_Any, t);
            il.EmitCall(System.Reflection.Emit.OpCodes.Call, writeMi, null);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            return (NeoVtWriterDelegate)dm.CreateDelegate(typeof(NeoVtWriterDelegate));
        }

        // Read sz managed bytes at frameBase+curPrim into a BOXED object of the
        // given CLR Type, advancing curPrim by sz (sz MUST equal
        // Optimizer.GetNeoValueTypeManagedSize(clr) for the param/return slot).
        // public: the autogen CLR binding redirect delegate body calls this so the
        // generated reader and the reflection reader share one code path.
        public static unsafe object ReadNeoValueType(Type clr, byte* frameBase, ref int curPrim, int sz)
        {
            if (clr == null || sz <= 0)
            {
                curPrim += sz;
                return null;
            }
            var reader = s_neoVtReaders.GetOrAdd(clr, CreateNeoVtReader);
            object result = reader(frameBase + curPrim);
            curPrim += sz;
            return result;
        }

        // Write a boxed CLR struct's managed bytes into dst (sz bytes). The
        // inverse of ReadNeoValueType, used by the return-value path. public: the
        // autogen GetReturnValueCodeNeo delegate body calls this.
        public static unsafe void WriteNeoValueType(object value, byte* dst, int sz)
        {
            if (value == null || dst == null || sz <= 0)
                return;
            Type clr = value.GetType();
            var writer = s_neoVtWriters.GetOrAdd(clr, CreateNeoVtWriter);
            writer(dst, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void CopyNeoCallArguments(ref NeoCallParamMap map, byte* frameBase, byte* targetBase)
        {
            if (map.PrimitiveSize == null)
                return;

            for (int i = 0; i < map.PrimitiveSize.Length; i++)
            {
                Unsafe.CopyBlock(targetBase + map.PrimitiveDst[i], frameBase + map.PrimitiveSrc[i], map.PrimitiveSize[i]);
            }
        }

        bool InvokeNeoCallTarget(IMethod targetMethod, bool isNewobj, byte* targetBase, AutoList mStack, byte* retDstPtr, int targetRetRefBase, out bool unhandledException)
        {
            unhandledException = false;
            if (targetMethod is ILMethod ilm)
            {
                ExecuteNeo(ilm, targetBase, retDstPtr, targetRetRefBase, out unhandledException);
                return !unhandledException;
            }
            else if (targetMethod is CLRMethod clrMethod)
            {
                InvokeNeoClrMethod(clrMethod, isNewobj, targetBase, mStack, retDstPtr, targetRetRefBase);
                return true;
            }

            throw new NotImplementedException("Unknown method type in Neo mode.");
        }

        void InvokeNeoClrMethod(CLRMethod clrMethod, bool isNewobj, byte* targetBase, AutoList mStack, byte* retDstPtr, int targetRetRefBase)
        {
            var redirectNeo = clrMethod.RedirectionNeo;
            if (redirectNeo != null)
            {
                // The Neo Redirection owns the dest write for both call and
                // newobj (the redirect delegate allocates/stores the result).
                redirectNeo(this, targetBase, mStack, clrMethod, isNewobj, retDstPtr, targetRetRefBase);
                return;
            }

            object res = clrMethod.Invoke(targetBase, mStack, isNewobj);

            // Step 18 (D3): for a reflection-constructed newobj the returned
            // object MUST be stored into the caller's dest (the early-return
            // previously skipped this, so the dest was never filled). The
            // redirect path already returned above (it owns its dest write).
            // A non-newobj void/no-return also early-returns.
            if (retDstPtr == null)
                return;
            if (isNewobj)
            {
                // The constructed object is a reference type; store it into the
                // dest mStack ref slot and write the index to the dest byte
                // offset (mirrors the reference-type return store below).
                if (targetRetRefBase >= mStack.Count)
                    mStack.Add(res);
                else
                    mStack[targetRetRefBase] = res;
                *(int*)retDstPtr = targetRetRefBase;
                return;
            }

            IType retType = clrMethod.ReturnType;
            if (retType == null || retType == AppDomain.VoidType)
                return;

            if (retType.TypeForCLR.IsPrimitive || retType.TypeForCLR.IsEnum)
            {
                if (retType.TypeForCLR == typeof(int) || retType.TypeForCLR.IsEnum) *(int*)retDstPtr = (int)res;
                else if (retType.TypeForCLR == typeof(long)) *(long*)retDstPtr = (long)res;
                else if (retType.TypeForCLR == typeof(float)) *(float*)retDstPtr = (float)res;
                else if (retType.TypeForCLR == typeof(double)) *(double*)retDstPtr = (double)res;
                else if (retType.TypeForCLR == typeof(bool)) *(int*)retDstPtr = (bool)res ? 1 : 0;
                else if (retType.TypeForCLR == typeof(byte)) *(int*)retDstPtr = (byte)res;
                else if (retType.TypeForCLR == typeof(sbyte)) *(int*)retDstPtr = (sbyte)res;
                else if (retType.TypeForCLR == typeof(short)) *(int*)retDstPtr = (short)res;
                else if (retType.TypeForCLR == typeof(ushort)) *(int*)retDstPtr = (ushort)res;
                else if (retType.TypeForCLR == typeof(uint)) *(uint*)retDstPtr = (uint)res;
                else if (retType.TypeForCLR == typeof(ulong)) *(ulong*)retDstPtr = (ulong)res;
                else if (retType.TypeForCLR == typeof(char)) *(int*)retDstPtr = (char)res;
            }
            else if (retType.IsValueType)
            {
                // Step 13b (D6): write a CLR struct return value's flat managed
                // bytes into the caller's dest frame slot (already sized by
                // AllocateLocalStackSpaces for the value type) via WriteNeoValueType.
                // The inverse of the param read path (D2/D4) -- same size source
                // (Optimizer.GetNeoValueTypeManagedSize), so the write matches the
                // caller's dest layout by construction. A struct with reference
                // fields and no binder is unreadable on the way IN and unwriteable
                // on the way OUT the same way; for the reflection fallback (no
                // binder ref-mapping) we only support pure-primitive / zero-managed-
                // count binder structs. The boxed CLR return `res` carries the GC
                // refs for a binder struct when the binder populated them upstream
                // (def not here); this path writes the flat primitive bytes.
                int retSz = Optimizer.GetNeoValueTypeManagedSize(retType.TypeForCLR);
                WriteNeoValueType(res, retDstPtr, retSz);
            }
            else
            {
                if (targetRetRefBase >= mStack.Count)
                    mStack.Add(res);
                else
                    mStack[targetRetRefBase] = res;

                *(int*)retDstPtr = targetRetRefBase;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static object ReadNeoCallThis(OpCodeR* ip, byte* targetBase, AutoList mStack)
        {
            int thisArgOffset = (int)((uint)ip->Operand4 >> 16);
            int thisIdx = *(int*)(targetBase + thisArgOffset);
            if (thisIdx < 0 || thisIdx >= mStack.Count)
                throw new NullReferenceException("Neo callvirt this is null.");

            object thisObj = mStack[thisIdx];
            if (thisObj == null)
                throw new NullReferenceException("Neo callvirt this is null.");

            return thisObj;
        }

        static IMethod ResolveNeoCallvirtILTarget(OpCodeR* ip, IMethod declaredMethod, byte* targetBase, AutoList mStack)
        {
            object thisObj = ReadNeoCallThis(ip, targetBase, mStack);
            if (thisObj is ILTypeInstance instance)
            {
                int slot = ip->Operand4 & 0xffff;
                if (slot == 0xffff)
                {
                    if (!instance.Type.TryGetNeoVTableSlot(declaredMethod, out slot))
                        throw new MissingMethodException(string.Format("Neo callvirt cannot resolve VTable slot for {0} on {1}.", declaredMethod, instance.Type.FullName));
                }

                var vtable = instance.Type.NeoVTable;
                if (slot < 0 || slot >= vtable.Length)
                    throw new MissingMethodException(string.Format("Neo callvirt VTable slot {0} is missing on {1}.", slot, instance.Type.FullName));

                IMethod actual = vtable[slot];
                if (actual == null)
                    throw new MissingMethodException(string.Format("Neo callvirt VTable slot {0} is null on {1}.", slot, instance.Type.FullName));

                return actual;
            }

            throw new InvalidOperationException(string.Format("Neo Callvirt_IL requires ILTypeInstance this, got {0}.", thisObj.GetType().FullName));
        }

        static IMethod ResolveNeoCallvirtInterfaceTarget(OpCodeR* ip, IMethod declaredMethod, byte* targetBase, AutoList mStack)
        {
            object thisObj = ReadNeoCallThis(ip, targetBase, mStack);
            if (!(thisObj is ILTypeInstance instance))
                throw new InvalidOperationException(string.Format("Neo Callvirt_Interface requires ILTypeInstance this (CLR object through an interface is out of scope for Step 11), got {0}.", thisObj.GetType().FullName));

            ILType runtimeType = instance.Type;
            IType ifaceType = declaredMethod != null ? declaredMethod.DeclearingType : null;
            int ifaceMethodSlot = ip->Operand4 & 0xffff;

            if (ifaceType == null || !runtimeType.TryResolveNeoInterfaceClassSlot(ifaceType, ifaceMethodSlot, out int classSlot))
                throw new MissingMethodException(string.Format("Neo Callvirt_Interface: type {0} does not implement interface {1} (method slot {2}).",
                    runtimeType.FullName, ifaceType != null ? ifaceType.FullName : "<null>", ifaceMethodSlot));

            var vtable = runtimeType.NeoVTable;
            if (classSlot < 0 || classSlot >= vtable.Length)
                throw new MissingMethodException(string.Format("Neo Callvirt_Interface: interface slot out of range on {0} (interface {1}, slot {2} -> class slot {3}).",
                    runtimeType.FullName, ifaceType.FullName, ifaceMethodSlot, classSlot));

            IMethod actual = vtable[classSlot];
            if (actual == null)
                throw new MissingMethodException(string.Format("Neo Callvirt_Interface: interface slot is null on {0} (interface {1}, slot {2} -> class slot {3}).",
                    runtimeType.FullName, ifaceType.FullName, ifaceMethodSlot, classSlot));

            return actual;
        }

        static CLRMethod ResolveNeoCallvirtCLRTarget(OpCodeR* ip, IMethod declaredMethod, byte* targetBase, AutoList mStack)
        {
            ReadNeoCallThis(ip, targetBase, mStack);
            if (declaredMethod is CLRMethod clrMethod)
                return clrMethod;

            throw new InvalidOperationException(string.Format("Neo Callvirt_CLR requires CLRMethod, got {0}.", declaredMethod));
        }

        static IMethod ResolveNeoGenericCallvirtTarget(OpCodeR* ip, IMethod declaredMethod, byte* targetBase, AutoList mStack)
        {
            object thisObj = ReadNeoCallThis(ip, targetBase, mStack);
            if (thisObj is ILTypeInstance)
                return ResolveNeoCallvirtILTarget(ip, declaredMethod, targetBase, mStack);
            if (declaredMethod is CLRMethod)
                return declaredMethod;

            throw new InvalidOperationException(string.Format("Neo generic callvirt cannot dispatch non-IL object {0} to {1}.", thisObj.GetType().FullName, declaredMethod));
        }

        internal unsafe byte* ExecuteNeo(ILMethod method, byte* esp, byte* retDst, int retRefBase, out bool unhandledException,
            byte* vtNewobjCallerDst = null, int vtNewobjCallerDstRefBase = -1, int vtNewobjCallerPrimSize = 0, int vtNewobjCallerRefCount = 0)
        {
#if DEBUG
            if (method == null)
                throw new NullReferenceException();
#endif
#if DEBUG && !NO_PROFILER
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == AppDomain.UnityMainThreadID)

#if UNITY_5_5_OR_NEWER
                UnityEngine.Profiling.Profiler.BeginSample(method.ToString());
#else
                UnityEngine.Profiler.BeginSample(method.ToString());
#endif

#endif
            unhandledException = false;

            OpCodeR[] body = method.CompiledFrame.NeoExecuteBody;
            AutoList mStack = stack.ManagedStack;
            ref readonly var nf = ref method.CompiledFrame;
            int frameSize = nf.TotalStructSize;
            int totalRefSize = nf.TotalRefSize;
            int returnPrimitiveSize = nf.ReturnPrimitiveSize;
            int returnRefCount = nf.ReturnRefCount;
            var localInfos = nf.LocalInfos;
            var localIsRef = nf.LocalIsReference;

            byte* frameBase = esp;
            byte* newEsp = esp + frameSize;
            // TODO: stack overflow check vs stack.StackBase upper bound; will be added in Step 14 / 26

            // Zero locals primitive region
            if (nf.LocalsPrimitiveSize > 0)
                Unsafe.InitBlock(frameBase + nf.ParamPrimitiveSize, 0, (uint)nf.LocalsPrimitiveSize);
            if (localInfos != null && localIsRef != null)
            {
                for (int i = 0; i < localInfos.Length; i++)
                {
                    if (localIsRef[i])
                    {
                        *(int*)(frameBase + localInfos[i].Offset) = -1;
                    }
                }
            }

            // Managed stack reservation for this frame's reference slots
            int frameRefBase = mStack.Count;
            for (int i = 0; i < totalRefSize; i++)
                mStack.Add(null);

            // Frames stack placeholder: keep existing StackFrame plumbing alive.
            // BasePointer is interpreted as byte* via reinterpret cast; full debugger
            // adaptation is deferred to Step 14/26.
            StackFrame frame = new StackFrame();
            frame.LocalVarPointer = (StackObject*)frameBase;
            frame.BasePointer = (StackObject*)frameBase;
            frame.Method = method;
            frame.IsRegister = true;
            frame.ManagedStackBase = frameRefBase;
            frame.ValueTypeBasePointer = stack.ValueTypeStackPointer;
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
            frame.Address = new IntegerReference();
#endif
            stack.PushFrame(ref frame);

            int finallyEndAddress = 0;
            Exception lastCaughtEx = null;
            var ehs = method.ExceptionHandlerRegister;
            // Step 14: when an exception escapes this frame unhandled, we must
            // NOT throw from inside the per-iteration catch (that would skip the
            // bottom-of-method cleanup below and leak this frame on the frames
            // stack / its mStack reservation). Instead we stash the to-be-thrown
            // exception here, break out of the loop, let the cleanup run, and
            // re-throw AFTER cleanup -- making every Neo frame self-cleaning.
            Exception pendingThrow = null;

            fixed (OpCodeR* ptr = body)
            {
                OpCodeR* ip = ptr;
                bool returned = false;
                // Shared locals across case blocks. Declared at method scope so
                // IL2CPP / non-O3 builds reuse the same stack slot for every case.
                IType t;
                ILType ilType;
                ILTypeInstance ins;
                object obj;
                int sz, refCnt, srcIdx, dstIdx, srcRefOffset, dstRefOffset;
                while (!returned)
                {
                    try
                    {
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
                        if (ShouldBreak)
                            Break();
                        var insOffset = (int)(ip - ptr);
                        frame.Address.Value = insOffset;
                        AppDomain.DebugService.CheckShouldBreak(method, this, insOffset);
#endif
                        OpCodeREnum code = ip->Code;
                        switch (code)
                        {
                            case OpCodeREnum.Ldc_I4_M1:
                                *(int*)(frameBase + ip->DstOffset) = -1;
                                break;
                            case OpCodeREnum.Ldc_I4_0:
                                *(int*)(frameBase + ip->DstOffset) = 0;
                                break;
                            case OpCodeREnum.Ldc_I4_1:
                                *(int*)(frameBase + ip->DstOffset) = 1;
                                break;
                            case OpCodeREnum.Ldc_I4_2:
                                *(int*)(frameBase + ip->DstOffset) = 2;
                                break;
                            case OpCodeREnum.Ldc_I4_3:
                                *(int*)(frameBase + ip->DstOffset) = 3;
                                break;
                            case OpCodeREnum.Ldc_I4_4:
                                *(int*)(frameBase + ip->DstOffset) = 4;
                                break;
                            case OpCodeREnum.Ldc_I4_5:
                                *(int*)(frameBase + ip->DstOffset) = 5;
                                break;
                            case OpCodeREnum.Ldc_I4_6:
                                *(int*)(frameBase + ip->DstOffset) = 6;
                                break;
                            case OpCodeREnum.Ldc_I4_7:
                                *(int*)(frameBase + ip->DstOffset) = 7;
                                break;
                            case OpCodeREnum.Ldc_I4_8:
                                *(int*)(frameBase + ip->DstOffset) = 8;
                                break;
                            case OpCodeREnum.Ldc_I4:
                            case OpCodeREnum.Ldc_I4_S:
                                *(int*)(frameBase + ip->DstOffset) = ip->Operand;
                                break;
                            case OpCodeREnum.Ldc_I8:
                                *(long*)(frameBase + ip->DstOffset) = ip->OperandLong;
                                break;
                            case OpCodeREnum.Ldc_R4:
                                *(float*)(frameBase + ip->DstOffset) = ip->OperandFloat;
                                break;
                            case OpCodeREnum.Ldc_R8:
                                *(double*)(frameBase + ip->DstOffset) = ip->OperandDouble;
                                break;
                            case OpCodeREnum.Ldnull:
                                *(int*)(frameBase + ip->DstOffset) = -1;
                                break;
                            case OpCodeREnum.Ldstr:
                                dstIdx = frameRefBase + ip->Operand;
                                mStack[dstIdx] = AppDomain.GetString(ip->OperandLong);
                                *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                break;
                            case OpCodeREnum.Move:
                                Unsafe.CopyBlock(frameBase + ip->DstOffset, frameBase + ip->SrcOffset, (uint)ip->Operand2);
                                if (ip->Operand == 1)
                                {
                                    dstRefOffset = ip->Operand3;
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    dstIdx = frameRefBase + dstRefOffset;
                                    if (srcIdx >= 0)
                                    {
                                        mStack[dstIdx] = mStack[srcIdx];
                                        *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                    }
                                    else
                                    {
                                        mStack[dstIdx] = null;
                                        *(int*)(frameBase + ip->DstOffset) = -1;
                                    }
                                }
                                break;
                            // Step 12b: whole value-type copy (assignment / local-
                            // init / starg of a value type with reference fields).
                            // Operand2 = primitive byte size; Operand3 = dst ref-
                            // run base; Operand = src ref-run base; Operand4 =
                            // refCount. In-frame value-type reference fields live
                            // out-of-line in the frame mStack ref region (Step 12
                            // design), so the copy is a byte CopyBlock for the
                            // primitive region PLUS a direct mStack-to-mStack copy
                            // of refCount reference slots. src and dst runs share
                            // the same length (same-type assignment), preserving
                            // object identity per C# shallow struct-copy semantics.
                            case OpCodeREnum.Move_Vt:
                                {
                                    int vtPrimSize = ip->Operand2;
                                    int vtDstRefBase = ip->Operand3;
                                    int vtSrcRefBase = ip->Operand;
                                    int vtRefCount = ip->Operand4;
                                    if (vtPrimSize > 0)
                                        Unsafe.CopyBlock(frameBase + ip->DstOffset, frameBase + ip->SrcOffset, (uint)vtPrimSize);
                                    for (int i = 0; i < vtRefCount; i++)
                                    {
                                        mStack[frameRefBase + vtDstRefBase + i] =
                                            mStack[frameRefBase + vtSrcRefBase + i];
                                    }
                                }
                                break;
                            // Step 12: ldloca / ldloca.s of a value-type local.
                            // The C# compiler emits `ldloca V; stfld/ldfld/initobj`
                            // for struct field access. The Neo offset-lowering pass
                            // resolves the ldloca dest back to the source local's
                            // frame offset for the _Inline field opcodes and
                            // Initobj, so those do not read this temp's bytes.
                            // Therefore ldloca itself is a no-op at runtime for the
                            // Step 12 in-frame-VT field-access path. (Genuine byref
                            // use of the address -- ref params, stind, fixed -- is a
                            // separate pointer model not covered by Step 12 and
                            // remains unimplemented; the dest temp is left as-is.)
                            case OpCodeREnum.Ldloca:
                            case OpCodeREnum.Ldloca_S:
                            case OpCodeREnum.Ldarga:
                            case OpCodeREnum.Ldarga_S:
                                {
                                    // Step 17: produce a real 8-byte Ref Slot =
                                    // (-1, absoluteFrameOffset) -- a frame-native
                                    // managed address. The optimizer's addrAlias
                                    // folding still resolves the pure in-frame-VT
                                    // `ldloca V; stfld/ldfld/initobj` pattern at
                                    // compile time, in which case this dest is
                                    // dead at runtime (writing the slot is
                                    // harmless). When the address ESCAPES the
                                    // folding window (byref param / stind / ldind
                                    // / stobj / ldobj / ldelema / constrained
                                    // box), the optimizer leaves this dest real
                                    // and this arm produces the genuine Ref Slot.
                                    // (ldloca/ldarga of a reference-typed slot is
                                    // illegal in verifiable IL, so the source is
                                    // always a value-type slot -> frame-native.)
                                    // ip->SrcOffset = the source slot's frame byte
                                    // offset (lowered from R2).
                                    int dst = ip->DstOffset;
                                    *(int*)(frameBase + dst + 0) = -1;            // frame-native
                                    *(int*)(frameBase + dst + 4) = ip->SrcOffset; // absolute frame byte offset
                                }
                                break;
                            // Step 12: ldflda of a nested in-frame value-type
                            // field. Like ldloca, the address is resolved at
                            // offset-lowering time (the dest is recorded in the
                            // address-alias map with the accumulated nested-field
                            // byte offset), so the leaf _Inline field access uses
                            // the folded absolute offset directly. ldflda itself
                            // is therefore a no-op at runtime for the in-frame-VT
                            // path. (Heap-instance ldflda remains Step 6+.)
                            case OpCodeREnum.Ldflda:
                                {
                                    // Step 17: dispatch on the operand's Ref Slot
                                    // objectIndex half. ip->Operand2 =
                                    // field.PrimitiveOffset. ip->SrcOffset = the
                                    // operand slot byte offset (lowered from R2).
                                    // When the operand is an in-frame VT address
                                    // (produced by a real ldloca/ldarga), its
                                    // objectIndex half is -1 and the offset half
                                    // is the VT base -> produce a frame-native
                                    // Ref Slot. When the operand is a heap IL
                                    // object, its slot holds an mStack index
                                    // (objectIndex >= 0) -> produce
                                    // (mStackIdx, fieldPrimOff).
                                    int dst = ip->DstOffset;
                                    int operandSlotOff = ip->SrcOffset;
                                    int fieldPrimOff = ip->Operand2;
                                    int objIdx = *(int*)(frameBase + operandSlotOff + 0);
                                    if (objIdx == -1)
                                    {
                                        int vtBase = *(int*)(frameBase + operandSlotOff + 4);
                                        *(int*)(frameBase + dst + 0) = -1;
                                        *(int*)(frameBase + dst + 4) = vtBase + fieldPrimOff;
                                    }
                                    else
                                    {
                                        *(int*)(frameBase + dst + 0) = objIdx;
                                        *(int*)(frameBase + dst + 4) = fieldPrimOff;
                                    }
                                }
                                break;
                            case OpCodeREnum.Add:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) + *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Sub:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) - *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Mul:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) * *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Div:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) / *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Div_Un:
                                *(int*)(frameBase + ip->DstOffset) = (int)(*(uint*)(frameBase + ip->SrcOffset) / *(uint*)(frameBase + ip->OperandOffset));
                                break;
                            case OpCodeREnum.Rem:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) % *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Rem_Un:
                                *(int*)(frameBase + ip->DstOffset) = (int)(*(uint*)(frameBase + ip->SrcOffset) % *(uint*)(frameBase + ip->OperandOffset));
                                break;
                            case OpCodeREnum.And:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) & *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Or:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) | *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Xor:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) ^ *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Shl:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) << *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Shr:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) >> *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Shr_Un:
                                *(int*)(frameBase + ip->DstOffset) = (int)(*(uint*)(frameBase + ip->SrcOffset) >> *(int*)(frameBase + ip->OperandOffset));
                                break;
                            case OpCodeREnum.Neg:
                                *(int*)(frameBase + ip->DstOffset) = -*(int*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Not:
                                *(int*)(frameBase + ip->DstOffset) = ~*(int*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Add_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) + *(long*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Sub_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) - *(long*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Mul_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) * *(long*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Div_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) / *(long*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Div_Un_I8:
                                *(long*)(frameBase + ip->DstOffset) = (long)(*(ulong*)(frameBase + ip->SrcOffset) / *(ulong*)(frameBase + ip->OperandOffset));
                                break;
                            case OpCodeREnum.Rem_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) % *(long*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Rem_Un_I8:
                                *(long*)(frameBase + ip->DstOffset) = (long)(*(ulong*)(frameBase + ip->SrcOffset) % *(ulong*)(frameBase + ip->OperandOffset));
                                break;
                            case OpCodeREnum.And_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) & *(long*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Or_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) | *(long*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Xor_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) ^ *(long*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Shl_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) << *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Shr_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) >> *(int*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Shr_Un_I8:
                                *(long*)(frameBase + ip->DstOffset) = (long)(*(ulong*)(frameBase + ip->SrcOffset) >> *(int*)(frameBase + ip->OperandOffset));
                                break;
                            case OpCodeREnum.Neg_I8:
                                *(long*)(frameBase + ip->DstOffset) = -*(long*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Not_I8:
                                *(long*)(frameBase + ip->DstOffset) = ~*(long*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Add_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) + *(float*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Sub_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) - *(float*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Mul_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) * *(float*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Div_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) / *(float*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Rem_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) % *(float*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Neg_R4:
                                *(float*)(frameBase + ip->DstOffset) = -*(float*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Add_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) + *(double*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Sub_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) - *(double*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Mul_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) * *(double*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Div_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) / *(double*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Rem_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) % *(double*)(frameBase + ip->OperandOffset);
                                break;
                            case OpCodeREnum.Neg_R8:
                                *(double*)(frameBase + ip->DstOffset) = -*(double*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Ceq:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) == *(int*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgt:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) > *(int*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgt_Un:
                                {
                                    // Step 15: CIL `cgt.un` doubles as the reference
                                    // "not null" test (the C# `is`/`as != null`/`!= null`
                                    // lowering is `ldnull; cgt.un`). Under the Neo flat
                                    // frame a reference is an mStack index with -1 =
                                    // null, so a naive unsigned compare mis-handles the
                                    // null sentinel (-1 == 0xFFFFFFFF). Mirror Legacy's
                                    // reference/integer rule (ILIntepreter.Register.cs
                                    // Cgt_Un): when the src slot is null (-1) the result
                                    // is false; otherwise the unsigned compare holds, OR
                                    // the operand is itself null (-1). (Diverges from raw
                                    // unsigned semantics only for the pathological
                                    // `cgt.un x, (uint)0xFFFFFFFF` integer case, which
                                    // the validated tests do not exercise.)
                                    int cguA = *(int*)(frameBase + ip->SrcOffset);
                                    int cguB = *(int*)(frameBase + ip->OperandOffset);
                                    bool cguRes = cguA != -1 && ((uint)cguA > (uint)cguB || cguB == -1);
                                    *(int*)(frameBase + ip->DstOffset) = cguRes ? 1 : 0;
                                }
                                break;
                            case OpCodeREnum.Clt:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) < *(int*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Clt_Un:
                                *(int*)(frameBase + ip->DstOffset) = *(uint*)(frameBase + ip->SrcOffset) < *(uint*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Ceq_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) == *(long*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgt_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) > *(long*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgt_Un_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(ulong*)(frameBase + ip->SrcOffset) > *(ulong*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Clt_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) < *(long*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Clt_Un_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(ulong*)(frameBase + ip->SrcOffset) < *(ulong*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Ceq_R4:
                                *(int*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) == *(float*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Ceq_R8:
                                *(int*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) == *(double*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgt_R4:
                            case OpCodeREnum.Cgt_Un_R4:
                                *(int*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) > *(float*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgt_R8:
                            case OpCodeREnum.Cgt_Un_R8:
                                *(int*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) > *(double*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Clt_R4:
                            case OpCodeREnum.Clt_Un_R4:
                                *(int*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) < *(float*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Clt_R8:
                            case OpCodeREnum.Clt_Un_R8:
                                *(int*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) < *(double*)(frameBase + ip->OperandOffset) ? 1 : 0;
                                break;
                            case OpCodeREnum.Br:
                            case OpCodeREnum.Br_S:
                                ip = ptr + ip->Operand;
                                continue;
                            case OpCodeREnum.Brtrue:
                            case OpCodeREnum.Brtrue_S:
                                if (ip->Operand2 == 8 ? *(long*)(frameBase + ip->DstOffset) != 0 : *(int*)(frameBase + ip->DstOffset) != 0)
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Brfalse:
                            case OpCodeREnum.Brfalse_S:
                                if (ip->Operand2 == 8 ? *(long*)(frameBase + ip->DstOffset) == 0 : *(int*)(frameBase + ip->DstOffset) == 0)
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Beq:
                                if (*(int*)(frameBase + ip->DstOffset) == *(int*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bne_Un:
                                if (*(int*)(frameBase + ip->DstOffset) != *(int*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blt:
                                if (*(int*)(frameBase + ip->DstOffset) < *(int*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgt:
                                if (*(int*)(frameBase + ip->DstOffset) > *(int*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Ble:
                                if (*(int*)(frameBase + ip->DstOffset) <= *(int*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bge:
                                if (*(int*)(frameBase + ip->DstOffset) >= *(int*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blt_Un:
                                if (*(uint*)(frameBase + ip->DstOffset) < *(uint*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgt_Un:
                                if (*(uint*)(frameBase + ip->DstOffset) > *(uint*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Ble_Un:
                                if (*(uint*)(frameBase + ip->DstOffset) <= *(uint*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bge_Un:
                                if (*(uint*)(frameBase + ip->DstOffset) >= *(uint*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Beq_I8:
                                if (*(long*)(frameBase + ip->DstOffset) == *(long*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bne_Un_I8:
                                if (*(long*)(frameBase + ip->DstOffset) != *(long*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blt_I8:
                                if (*(long*)(frameBase + ip->DstOffset) < *(long*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgt_I8:
                                if (*(long*)(frameBase + ip->DstOffset) > *(long*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Ble_I8:
                                if (*(long*)(frameBase + ip->DstOffset) <= *(long*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bge_I8:
                                if (*(long*)(frameBase + ip->DstOffset) >= *(long*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blt_Un_I8:
                                if (*(ulong*)(frameBase + ip->DstOffset) < *(ulong*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgt_Un_I8:
                                if (*(ulong*)(frameBase + ip->DstOffset) > *(ulong*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Ble_Un_I8:
                                if (*(ulong*)(frameBase + ip->DstOffset) <= *(ulong*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bge_Un_I8:
                                if (*(ulong*)(frameBase + ip->DstOffset) >= *(ulong*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Beq_R4:
                                if (*(float*)(frameBase + ip->DstOffset) == *(float*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bne_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) != *(float*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blt_R4:
                            case OpCodeREnum.Blt_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) < *(float*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgt_R4:
                            case OpCodeREnum.Bgt_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) > *(float*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Ble_R4:
                            case OpCodeREnum.Ble_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) <= *(float*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bge_R4:
                            case OpCodeREnum.Bge_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) >= *(float*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Beq_R8:
                                if (*(double*)(frameBase + ip->DstOffset) == *(double*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bne_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) != *(double*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blt_R8:
                            case OpCodeREnum.Blt_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) < *(double*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgt_R8:
                            case OpCodeREnum.Bgt_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) > *(double*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Ble_R8:
                            case OpCodeREnum.Ble_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) <= *(double*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bge_R8:
                            case OpCodeREnum.Bge_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) >= *(double*)(frameBase + ip->SrcOffset))
                                {
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Addi:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) + ip->Operand;
                                break;
                            case OpCodeREnum.Subi:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) - ip->Operand;
                                break;
                            case OpCodeREnum.Muli:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) * ip->Operand;
                                break;
                            case OpCodeREnum.Divi:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) / ip->Operand;
                                break;
                            case OpCodeREnum.Divi_Un:
                                *(int*)(frameBase + ip->DstOffset) = (int)(*(uint*)(frameBase + ip->SrcOffset) / (uint)ip->Operand);
                                break;
                            case OpCodeREnum.Remi:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) % ip->Operand;
                                break;
                            case OpCodeREnum.Remi_Un:
                                *(int*)(frameBase + ip->DstOffset) = (int)(*(uint*)(frameBase + ip->SrcOffset) % (uint)ip->Operand);
                                break;
                            case OpCodeREnum.Andi:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) & ip->Operand;
                                break;
                            case OpCodeREnum.Ori:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) | ip->Operand;
                                break;
                            case OpCodeREnum.Xori:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) ^ ip->Operand;
                                break;
                            case OpCodeREnum.Shli:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) << ip->Operand;
                                break;
                            case OpCodeREnum.Shri:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) >> ip->Operand;
                                break;
                            case OpCodeREnum.Shri_Un:
                                *(int*)(frameBase + ip->DstOffset) = (int)(*(uint*)(frameBase + ip->SrcOffset) >> ip->Operand);
                                break;
                            case OpCodeREnum.Addi_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) + ip->OperandLong;
                                break;
                            case OpCodeREnum.Subi_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) - ip->OperandLong;
                                break;
                            case OpCodeREnum.Muli_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) * ip->OperandLong;
                                break;
                            case OpCodeREnum.Divi_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) / ip->OperandLong;
                                break;
                            case OpCodeREnum.Divi_Un_I8:
                                *(long*)(frameBase + ip->DstOffset) = (long)(*(ulong*)(frameBase + ip->SrcOffset) / (ulong)ip->OperandLong);
                                break;
                            case OpCodeREnum.Remi_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) % ip->OperandLong;
                                break;
                            case OpCodeREnum.Remi_Un_I8:
                                *(long*)(frameBase + ip->DstOffset) = (long)(*(ulong*)(frameBase + ip->SrcOffset) % (ulong)ip->OperandLong);
                                break;
                            case OpCodeREnum.Andi_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) & ip->OperandLong;
                                break;
                            case OpCodeREnum.Ori_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) | ip->OperandLong;
                                break;
                            case OpCodeREnum.Xori_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) ^ ip->OperandLong;
                                break;
                            case OpCodeREnum.Shli_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) << (int)ip->OperandLong;
                                break;
                            case OpCodeREnum.Shri_I8:
                                *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) >> (int)ip->OperandLong;
                                break;
                            case OpCodeREnum.Shri_Un_I8:
                                *(long*)(frameBase + ip->DstOffset) = (long)(*(ulong*)(frameBase + ip->SrcOffset) >> (int)ip->OperandLong);
                                break;
                            case OpCodeREnum.Addi_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) + ip->OperandFloat;
                                break;
                            case OpCodeREnum.Subi_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) - ip->OperandFloat;
                                break;
                            case OpCodeREnum.Muli_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) * ip->OperandFloat;
                                break;
                            case OpCodeREnum.Divi_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) / ip->OperandFloat;
                                break;
                            case OpCodeREnum.Remi_R4:
                                *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) % ip->OperandFloat;
                                break;
                            case OpCodeREnum.Addi_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) + ip->OperandDouble;
                                break;
                            case OpCodeREnum.Subi_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) - ip->OperandDouble;
                                break;
                            case OpCodeREnum.Muli_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) * ip->OperandDouble;
                                break;
                            case OpCodeREnum.Divi_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) / ip->OperandDouble;
                                break;
                            case OpCodeREnum.Remi_R8:
                                *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) % ip->OperandDouble;
                                break;
                            case OpCodeREnum.Ceqi:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) == ip->Operand ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgti:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) > ip->Operand ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgti_Un:
                                *(int*)(frameBase + ip->DstOffset) = *(uint*)(frameBase + ip->SrcOffset) > (uint)ip->Operand ? 1 : 0;
                                break;
                            case OpCodeREnum.Clti:
                                *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + ip->SrcOffset) < ip->Operand ? 1 : 0;
                                break;
                            case OpCodeREnum.Clti_Un:
                                *(int*)(frameBase + ip->DstOffset) = *(uint*)(frameBase + ip->SrcOffset) < (uint)ip->Operand ? 1 : 0;
                                break;
                            case OpCodeREnum.Ceqi_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) == ip->OperandLong ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgti_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) > ip->OperandLong ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgti_Un_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(ulong*)(frameBase + ip->SrcOffset) > (ulong)ip->OperandLong ? 1 : 0;
                                break;
                            case OpCodeREnum.Clti_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(long*)(frameBase + ip->SrcOffset) < ip->OperandLong ? 1 : 0;
                                break;
                            case OpCodeREnum.Clti_Un_I8:
                                *(int*)(frameBase + ip->DstOffset) = *(ulong*)(frameBase + ip->SrcOffset) < (ulong)ip->OperandLong ? 1 : 0;
                                break;
                            case OpCodeREnum.Ceqi_R4:
                                *(int*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) == ip->OperandFloat ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgti_R4:
                            case OpCodeREnum.Cgti_Un_R4:
                                *(int*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) > ip->OperandFloat ? 1 : 0;
                                break;
                            case OpCodeREnum.Clti_R4:
                            case OpCodeREnum.Clti_Un_R4:
                                *(int*)(frameBase + ip->DstOffset) = *(float*)(frameBase + ip->SrcOffset) < ip->OperandFloat ? 1 : 0;
                                break;
                            case OpCodeREnum.Ceqi_R8:
                                *(int*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) == ip->OperandDouble ? 1 : 0;
                                break;
                            case OpCodeREnum.Cgti_R8:
                            case OpCodeREnum.Cgti_Un_R8:
                                *(int*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) > ip->OperandDouble ? 1 : 0;
                                break;
                            case OpCodeREnum.Clti_R8:
                            case OpCodeREnum.Clti_Un_R8:
                                *(int*)(frameBase + ip->DstOffset) = *(double*)(frameBase + ip->SrcOffset) < ip->OperandDouble ? 1 : 0;
                                break;
                            case OpCodeREnum.Beqi:
                                if (*(int*)(frameBase + ip->DstOffset) == ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bnei_Un:
                                if (*(int*)(frameBase + ip->DstOffset) != ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blti:
                                if (*(int*)(frameBase + ip->DstOffset) < ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgti:
                                if (*(int*)(frameBase + ip->DstOffset) > ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blei:
                                if (*(int*)(frameBase + ip->DstOffset) <= ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgei:
                                if (*(int*)(frameBase + ip->DstOffset) >= ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blti_Un:
                                if (*(uint*)(frameBase + ip->DstOffset) < (uint)ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgti_Un:
                                if (*(uint*)(frameBase + ip->DstOffset) > (uint)ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blei_Un:
                                if (*(uint*)(frameBase + ip->DstOffset) <= (uint)ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgei_Un:
                                if (*(uint*)(frameBase + ip->DstOffset) >= (uint)ip->Operand)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Beqi_I8:
                                if (*(long*)(frameBase + ip->DstOffset) == ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bnei_Un_I8:
                                if (*(long*)(frameBase + ip->DstOffset) != ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blti_I8:
                                if (*(long*)(frameBase + ip->DstOffset) < ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgti_I8:
                                if (*(long*)(frameBase + ip->DstOffset) > ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blei_I8:
                                if (*(long*)(frameBase + ip->DstOffset) <= ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgei_I8:
                                if (*(long*)(frameBase + ip->DstOffset) >= ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blti_Un_I8:
                                if (*(ulong*)(frameBase + ip->DstOffset) < (ulong)ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgti_Un_I8:
                                if (*(ulong*)(frameBase + ip->DstOffset) > (ulong)ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blei_Un_I8:
                                if (*(ulong*)(frameBase + ip->DstOffset) <= (ulong)ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgei_Un_I8:
                                if (*(ulong*)(frameBase + ip->DstOffset) >= (ulong)ip->OperandLong)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Beqi_R4:
                                if (*(float*)(frameBase + ip->DstOffset) == ip->OperandFloat)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bnei_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) != ip->OperandFloat)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blti_R4:
                            case OpCodeREnum.Blti_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) < ip->OperandFloat)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgti_R4:
                            case OpCodeREnum.Bgti_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) > ip->OperandFloat)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blei_R4:
                            case OpCodeREnum.Blei_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) <= ip->OperandFloat)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgei_R4:
                            case OpCodeREnum.Bgei_Un_R4:
                                if (*(float*)(frameBase + ip->DstOffset) >= ip->OperandFloat)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Beqi_R8:
                                if (*(double*)(frameBase + ip->DstOffset) == ip->OperandDouble)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bnei_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) != ip->OperandDouble)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blti_R8:
                            case OpCodeREnum.Blti_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) < ip->OperandDouble)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgti_R8:
                            case OpCodeREnum.Bgti_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) > ip->OperandDouble)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Blei_R8:
                            case OpCodeREnum.Blei_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) <= ip->OperandDouble)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Bgei_R8:
                            case OpCodeREnum.Bgei_Un_R8:
                                if (*(double*)(frameBase + ip->DstOffset) >= ip->OperandDouble)
                                {
                                    ip = ptr + ip->Operand4;
                                    continue;
                                }
                                break;
                            case OpCodeREnum.Conv_I1:
                                *(int*)(frameBase + ip->DstOffset) = (sbyte)ReadConvI8(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_U1:
                                *(int*)(frameBase + ip->DstOffset) = (byte)ReadConvI8(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_I2:
                                *(int*)(frameBase + ip->DstOffset) = (short)ReadConvI8(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_U2:
                                *(int*)(frameBase + ip->DstOffset) = (ushort)ReadConvI8(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_I4:
                                *(int*)(frameBase + ip->DstOffset) = ReadConvI4(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_U4:
                                *(uint*)(frameBase + ip->DstOffset) = ReadConvU4(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_I8:
                                *(long*)(frameBase + ip->DstOffset) = ReadConvI8(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_U8:
                                *(ulong*)(frameBase + ip->DstOffset) = ReadConvU8(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_R4:
                                *(float*)(frameBase + ip->DstOffset) = ReadConvR4(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Conv_R8:
                                *(double*)(frameBase + ip->DstOffset) = ReadConvR8(frameBase, ip->SrcOffset, (NeoPrimitiveTypeTag)ip->Operand2);
                                break;
                            case OpCodeREnum.Call:
                                {
                                    var targetMethod = AppDomain.GetMethod(ip->Operand2);
                                    if (targetMethod == null)
                                    {
                                        ip++;
                                        continue;
                                    }

                                    int callParamIdx = ip->Operand;
                                    ref var map = ref nf.NeoCallParams[callParamIdx];
                                    byte* targetBase = newEsp;
                                    CopyNeoCallArguments(ref map, frameBase, targetBase);

                                    // Reference parameters are passed as existing mStack indices in the frame bytes.
                                    // The primitive copy above has already copied those indices into targetBase.

                                    byte* retDstPtr = null;
                                    int targetRetRefBase = -1;

                                    if (ip->Register1 >= 0)
                                    {
                                        retDstPtr = frameBase + ip->DstOffset;
                                        targetRetRefBase = frameRefBase + ip->Operand3;
                                    }

                                    if (!InvokeNeoCallTarget(targetMethod, false, targetBase, mStack, retDstPtr, targetRetRefBase, out unhandledException))
                                        return null;

                                    ip++;
                                    continue;
                                }
                            case OpCodeREnum.Newobj:
                                {
                                    var targetMethod = AppDomain.GetMethod(ip->Operand2);
                                    if (targetMethod == null)
                                    {
                                        ip++;
                                        continue;
                                    }

                                    int callParamIdx = ip->Operand;
                                    ref var map = ref nf.NeoCallParams[callParamIdx];
                                    byte* targetBase = newEsp;

                                    // dest register layout (stamped by the Optimizer
                                    // Newobj lowering from localInfos[op.Register1]).
                                    dstRefOffset = ip->Operand3;
                                    int newobjDstIdx = frameRefBase + dstRefOffset;
                                    byte* retDstPtr = frameBase + ip->DstOffset;

                                    var ilNewobjType = targetMethod.DeclearingType as ILType;
                                    if (ilNewobjType == null)
                                    {
                                        // Step 18 (D3): CLR-type newobj. Route to
                                        // InvokeNeoClrMethod(isNewobj:true). The dest is a
                                        // reference temp (4-byte mStack index + 1 ref slot),
                                        // same shape as the IL ref-type newobj dest.
                                        // InvokeNeoClrMethod stores the reflection-created
                                        // object into the dest mStack ref slot + writes the
                                        // index to the dest byte offset (the redirect path
                                        // owns its own dest write).
                                        if (targetMethod.DeclearingType is CLRType)
                                        {
                                            CopyNeoCallArguments(ref map, frameBase, targetBase);
                                            var clrCtor = targetMethod as CLRMethod;
                                            InvokeNeoClrMethod(clrCtor, true, targetBase, mStack, retDstPtr, newobjDstIdx);

                                            ip++;
                                            continue;
                                        }
                                        // Delegate / unknown: keep the Step 19 NIE.
                                        throw new NotImplementedException("Neo Newobj CLR type is not implemented (Step 9)");
                                    }

                                    if (ilNewobjType.IsDelegate)
                                        throw new NotImplementedException("Neo Newobj delegate is not implemented");

                                    if (ilNewobjType.IsValueType && !ilNewobjType.IsPrimitive && !ilNewobjType.IsEnum)
                                    {
                                        // VT-THIS-ADDR (resolves Step 18 D1 / Q-VT-NEWOBJ): IL
                                        // value-type newobj via the copy-back pattern, adapted
                                        // from Legacy `*reg1 = *ins` to the Neo frame model.
                                        //
                                        // The callee ctor's `this` (param slot 0) is laid out
                                        // and typed as the in-frame VT value
                                        // (AllocateLocalStackSpaces sizes ParamInfos[0] as
                                        // TotalPrimitiveSize/TotalReferenceCount; the type-spec
                                        // pass seeds registerTypes[0] = declaringType), so the
                                        // ctor's `this.field =` lowers to `_Inline` and writes
                                        // the CALLEE frame's slot-0 byte/ref region. The Neo
                                        // frame-native Ref-Slot zero-copy mechanism does NOT
                                        // apply here because the `_Inline` arm writes through
                                        // the owning slot's frame bytes directly (it does not
                                        // dereference a Ref Slot), and the caller's dest region
                                        // is a SEPARATE frame buffer from the callee's slot-0
                                        // region. So: zero-init the caller's dest, copy the
                                        // caller's dest region INTO the callee's slot-0 region
                                        // (pre-call, so a ctor reading an existing field sees
                                        // the zero/default), copy the ctor args to slots [1..],
                                        // invoke, then copy the callee's slot-0 region BACK to
                                        // the caller's dest region (post-call, picking up the
                                        // ctor's writes). No heap ILTypeInstance; no mStack
                                        // `this` push; the inline-stfld seeding fix above makes
                                        // every same-owner `this.field=` resolve consistently.
                                        var ilCtor = targetMethod as ILMethod;
                                        var ctorFrame = ilCtor.CompiledFrame;
                                        // Resolve the callee slot-0 (this) primitive offset.
                                        // For a HasThis VT ctor ParamInfos[0] is the in-frame
                                        // value (the ref offset / count are read inside
                                        // ExecuteNeo's Ret-arm copy-back via ParamInfos[0]).
                                        var thisSlot = ctorFrame.ParamInfos[0];
                                        int thisPrimOff = thisSlot.Offset;

                                        int vtPrimSize = ilNewobjType.TotalPrimitiveSize;
                                        int vtRefCount = ilNewobjType.TotalReferenceCount;

                                        // 1) Zero-init the caller's dest region (prim + refs)
                                        //    so a ctor that sets only SOME fields leaves the
                                        //    rest at the default (matches Initobj semantics).
                                        if (vtPrimSize > 0)
                                            Unsafe.InitBlock(frameBase + ip->DstOffset, 0, (uint)vtPrimSize);
                                        for (int i = 0; i < vtRefCount; i++)
                                            mStack[newobjDstIdx + i] = null;

                                        // 2) Seed the callee's slot-0 PRIMITIVE bytes with the
                                        //    caller's dest prim bytes (pre-call). ExecuteNeo
                                        //    reserves the callee's slot-0 REF region as nulls
                                        //    itself (the in-frame-VT zero-init); the ctor's ref
                                        //    writes to slot-0 are copied back to the caller's
                                        //    dest by ExecuteNeo's cleanup BEFORE the mStack pop
                                        //    (see the vtNewobjCallerDst path in ExecuteNeo).
                                        if (vtPrimSize > 0)
                                            Unsafe.CopyBlock(targetBase + thisPrimOff, frameBase + ip->DstOffset, (uint)vtPrimSize);

                                        // 3) Copy the remaining ctor args (slots [1..]). The
                                        //    lowering built the NeoCallParamMap skipping slot 0.
                                        CopyNeoCallArguments(ref map, frameBase, targetBase);

                                        // 4) Invoke the ctor directly via ExecuteNeo (not
                                        //    InvokeNeoCallTarget) so we can pass the caller's dest
                                        //    region for the slot-0 -> dest copy-back. ExecuteNeo
                                        //    performs that copy-back in its cleanup BEFORE popping
                                        //    the callee's mStack reservation -- the ref half MUST
                                        //    be copied before the pop (the slot-0 ref entries are
                                        //    removed by the pop). retDst=null: a constructor has
                                        //    no return value (the dest is filled via the slot-0
                                        //    copy-back, not via a return write).
                                        ExecuteNeo(ilCtor, targetBase, null, -1, out unhandledException,
                                            frameBase + ip->DstOffset, newobjDstIdx, vtPrimSize, vtRefCount);
                                        if (unhandledException)
                                            return null;

                                        ip++;
                                        continue;
                                    }

                                    // IL reference-type newobj (Step 8b, unchanged).
                                    ins = ilNewobjType.Instantiate(false);
                                    mStack[newobjDstIdx] = ins;
                                    *(int*)retDstPtr = newobjDstIdx;

                                    *(int*)targetBase = newobjDstIdx;
                                    CopyNeoCallArguments(ref map, frameBase, targetBase);

                                    int targetRetRefBase = frameRefBase + dstRefOffset;
                                    mStack.Add(mStack[newobjDstIdx]); // push 'this'

                                    if (!InvokeNeoCallTarget(targetMethod, true, targetBase, mStack, null, targetRetRefBase, out unhandledException))
                                        return null;

                                    ip++;
                                    continue;
                                }
                            case OpCodeREnum.Callvirt_IL:
                                {
                                    var targetMethod = AppDomain.GetMethod(ip->Operand2);
                                    if (targetMethod == null)
                                    {
                                        ip++;
                                        continue;
                                    }

                                    int callParamIdx = ip->Operand;
                                    ref var map = ref nf.NeoCallParams[callParamIdx];
                                    byte* targetBase = newEsp;
                                    CopyNeoCallArguments(ref map, frameBase, targetBase);

                                    byte* retDstPtr = ip->Register1 >= 0 ? frameBase + ip->DstOffset : null;
                                    int targetRetRefBase = ip->Register1 >= 0 ? frameRefBase + ip->Operand3 : -1;
                                    IMethod actualMethod = ResolveNeoCallvirtILTarget(ip, targetMethod, targetBase, mStack);

                                    if (!InvokeNeoCallTarget(actualMethod, false, targetBase, mStack, retDstPtr, targetRetRefBase, out unhandledException))
                                        return null;

                                    ip++;
                                    continue;
                                }
                            case OpCodeREnum.Callvirt_CLR:
                                {
                                    var targetMethod = AppDomain.GetMethod(ip->Operand2);
                                    if (targetMethod == null)
                                    {
                                        ip++;
                                        continue;
                                    }

                                    int callParamIdx = ip->Operand;
                                    ref var map = ref nf.NeoCallParams[callParamIdx];
                                    byte* targetBase = newEsp;
                                    CopyNeoCallArguments(ref map, frameBase, targetBase);

                                    byte* retDstPtr = ip->Register1 >= 0 ? frameBase + ip->DstOffset : null;
                                    int targetRetRefBase = ip->Register1 >= 0 ? frameRefBase + ip->Operand3 : -1;
                                    CLRMethod clrMethod = ResolveNeoCallvirtCLRTarget(ip, targetMethod, targetBase, mStack);
                                    InvokeNeoClrMethod(clrMethod, false, targetBase, mStack, retDstPtr, targetRetRefBase);

                                    ip++;
                                    continue;
                                }
                            case OpCodeREnum.Callvirt_Interface:
                                {
                                    var targetMethod = AppDomain.GetMethod(ip->Operand2);
                                    if (targetMethod == null)
                                    {
                                        ip++;
                                        continue;
                                    }

                                    int callParamIdx = ip->Operand;
                                    ref var map = ref nf.NeoCallParams[callParamIdx];
                                    byte* targetBase = newEsp;
                                    CopyNeoCallArguments(ref map, frameBase, targetBase);

                                    byte* retDstPtr = ip->Register1 >= 0 ? frameBase + ip->DstOffset : null;
                                    int targetRetRefBase = ip->Register1 >= 0 ? frameRefBase + ip->Operand3 : -1;
                                    IMethod actualMethod = ResolveNeoCallvirtInterfaceTarget(ip, targetMethod, targetBase, mStack);

                                    if (!InvokeNeoCallTarget(actualMethod, false, targetBase, mStack, retDstPtr, targetRetRefBase, out unhandledException))
                                        return null;

                                    ip++;
                                    continue;
                                }
                            case OpCodeREnum.Callvirt:
                                {
                                    var targetMethod = AppDomain.GetMethod(ip->Operand2);
                                    if (targetMethod == null)
                                    {
                                        ip++;
                                        continue;
                                    }

                                    int callParamIdx = ip->Operand;
                                    ref var map = ref nf.NeoCallParams[callParamIdx];
                                    byte* targetBase = newEsp;
                                    CopyNeoCallArguments(ref map, frameBase, targetBase);

                                    byte* retDstPtr = ip->Register1 >= 0 ? frameBase + ip->DstOffset : null;
                                    int targetRetRefBase = ip->Register1 >= 0 ? frameRefBase + ip->Operand3 : -1;
                                    IMethod actualMethod = ResolveNeoGenericCallvirtTarget(ip, targetMethod, targetBase, mStack);

                                    if (!InvokeNeoCallTarget(actualMethod, false, targetBase, mStack, retDstPtr, targetRetRefBase, out unhandledException))
                                        return null;

                                    ip++;
                                    continue;
                                }
                            case OpCodeREnum.Ret:
                                if (retDst != null && (returnPrimitiveSize > 0 || returnRefCount > 0))
                                {
                                    if (returnRefCount == 0)
                                    {
                                        if (returnPrimitiveSize > 0)
                                            Unsafe.CopyBlock(retDst, frameBase + ip->DstOffset, (uint)returnPrimitiveSize);
                                    }
                                    else
                                    {
                                        IType returnType = method.ReturnType;
                                        bool isSingleReferenceReturn = returnType != null &&
                                            !returnType.IsPrimitive &&
                                            !returnType.IsValueType &&
                                            returnPrimitiveSize == 4 &&
                                            returnRefCount == 1;
                                        if (!isSingleReferenceReturn)
                                            throw new NotImplementedException("Neo return with value-type reference fields requires Step 12/13 return layout support.");

                                        int retSrcIdx = *(int*)(frameBase + ip->DstOffset);
                                        if (retSrcIdx >= 0)
                                        {
                                            mStack[retRefBase] = mStack[retSrcIdx];
                                            *(int*)retDst = retRefBase;
                                        }
                                        else
                                        {
                                            mStack[retRefBase] = null;
                                            *(int*)retDst = -1;
                                        }
                                    }
                                }
                                // VT-THIS-ADDR: when this ExecuteNeo invocation is a
                                // value-type ctor called from the runtime Newobj IL-VT
                                // branch (vtNewobjCallerDst != null), copy the slot-0
                                // (this) region back to the caller's dest BEFORE the
                                // mStack pop below -- the slot-0 ref entries are removed
                                // by the pop, so the ref half MUST be copied first.
                                if (vtNewobjCallerDst != null)
                                {
                                    var ctorFrameR = method.CompiledFrame;
                                    var thisSlotR = ctorFrameR.ParamInfos[0];
                                    int thisPrimOffR = thisSlotR.Offset;
                                    int thisRefOffR = thisSlotR.RefOffset;
                                    if (vtNewobjCallerPrimSize > 0)
                                        Unsafe.CopyBlock(vtNewobjCallerDst, frameBase + thisPrimOffR, (uint)vtNewobjCallerPrimSize);
                                    for (int i = 0; i < vtNewobjCallerRefCount; i++)
                                        mStack[vtNewobjCallerDstRefBase + i] = mStack[frameRefBase + thisRefOffR + i];
                                }
                                mStack.RemoveRange(frameRefBase, mStack.Count - frameRefBase);
                                returned = true;
                                continue;
                            case OpCodeREnum.Initobj:
                                t = AppDomain.GetType(ip->Operand);
                                ilType = t as ILType;
                                if (ilType != null)
                                {
                                    refCnt = 0;
                                    if (ilType.IsEnum)
                                        sz = AppDomain.GetPrimitiveSize(ilType.FieldTypes[0]);
                                    else if (ilType.IsPrimitive)
                                        sz = AppDomain.GetPrimitiveSize(ilType);
                                    else if (ilType.IsValueType)
                                    {
                                        sz = ilType.TotalPrimitiveSize;
                                        refCnt = ilType.TotalReferenceCount;
                                    }
                                    else
                                    {
                                        // Reference type initobj → write null index (-1) into the byte slot.
                                        *(int*)(frameBase + ip->DstOffset) = -1;
                                        break;
                                    }
                                    if (sz > 0)
                                        Unsafe.InitBlock(frameBase + ip->DstOffset, 0, (uint)sz);
                                    if (refCnt > 0)
                                    {
                                        // Step 12: null the in-frame VT's reference-
                                        // field mStack slots. ip->Operand3 carries
                                        // the target slot's RefOffset (stamped by
                                        // the Neo offset-lowering pass).
                                        int slotRefOffset = ip->Operand3;
                                        for (int i = 0; i < refCnt; i++)
                                            mStack[frameRefBase + slotRefOffset + i] = null;
                                    }
                                }
                                else
                                {
                                    // Step 13 / F-MAJ-1 review-fix: CLR value type Initobj.
                                    // A Neo CLR value-type LOCAL (struct OR enum) is stored as
                                    // FLAT MANAGED BYTES (Size = GetNeoValueTypeManagedSize,
                                    // RefCount = 0, isRef = false; see
                                    // JITCompiler.AllocateLocalStackSpaces CLR-VT branch under
                                    // ENABLE_NEO_MODE). Initobj therefore ZEROES the flat-bytes
                                    // region -- mirroring the IL-VT Initobj branch above and the
                                    // CLR-primitive branch below -- and does NOT touch mStack /
                                    // RefOffset (there is no ref slot: RefCount = 0). Zeroing is
                                    // correct for both pure-primitive CLR structs (default = all
                                    // fields zero) and CLR enums (default = underlying zero).
                                    CLRType clrInitType = t as CLRType;
                                    if (clrInitType == null)
                                    {
                                        // Unknown CLR type Initobj: write null index.
                                        *(int*)(frameBase + ip->DstOffset) = -1;
                                        break;
                                    }
                                    if (clrInitType.IsPrimitive)
                                    {
                                        // CLR primitive local: flat bytes; zero them.
                                        int psz = AppDomain.GetPrimitiveSize(clrInitType);
                                        if (psz > 0)
                                            Unsafe.InitBlock(frameBase + ip->DstOffset, 0, (uint)psz);
                                    }
                                    else if (!clrInitType.IsValueType)
                                    {
                                        // Reference-type CLR local Initobj: write null index.
                                        *(int*)(frameBase + ip->DstOffset) = -1;
                                    }
                                    else
                                    {
                                        // CLR struct OR CLR enum local: flat bytes; zero them.
                                        // (Pre-F-MAJ-1 this arm installed a boxed default into
                                        // mStack[frameRefBase+RefOffset]; with RefCount=0 the
                                        // stamped RefOffset is STALE -- it belongs to a
                                        // neighbouring ref slot -- so the write silently
                                        // corrupted that neighbour. The flat-bytes region is
                                        // already zeroed by frame init, but Initobj must still
                                        // re-zero it for the `v = default(T)` re-init case.)
                                        int csz = Optimizer.GetNeoValueTypeManagedSize(clrInitType.TypeForCLR);
                                        if (csz > 0)
                                            Unsafe.InitBlock(frameBase + ip->DstOffset, 0, (uint)csz);
                                    }
                                }
                                break;
                            case OpCodeREnum.Box:
                                dstRefOffset = ip->Operand3;
                                srcRefOffset = (short)ip->Operand4;
                                t = AppDomain.GetType(ip->Operand);
                                ilType = t as ILType;
                                if (ilType != null)
                                {
                                    if (ilType.IsEnum)
                                    {
                                        ins = new ILEnumTypeInstance(ilType);
                                        sz = AppDomain.GetPrimitiveSize(ilType.FieldTypes[0]);
                                        if (sz > 0)
                                        {
                                            ref byte dstP = ref MemoryMarshal.GetReference(ins.Primitives.AsSpan());
                                            Unsafe.CopyBlock(ref dstP, ref *(frameBase + ip->SrcOffset), (uint)sz);
                                        }
                                    }
                                    else if (ilType.IsPrimitive)
                                    {
                                        // Boxing a primitive IL type isn't a regular path
                                        // (compiler usually boxes CLR primitives), but handle for completeness.
                                        ins = ilType.Instantiate(false);
                                        sz = AppDomain.GetPrimitiveSize(ilType);
                                        if (sz > 0 && ins.Primitives != null)
                                        {
                                            ref byte dstP = ref MemoryMarshal.GetReference(ins.Primitives.AsSpan());
                                            Unsafe.CopyBlock(ref dstP, ref *(frameBase + ip->SrcOffset), (uint)sz);
                                        }
                                    }
                                    else if (ilType.IsValueType)
                                    {
                                        ins = ilType.Instantiate(false);
                                        CopyFrameToIL(frameBase, ip->SrcOffset, srcRefOffset,
                                                      ilType.TotalPrimitiveSize, ilType.TotalReferenceCount,
                                                      mStack, frameRefBase, ins);
                                    }
                                    else
                                    {
                                        // Boxing a reference type is a no-op: the same instance flows through.
                                        srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                        obj = srcIdx >= 0 ? mStack[srcIdx] : null;
                                        dstIdx = frameRefBase + dstRefOffset;
                                        mStack[dstIdx] = obj;
                                        *(int*)(frameBase + ip->DstOffset) = obj != null ? dstIdx : -1;
                                        break;
                                    }
                                    ins.Boxed = true;
                                    dstIdx = frameRefBase + dstRefOffset;
                                    mStack[dstIdx] = ins;
                                    *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                }
                                else
                                {
                                    // Step 13: CLR value type Box.
                                    CLRType clrBoxType = t as CLRType;
                                    if (clrBoxType == null)
                                        throw new InvalidCastException();
                                    object boxed;
                                    if (clrBoxType.IsPrimitive)
                                    {
                                        // CLR primitives are stored as flat bytes in the
                                        // frame; box reads the sized value and boxes it.
                                        boxed = NeoBoxReturnValue(clrBoxType, frameBase + ip->SrcOffset,
                                            AppDomain.GetPrimitiveSize(clrBoxType));
                                    }
                                    else
                                    {
                                        // F-MAJ-1 review-fix: a Neo CLR value-type (struct OR
                                        // enum) LOCAL is stored as FLAT MANAGED BYTES (Size =
                                        // GetNeoValueTypeManagedSize, RefCount = 0; see
                                        // JITCompiler.AllocateLocalStackSpaces CLR-VT branch
                                        // under ENABLE_NEO_MODE). Box reads the flat bytes and
                                        // boxes them via the cached typed reader
                                        // (ReadNeoValueType -> Unsafe.ReadUnaligned<T> + Box),
                                        // which yields an INDEPENDENT boxed copy, preserving
                                        // value semantics. (Pre-F-MAJ-1 this arm read a 4-byte
                                        // mStack index from the flat bytes -- garbage as an
                                        // index -> wrong object / OOB.) The Box opcode's source
                                        // is always a value-typed operand, so the flat-bytes
                                        // read is correct here unconditionally; the boxed-REF
                                        // source shape (an already-boxed struct typed as
                                        // object) never reaches Box (re-boxing an object is a
                                        // compiler no-op) -- that case is handled by the
                                        // Isinst/Castclass arms instead.
                                        int bsz = Optimizer.GetNeoValueTypeManagedSize(clrBoxType.TypeForCLR);
                                        int boxOff = ip->SrcOffset;
                                        boxed = ReadNeoValueType(clrBoxType.TypeForCLR, frameBase, ref boxOff, bsz);
                                    }
                                    dstIdx = frameRefBase + dstRefOffset;
                                    mStack[dstIdx] = boxed;
                                    *(int*)(frameBase + ip->DstOffset) = boxed != null ? dstIdx : -1;
                                }
                                break;
                            case OpCodeREnum.Ldfld_I1:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(int*)(frameBase + ip->DstOffset) = (sbyte)ins.Primitives[ip->Operand2];
                                break;
                            case OpCodeREnum.Ldfld_U1:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(int*)(frameBase + ip->DstOffset) = ins.Primitives[ip->Operand2];
                                break;
                            case OpCodeREnum.Ldfld_I2:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(int*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<short>(ref ins.Primitives[ip->Operand2]);
                                break;
                            case OpCodeREnum.Ldfld_U2:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(int*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<ushort>(ref ins.Primitives[ip->Operand2]);
                                break;
                            case OpCodeREnum.Ldfld_I4:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(int*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<int>(ref ins.Primitives[ip->Operand2]);
                                break;
                            case OpCodeREnum.Ldfld_U4:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(uint*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<uint>(ref ins.Primitives[ip->Operand2]);
                                break;
                            case OpCodeREnum.Ldfld_I8:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(long*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<long>(ref ins.Primitives[ip->Operand2]);
                                break;
                            case OpCodeREnum.Ldfld_U8:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(ulong*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<ulong>(ref ins.Primitives[ip->Operand2]);
                                break;
                            case OpCodeREnum.Ldfld_R4:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(float*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<float>(ref ins.Primitives[ip->Operand2]);
                                break;
                            case OpCodeREnum.Ldfld_R8:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                *(double*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<double>(ref ins.Primitives[ip->Operand2]);
                                break;
                            case OpCodeREnum.Ldfld_Ref:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->SrcOffset));
                                obj = ins.ManagedObjects[ip->Operand3];
                                dstIdx = frameRefBase + ip->Operand;
                                mStack[dstIdx] = obj;
                                *(int*)(frameBase + ip->DstOffset) = obj != null ? dstIdx : -1;
                                break;
                            case OpCodeREnum.Stfld_I1:
                            case OpCodeREnum.Stfld_U1:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                ins.Primitives[ip->Operand2] = *(byte*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Stfld_I2:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                Unsafe.WriteUnaligned(ref ins.Primitives[ip->Operand2], *(short*)(frameBase + ip->SrcOffset));
                                break;
                            case OpCodeREnum.Stfld_U2:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                Unsafe.WriteUnaligned(ref ins.Primitives[ip->Operand2], *(ushort*)(frameBase + ip->SrcOffset));
                                break;
                            case OpCodeREnum.Stfld_I4:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                Unsafe.WriteUnaligned(ref ins.Primitives[ip->Operand2], *(int*)(frameBase + ip->SrcOffset));
                                break;
                            case OpCodeREnum.Stfld_U4:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                Unsafe.WriteUnaligned(ref ins.Primitives[ip->Operand2], *(uint*)(frameBase + ip->SrcOffset));
                                break;
                            case OpCodeREnum.Stfld_I8:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                Unsafe.WriteUnaligned(ref ins.Primitives[ip->Operand2], *(long*)(frameBase + ip->SrcOffset));
                                break;
                            case OpCodeREnum.Stfld_U8:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                Unsafe.WriteUnaligned(ref ins.Primitives[ip->Operand2], *(ulong*)(frameBase + ip->SrcOffset));
                                break;
                            case OpCodeREnum.Stfld_R4:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                Unsafe.WriteUnaligned(ref ins.Primitives[ip->Operand2], *(float*)(frameBase + ip->SrcOffset));
                                break;
                            case OpCodeREnum.Stfld_R8:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                Unsafe.WriteUnaligned(ref ins.Primitives[ip->Operand2], *(double*)(frameBase + ip->SrcOffset));
                                break;
                            case OpCodeREnum.Stfld_Ref:
                                ins = GetNeoILInstance(mStack, *(int*)(frameBase + ip->DstOffset));
                                srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                ins.ManagedObjects[ip->Operand3] = srcIdx >= 0 ? mStack[srcIdx] : null;
                                break;
                            // ---- Step 12: in-frame value-type inline field access ----
                            // These index the frame byte region directly. Encoding:
                            //   Ldfld_*_Inline: DstOffset = dest temp byte offset;
                            //                   SrcOffset = owning VT slot byte offset;
                            //                   Operand2  = field PrimitiveOffset within the VT.
                            //   Stfld_*_Inline: DstOffset = owning VT slot byte offset;
                            //                   SrcOffset = value temp byte offset;
                            //                   Operand2  = field PrimitiveOffset within the VT.
                            // The owning VT slot holds raw bytes (NOT an mStack index),
                            // so there is no GetNeoILInstance / no branch.
                            case OpCodeREnum.Ldfld_I1_Inline:
                                *(int*)(frameBase + ip->DstOffset) =
                                    *(sbyte*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_U1_Inline:
                                *(int*)(frameBase + ip->DstOffset) =
                                    *(byte*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_I2_Inline:
                                *(int*)(frameBase + ip->DstOffset) =
                                    *(short*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_U2_Inline:
                                *(int*)(frameBase + ip->DstOffset) =
                                    *(ushort*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_I4_Inline:
                                *(int*)(frameBase + ip->DstOffset) =
                                    *(int*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_U4_Inline:
                                *(uint*)(frameBase + ip->DstOffset) =
                                    *(uint*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_I8_Inline:
                                *(long*)(frameBase + ip->DstOffset) =
                                    *(long*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_U8_Inline:
                                *(ulong*)(frameBase + ip->DstOffset) =
                                    *(ulong*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_R4_Inline:
                                *(float*)(frameBase + ip->DstOffset) =
                                    *(float*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Ldfld_R8_Inline:
                                *(double*)(frameBase + ip->DstOffset) =
                                    *(double*)(frameBase + ip->SrcOffset + ip->Operand2);
                                break;
                            case OpCodeREnum.Stfld_I1_Inline:
                            case OpCodeREnum.Stfld_U1_Inline:
                                *(byte*)(frameBase + ip->DstOffset + ip->Operand2) =
                                    *(byte*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Stfld_I2_Inline:
                            case OpCodeREnum.Stfld_U2_Inline:
                                *(short*)(frameBase + ip->DstOffset + ip->Operand2) =
                                    *(short*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Stfld_I4_Inline:
                            case OpCodeREnum.Stfld_U4_Inline:
                                *(int*)(frameBase + ip->DstOffset + ip->Operand2) =
                                    *(int*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Stfld_I8_Inline:
                            case OpCodeREnum.Stfld_U8_Inline:
                                *(long*)(frameBase + ip->DstOffset + ip->Operand2) =
                                    *(long*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Stfld_R4_Inline:
                                *(float*)(frameBase + ip->DstOffset + ip->Operand2) =
                                    *(float*)(frameBase + ip->SrcOffset);
                                break;
                            case OpCodeREnum.Stfld_R8_Inline:
                                *(double*)(frameBase + ip->DstOffset + ip->Operand2) =
                                    *(double*)(frameBase + ip->SrcOffset);
                                break;
                            // Ref inline variants operate on the frame's mStack
                            // reference region. Encoding (stamped by the Neo
                            // offset-lowering pass):
                            //   Ldfld_Ref_Inline: Operand  = source field absolute
                            //                                frame-ref index
                            //                                (owningSlot.RefOffset
                            //                                 + field.ReferenceOffset);
                            //                     Operand4 = dest temp RefOffset.
                            //   Stfld_Ref_Inline: Operand  = dest field absolute
                            //                                frame-ref index
                            //                                (owningSlot.RefOffset
                            //                                 + field.ReferenceOffset).
                            //                     SrcOffset = value temp byte offset.
                            case OpCodeREnum.Ldfld_Ref_Inline:
                                obj = mStack[frameRefBase + ip->Operand];
                                dstIdx = frameRefBase + ip->Operand4;
                                mStack[dstIdx] = obj;
                                *(int*)(frameBase + ip->DstOffset) = obj != null ? dstIdx : -1;
                                break;
                            case OpCodeREnum.Stfld_Ref_Inline:
                                srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                mStack[frameRefBase + ip->Operand] =
                                    srcIdx >= 0 ? mStack[srcIdx] : null;
                                break;
                            case OpCodeREnum.Unbox:
                            case OpCodeREnum.Unbox_Any:
                                dstRefOffset = ip->Operand3;
                                t = AppDomain.GetType(ip->Operand);
                                srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                if (srcIdx < 0)
                                    throw new NullReferenceException();
                                obj = mStack[srcIdx];
                                if (obj == null)
                                    throw new NullReferenceException();
                                ilType = t as ILType;
                                if (ilType != null)
                                {
                                    ins = obj as ILTypeInstance;
                                    if (ins == null)
                                        throw new InvalidCastException();
                                    if (ilType.IsEnum)
                                    {
                                        sz = AppDomain.GetPrimitiveSize(ilType.FieldTypes[0]);
                                        if (sz > 0)
                                        {
                                            ref byte srcP = ref MemoryMarshal.GetReference(ins.Primitives.AsSpan());
                                            Unsafe.CopyBlock(ref *(frameBase + ip->DstOffset), ref srcP, (uint)sz);
                                        }
                                    }
                                    else if (ilType.IsPrimitive)
                                    {
                                        sz = AppDomain.GetPrimitiveSize(ilType);
                                        if (sz > 0 && ins.Primitives != null)
                                        {
                                            ref byte srcP = ref MemoryMarshal.GetReference(ins.Primitives.AsSpan());
                                            Unsafe.CopyBlock(ref *(frameBase + ip->DstOffset), ref srcP, (uint)sz);
                                        }
                                    }
                                    else if (ilType.IsValueType)
                                    {
                                        CopyILToFrame(ins,
                                                      frameBase, ip->DstOffset, dstRefOffset,
                                                      ilType.TotalPrimitiveSize, ilType.TotalReferenceCount,
                                                      mStack, frameRefBase);
                                    }
                                    else
                                    {
                                        throw new InvalidCastException();
                                    }
                                }
                                else
                                {
                                    // Step 13: CLR value type Unbox / Unbox_Any.
                                    CLRType clrUnboxType = t as CLRType;
                                    if (clrUnboxType == null)
                                        throw new InvalidCastException();
                                    // obj (the boxed source) was fetched above; null was
                                    // already turned into NullReferenceException.
                                    if (clrUnboxType.IsPrimitive)
                                    {
                                        // CLR primitives unbox into the dest flat-bytes slot
                                        // by value.
                                        NeoWritePrimitiveToFrame(obj, frameBase + ip->DstOffset);
                                    }
                                    else
                                    {
                                        // F-MAJ-1 review-fix: a Neo CLR value-type (struct OR
                                        // enum) LOCAL destination is FLAT MANAGED BYTES (Size =
                                        // GetNeoValueTypeManagedSize, RefCount = 0; see
                                        // JITCompiler.AllocateLocalStackSpaces CLR-VT branch
                                        // under ENABLE_NEO_MODE). Unbox writes the boxed
                                        // struct's flat bytes into the dest via WriteNeoValueType
                                        // (an independent value copy -- value semantics
                                        // preserved). (Pre-F-MAJ-1 this arm installed a boxed
                                        // clone into mStack[frameRefBase+dstRefOffset] and
                                        // wrote the index into the flat bytes; with RefCount=0
                                        // dstRefOffset is STALE -> corruption.) This is the
                                        // inverse of the M2 Box fix. The dest of unbox.any is
                                        // always a value-typed local, so the flat-bytes write is
                                        // correct here unconditionally.
                                        int unbxSz = Optimizer.GetNeoValueTypeManagedSize(clrUnboxType.TypeForCLR);
                                        WriteNeoValueType(obj, frameBase + ip->DstOffset, unbxSz);
                                    }
                                }
                                break;
                            // Step 15: isinst (C# `is` / `as`). The checked value is
                            // always a reference (Box produced it, or it was a reference
                            // local/field). Resolve the target type statically via the
                            // type-token operand; test assignability; keep the original
                            // reference on success else write null. NEVER throws on mismatch.
                            case OpCodeREnum.Isinst:
                                {
                                    IType isinstType = AppDomain.GetType(ip->Operand);
                                    if (isinstType == null)
                                        throw new NullReferenceException();
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    obj = srcIdx >= 0 ? mStack[srcIdx] : null;
                                    object isinstResult = null;
                                    if (obj != null)
                                    {
                                        if (obj is ILTypeInstance isinstILI)
                                            isinstResult = isinstILI.CanAssignTo(isinstType) ? obj : null;
                                        else
                                            isinstResult = isinstType.TypeForCLR.IsAssignableFrom(obj.GetType()) ? obj : null;
                                    }
                                    if (isinstResult != null)
                                    {
                                        dstIdx = frameRefBase + ip->Operand3;
                                        mStack[dstIdx] = isinstResult;
                                        *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                    }
                                    else
                                        *(int*)(frameBase + ip->DstOffset) = -1;
                                }
                                break;
                            // Step 15: castclass (C# explicit `(T)obj` cast). Same
                            // assignability dispatch as isinst, but a failed check throws
                            // InvalidCastException; a null source passes through as null.
                            case OpCodeREnum.Castclass:
                                {
                                    IType castType = AppDomain.GetType(ip->Operand);
                                    if (castType == null)
                                        throw new NullReferenceException();
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    obj = srcIdx >= 0 ? mStack[srcIdx] : null;
                                    object castResult;
                                    if (obj == null)
                                    {
                                        castResult = null;
                                    }
                                    else if (obj is ILTypeInstance castILI)
                                    {
                                        if (!castILI.CanAssignTo(castType))
                                            throw new InvalidCastException(string.Format(
                                                "Cannot Cast {0} to {1}",
                                                castILI.Type.FullName, castType.FullName));
                                        castResult = obj;
                                    }
                                    else
                                    {
                                        if (!castType.TypeForCLR.IsAssignableFrom(obj.GetType()))
                                            throw new InvalidCastException(string.Format(
                                                "Cannot Cast {0} to {1}",
                                                obj.GetType().FullName, castType.FullName));
                                        castResult = obj;
                                    }
                                    if (castResult != null)
                                    {
                                        dstIdx = frameRefBase + ip->Operand3;
                                        mStack[dstIdx] = castResult;
                                        *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                    }
                                    else
                                        *(int*)(frameBase + ip->DstOffset) = -1;
                                }
                                break;
                            // ---- Step 16: array element access ----
                            // Encoding after LowerNeoOffsets:
                            //   Newarr: DstOffset=dest array byte off, SrcOffset=count
                            //           byte off, Operand=element-type token,
                            //           Operand3=dest array ref slot.
                            //   Ldlen:  DstOffset=dest(int), SrcOffset=array byte off.
                            //   Ldelem_*: DstOffset=dest, SrcOffset=array,
                            //             Operand4=index byte off, Operand3=dest ref slot.
                            //   Stelem_*: DstOffset=array, SrcOffset=index,
                            //             Operand4=value byte off, Operand3=value ref slot.
                            // Three array kinds (matches Legacy ExecuteR @4879-5304):
                            //   (a) CLR primitive array: typed CLR indexer.
                            //   (b) IL reference-type array: ILTypeInstance[] (or CLR
                            //       object[]); element is an mStack-resident object.
                            //   (c) IL value-type array: ILTypeInstance[] with every slot
                            //       pre-instantiated; element is a heap ILTypeInstance,
                            //       copied via CopyILToFrame / CopyFrameToIL (Step 12/13).
                            // Bounds: the CLR typed indexer throws IndexOutOfRangeException
                            // natively; null array -> NullReferenceException (surfaced by
                            // the Step 14 outer try/catch). No explicit bounds check.
                            case OpCodeREnum.Newarr:
                                {
                                    int count = *(int*)(frameBase + ip->SrcOffset);
                                    t = AppDomain.GetType(ip->Operand);
                                    object arr = null;
                                    if (t != null)
                                    {
                                        if (t.TypeForCLR != typeof(ILTypeInstance))
                                        {
                                            if (t is CLRType ct)
                                                arr = ct.CreateArrayInstance(count);
                                            else
                                                arr = Array.CreateInstance(t.TypeForCLR, count);
                                            // Register the array's CLR type, as Legacy does.
                                            AppDomain.GetType(arr.GetType());
                                        }
                                        else
                                        {
                                            var ilArr = new ILTypeInstance[count];
                                            ilType = (ILType)t;
                                            if (ilType.IsValueType)
                                            {
                                                for (int i = 0; i < count; i++)
                                                    ilArr[i] = ilType.Instantiate(true);
                                            }
                                            arr = ilArr;
                                        }
                                    }
                                    if (arr != null)
                                    {
                                        dstIdx = frameRefBase + ip->Operand3;
                                        mStack[dstIdx] = arr;
                                        *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                    }
                                    else
                                        *(int*)(frameBase + ip->DstOffset) = -1;
                                }
                                break;
                            case OpCodeREnum.Ldlen:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0)
                                        throw new NullReferenceException();
                                    Array lenArr = (Array)mStack[srcIdx];
                                    *(int*)(frameBase + ip->DstOffset) = lenArr.Length;
                                }
                                break;
                            case OpCodeREnum.Ldelem_I1:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    Array la = (Array)mStack[srcIdx];
                                    // bool[] vs sbyte[] disambiguation (Legacy @5161-5179).
                                    if (la is bool[] ba) *(int*)(frameBase + ip->DstOffset) = ba[li] ? 1 : 0;
                                    else *(int*)(frameBase + ip->DstOffset) = ((sbyte[])la)[li];
                                }
                                break;
                            case OpCodeREnum.Ldelem_U1:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    Array la = (Array)mStack[srcIdx];
                                    // byte[] vs bool[] disambiguation (Legacy @5181-5200).
                                    if (la is byte[] bya) *(int*)(frameBase + ip->DstOffset) = bya[li];
                                    else *(int*)(frameBase + ip->DstOffset) = ((bool[])la)[li] ? 1 : 0;
                                }
                                break;
                            case OpCodeREnum.Ldelem_I2:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    Array la = (Array)mStack[srcIdx];
                                    // short[] vs char[] disambiguation (Legacy @5201-5220).
                                    if (la is short[] sa) *(int*)(frameBase + ip->DstOffset) = sa[li];
                                    else *(int*)(frameBase + ip->DstOffset) = ((char[])la)[li];
                                }
                                break;
                            case OpCodeREnum.Ldelem_U2:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    Array la = (Array)mStack[srcIdx];
                                    // ushort[] vs char[] disambiguation (Legacy @5221-5240).
                                    if (la is ushort[] usa) *(int*)(frameBase + ip->DstOffset) = usa[li];
                                    else *(int*)(frameBase + ip->DstOffset) = ((char[])la)[li];
                                }
                                break;
                            case OpCodeREnum.Ldelem_I4:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    Array la = (Array)mStack[srcIdx];
                                    if (la is int[] ia) *(int*)(frameBase + ip->DstOffset) = ia[li];
                                    else *(int*)(frameBase + ip->DstOffset) = (int)((uint[])la)[li];
                                }
                                break;
                            case OpCodeREnum.Ldelem_U4:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    *(uint*)(frameBase + ip->DstOffset) = ((uint[])(Array)mStack[srcIdx])[li];
                                }
                                break;
                            case OpCodeREnum.Ldelem_I8:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    Array la = (Array)mStack[srcIdx];
                                    if (la is long[] lla) *(long*)(frameBase + ip->DstOffset) = lla[li];
                                    else *(long*)(frameBase + ip->DstOffset) = (long)((ulong[])la)[li];
                                }
                                break;
                            case OpCodeREnum.Ldelem_R4:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    *(float*)(frameBase + ip->DstOffset) = ((float[])(Array)mStack[srcIdx])[li];
                                }
                                break;
                            case OpCodeREnum.Ldelem_R8:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    *(double*)(frameBase + ip->DstOffset) = ((double[])(Array)mStack[srcIdx])[li];
                                }
                                break;
                            case OpCodeREnum.Ldelem_Ref:
                            case OpCodeREnum.Ldelem_Any:
                                {
                                    srcIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int li = *(int*)(frameBase + ip->Operand4);
                                    Array la = (Array)mStack[srcIdx];
                                    object elem;
                                    if (la is ILTypeInstance[] ilArr)
                                        elem = ilArr[li];
                                    else
                                        elem = la.GetValue(li);
                                    if (elem is CrossBindingAdaptorType cbat)
                                        elem = cbat.ILInstance;
                                    if (elem is ILTypeInstance elemIns
                                        && !(elemIns is DelegateAdapter)
                                        && elemIns.Type.IsValueType
                                        && !elemIns.Boxed)
                                    {
                                        // IL value-type element: copy primitive bytes +
                                        // ref slots into the dest frame region (Step 12/13
                                        // CopyILToFrame helper).
                                        CopyILToFrame(elemIns,
                                            frameBase, ip->DstOffset, ip->Operand3,
                                            elemIns.Type.TotalPrimitiveSize,
                                            elemIns.Type.TotalReferenceCount,
                                            mStack, frameRefBase);
                                    }
                                    else if (elem != null)
                                    {
                                        // Reference element: store the object on the dest
                                        // ref slot, write its mStack index to the dest slot.
                                        dstIdx = frameRefBase + ip->Operand3;
                                        mStack[dstIdx] = elem;
                                        *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                    }
                                    else
                                        *(int*)(frameBase + ip->DstOffset) = -1;
                                }
                                break;
                            case OpCodeREnum.Stelem_I1:
                                {
                                    srcIdx = *(int*)(frameBase + ip->DstOffset); // array
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int si = *(int*)(frameBase + ip->SrcOffset); // index
                                    byte val1 = *(byte*)(frameBase + ip->Operand4); // value
                                    Array sa = (Array)mStack[srcIdx];
                                    if (sa is byte[] sba) sba[si] = val1;
                                    else if (sa is bool[] sboa) sboa[si] = val1 != 0;
                                    else ((sbyte[])sa)[si] = (sbyte)val1;
                                }
                                break;
                            case OpCodeREnum.Stelem_I2:
                                {
                                    srcIdx = *(int*)(frameBase + ip->DstOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int si = *(int*)(frameBase + ip->SrcOffset);
                                    short val2 = *(short*)(frameBase + ip->Operand4);
                                    Array sa = (Array)mStack[srcIdx];
                                    if (sa is short[] ssa) ssa[si] = val2;
                                    else if (sa is ushort[] susa) susa[si] = (ushort)val2;
                                    else ((char[])sa)[si] = (char)val2;
                                }
                                break;
                            case OpCodeREnum.Stelem_I4:
                                {
                                    srcIdx = *(int*)(frameBase + ip->DstOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int si = *(int*)(frameBase + ip->SrcOffset);
                                    int val4 = *(int*)(frameBase + ip->Operand4);
                                    Array sa = (Array)mStack[srcIdx];
                                    if (sa is int[] sia) sia[si] = val4;
                                    else ((uint[])sa)[si] = (uint)val4;
                                }
                                break;
                            case OpCodeREnum.Stelem_I8:
                                {
                                    srcIdx = *(int*)(frameBase + ip->DstOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int si = *(int*)(frameBase + ip->SrcOffset);
                                    long val8 = *(long*)(frameBase + ip->Operand4);
                                    Array sa = (Array)mStack[srcIdx];
                                    if (sa is long[] slla) slla[si] = val8;
                                    else ((ulong[])sa)[si] = (ulong)val8;
                                }
                                break;
                            case OpCodeREnum.Stelem_R4:
                                {
                                    srcIdx = *(int*)(frameBase + ip->DstOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int si = *(int*)(frameBase + ip->SrcOffset);
                                    float valr4 = *(float*)(frameBase + ip->Operand4);
                                    ((float[])(Array)mStack[srcIdx])[si] = valr4;
                                }
                                break;
                            case OpCodeREnum.Stelem_R8:
                                {
                                    srcIdx = *(int*)(frameBase + ip->DstOffset);
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int si = *(int*)(frameBase + ip->SrcOffset);
                                    double valr8 = *(double*)(frameBase + ip->Operand4);
                                    ((double[])(Array)mStack[srcIdx])[si] = valr8;
                                }
                                break;
                            case OpCodeREnum.Stelem_Ref:
                            case OpCodeREnum.Stelem_Any:
                                {
                                    srcIdx = *(int*)(frameBase + ip->DstOffset); // array
                                    if (srcIdx < 0) throw new NullReferenceException();
                                    int si = *(int*)(frameBase + ip->SrcOffset); // index
                                    Array sa = (Array)mStack[srcIdx];
                                    if (sa is ILTypeInstance[] dstIlArr)
                                    {
                                        ILTypeInstance slotIns = dstIlArr[si];
                                        if (slotIns != null && slotIns.Type.IsValueType && !slotIns.Boxed)
                                        {
                                            // IL value-type element: copy the in-frame value
                                            // (primitive + ref slots) into the pre-instantiated
                                            // element instance (Step 12/12b CopyFrameToIL).
                                            CopyFrameToIL(frameBase, ip->Operand4, ip->Operand3,
                                                slotIns.Type.TotalPrimitiveSize,
                                                slotIns.Type.TotalReferenceCount,
                                                mStack, frameRefBase, slotIns);
                                        }
                                        else
                                        {
                                            // Reference element: read the value's mStack
                                            // object and store it into the array slot.
                                            int vIdx = *(int*)(frameBase + ip->Operand4);
                                            dstIlArr[si] = vIdx >= 0 ? (ILTypeInstance)mStack[vIdx] : null;
                                        }
                                    }
                                    else
                                    {
                                        // CLR object array: the value reaching Stelem_Ref /
                                        // Stelem_Any into an object[] is a reference (C#
                                        // boxes value types before storing into object[]),
                                        // so read its mStack object and Array.SetValue it.
                                        int vIdx = *(int*)(frameBase + ip->Operand4);
                                        object vObj = vIdx >= 0 ? mStack[vIdx] : null;
                                        sa.SetValue(vObj, si);
                                    }
                                }
                                break;
                            // ---- Step 17: byref store/load-indirect dispatch ----
                            // Each arm decodes the 8-byte Ref Slot address operand
                            // (ip->DstOffset for Stind/Stobj, ip->SrcOffset for
                            // Ldind/Ldobj) = (objectIndex:int, offset:int) and
                            // dispatches: objectIndex == -1 -> frame-native byte
                            // region; objectIndex >= 0 with an ILTypeInstance ->
                            // pinned Primitives (ref il.Primitives[off]); a CLR
                            // object target is DEFERRED (Step-17-tagged NIE).
                            // Stind_* / Stobj: DstOffset = address, SrcOffset =
                            // value. Ldind_* / Ldobj: DstOffset = dest, SrcOffset
                            // = address.
                            case OpCodeREnum.Stind_I1:
                                {
                                    int objIdx = *(int*)(frameBase + ip->DstOffset + 0);
                                    int off = *(int*)(frameBase + ip->DstOffset + 4);
                                    sbyte v = *(sbyte*)(frameBase + ip->SrcOffset);
                                    if (objIdx == -1) *(sbyte*)(frameBase + off) = v;
                                    else { ins = GetNeoILInstance(mStack, objIdx); ins.Primitives[off] = (byte)v; }
                                }
                                break;
                            case OpCodeREnum.Stind_I2:
                                {
                                    int objIdx = *(int*)(frameBase + ip->DstOffset + 0);
                                    int off = *(int*)(frameBase + ip->DstOffset + 4);
                                    short v = *(short*)(frameBase + ip->SrcOffset);
                                    if (objIdx == -1) *(short*)(frameBase + off) = v;
                                    else { ins = GetNeoILInstance(mStack, objIdx); Unsafe.WriteUnaligned(ref ins.Primitives[off], v); }
                                }
                                break;
                            case OpCodeREnum.Stind_I4:
                                {
                                    int objIdx = *(int*)(frameBase + ip->DstOffset + 0);
                                    int off = *(int*)(frameBase + ip->DstOffset + 4);
                                    int v = *(int*)(frameBase + ip->SrcOffset);
                                    if (objIdx == -1) *(int*)(frameBase + off) = v;
                                    else { ins = GetNeoILInstance(mStack, objIdx); Unsafe.WriteUnaligned(ref ins.Primitives[off], v); }
                                }
                                break;
                            case OpCodeREnum.Stind_I8:
                                {
                                    int objIdx = *(int*)(frameBase + ip->DstOffset + 0);
                                    int off = *(int*)(frameBase + ip->DstOffset + 4);
                                    long v = *(long*)(frameBase + ip->SrcOffset);
                                    if (objIdx == -1) *(long*)(frameBase + off) = v;
                                    else { ins = GetNeoILInstance(mStack, objIdx); Unsafe.WriteUnaligned(ref ins.Primitives[off], v); }
                                }
                                break;
                            case OpCodeREnum.Stind_R4:
                                {
                                    int objIdx = *(int*)(frameBase + ip->DstOffset + 0);
                                    int off = *(int*)(frameBase + ip->DstOffset + 4);
                                    float v = *(float*)(frameBase + ip->SrcOffset);
                                    if (objIdx == -1) *(float*)(frameBase + off) = v;
                                    else { ins = GetNeoILInstance(mStack, objIdx); Unsafe.WriteUnaligned(ref ins.Primitives[off], v); }
                                }
                                break;
                            case OpCodeREnum.Stind_R8:
                                {
                                    int objIdx = *(int*)(frameBase + ip->DstOffset + 0);
                                    int off = *(int*)(frameBase + ip->DstOffset + 4);
                                    double v = *(double*)(frameBase + ip->SrcOffset);
                                    if (objIdx == -1) *(double*)(frameBase + off) = v;
                                    else { ins = GetNeoILInstance(mStack, objIdx); Unsafe.WriteUnaligned(ref ins.Primitives[off], v); }
                                }
                                break;
                            case OpCodeREnum.Stind_I:
                                // native-int store: same width as I4 on this VM.
                                goto case OpCodeREnum.Stind_I4;
                            case OpCodeREnum.Ldind_I1:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    if (objIdx == -1) *(int*)(frameBase + ip->DstOffset) = *(sbyte*)(frameBase + off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); *(int*)(frameBase + ip->DstOffset) = ins.Primitives[off]; }
                                }
                                break;
                            case OpCodeREnum.Ldind_U1:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    if (objIdx == -1) *(int*)(frameBase + ip->DstOffset) = *(byte*)(frameBase + off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); *(int*)(frameBase + ip->DstOffset) = ins.Primitives[off]; }
                                }
                                break;
                            case OpCodeREnum.Ldind_I2:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    int v;
                                    if (objIdx == -1) v = *(short*)(frameBase + off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); v = Unsafe.ReadUnaligned<short>(ref ins.Primitives[off]); }
                                    *(int*)(frameBase + ip->DstOffset) = v;
                                }
                                break;
                            case OpCodeREnum.Ldind_U2:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    int v;
                                    if (objIdx == -1) v = *(ushort*)(frameBase + off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); v = Unsafe.ReadUnaligned<ushort>(ref ins.Primitives[off]); }
                                    *(int*)(frameBase + ip->DstOffset) = v;
                                }
                                break;
                            case OpCodeREnum.Ldind_I4:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    if (objIdx == -1) *(int*)(frameBase + ip->DstOffset) = *(int*)(frameBase + off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); *(int*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<int>(ref ins.Primitives[off]); }
                                }
                                break;
                            case OpCodeREnum.Ldind_U4:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    uint v;
                                    if (objIdx == -1) v = *(uint*)(frameBase + off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); v = Unsafe.ReadUnaligned<uint>(ref ins.Primitives[off]); }
                                    *(uint*)(frameBase + ip->DstOffset) = v;
                                }
                                break;
                            case OpCodeREnum.Ldind_I8:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    if (objIdx == -1) *(long*)(frameBase + ip->DstOffset) = *(long*)(frameBase + off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); *(long*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<long>(ref ins.Primitives[off]); }
                                }
                                break;
                            case OpCodeREnum.Ldind_R4:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    if (objIdx == -1) *(float*)(frameBase + ip->DstOffset) = *(float*)(frameBase + off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); *(float*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<float>(ref ins.Primitives[off]); }
                                }
                                break;
                            case OpCodeREnum.Ldind_R8:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    if (objIdx == -1) *(double*)(frameBase + ip->DstOffset) = *(double*)(frameBase + off);
                                    else { ins = GetNeoILInstance(mStack, objIdx); *(double*)(frameBase + ip->DstOffset) = Unsafe.ReadUnaligned<double>(ref ins.Primitives[off]); }
                                }
                                break;
                            case OpCodeREnum.Ldind_I:
                                // native-int load: same width as I4 on this VM.
                                goto case OpCodeREnum.Ldind_I4;
                            case OpCodeREnum.Stind_Ref:
                                {
                                    // Store a managed reference through the pointer.
                                    // Frame-native target: write the value's mStack
                                    // index as a 4-byte slot at the offset (a ref-
                                    // typed frame slot). Heap-IL ref field: write
                                    // the ManagedObjects entry (off is the field's
                                    // reference offset, stamped by Ldflda via the
                                    // Operand3 marker -- not yet wired, so NIE for
                                    // the heap-ref sub-case this step).
                                    int objIdx = *(int*)(frameBase + ip->DstOffset + 0);
                                    int off = *(int*)(frameBase + ip->DstOffset + 4);
                                    int vIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (objIdx == -1)
                                    {
                                        *(int*)(frameBase + off) = vIdx;
                                    }
                                    else
                                    {
                                        throw new NotImplementedException(
                                            "Step 17: stind_ref on a heap IL ref field is deferred (ref-field Ref Slot encoding)");
                                    }
                                }
                                break;
                            case OpCodeREnum.Ldind_Ref:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    if (objIdx == -1)
                                    {
                                        // Frame-native: read the mStack index at the
                                        // offset and materialize the object into the
                                        // dest ref slot.
                                        srcIdx = *(int*)(frameBase + off);
                                        if (srcIdx >= 0)
                                        {
                                            dstIdx = frameRefBase + ip->Operand3;
                                            mStack[dstIdx] = mStack[srcIdx];
                                            *(int*)(frameBase + ip->DstOffset) = dstIdx;
                                        }
                                        else
                                            *(int*)(frameBase + ip->DstOffset) = -1;
                                    }
                                    else
                                    {
                                        throw new NotImplementedException(
                                            "Step 17: ldind_ref on a heap IL ref field is deferred (ref-field Ref Slot encoding)");
                                    }
                                }
                                break;
                            // Stobj / Ldobj: value-type-sized copy through the
                            // pointer. Operand = type token (sized via the domain).
                            case OpCodeREnum.Stobj:
                                {
                                    int objIdx = *(int*)(frameBase + ip->DstOffset + 0);
                                    int off = *(int*)(frameBase + ip->DstOffset + 4);
                                    t = AppDomain.GetType(ip->Operand);
                                    ilType = t as ILType;
                                    int primSize = ilType != null ? ilType.TotalPrimitiveSize : AppDomain.GetPrimitiveSize(t);
                                    if (objIdx == -1)
                                    {
                                        Unsafe.CopyBlock(frameBase + off, frameBase + ip->SrcOffset, (uint)primSize);
                                    }
                                    else
                                    {
                                        ins = GetNeoILInstance(mStack, objIdx);
                                        // IL value-type field: copy primitives into
                                        // Primitives and ref slots into ManagedObjects.
                                        ref byte dstP = ref ins.Primitives[off];
                                        Unsafe.CopyBlock(ref dstP, ref *(frameBase + ip->SrcOffset), (uint)primSize);
                                    }
                                }
                                break;
                            case OpCodeREnum.Ldobj:
                                {
                                    int objIdx = *(int*)(frameBase + ip->SrcOffset + 0);
                                    int off = *(int*)(frameBase + ip->SrcOffset + 4);
                                    t = AppDomain.GetType(ip->Operand);
                                    ilType = t as ILType;
                                    int primSize = ilType != null ? ilType.TotalPrimitiveSize : AppDomain.GetPrimitiveSize(t);
                                    if (objIdx == -1)
                                    {
                                        Unsafe.CopyBlock(frameBase + ip->DstOffset, frameBase + off, (uint)primSize);
                                    }
                                    else
                                    {
                                        ins = GetNeoILInstance(mStack, objIdx);
                                        ref byte srcP = ref ins.Primitives[off];
                                        Unsafe.CopyBlock(ref *(frameBase + ip->DstOffset), ref srcP, (uint)primSize);
                                    }
                                }
                                break;
                            // Step 17 (D-LDELEMA): produce a Ref Slot addressing
                            // array element `elementIdx`. The green target is an
                            // IL value-type array (ILTypeInstance[] with pre-
                            // instantiated elements, the Step 16 representation):
                            // resolve the element ILTypeInstance and park it on
                            // mStack, then encode (mStackIdx, 0) so the consumers
                            // (stind/ldind) hit the standard IL-instance Primitives
                            // path on that element instance. The element reference
                            // is rooted by mStack for the pointer's lifetime (the
                            // frame's mStack range is torn down on method exit).
                            case OpCodeREnum.Ldelema:
                                {
                                    int arrIdx = *(int*)(frameBase + ip->SrcOffset);
                                    if (arrIdx < 0) throw new NullReferenceException();
                                    int elementIdx = *(int*)(frameBase + ip->Operand4);
                                    Array la = (Array)mStack[arrIdx];
                                    if (la is ILTypeInstance[] ilArr)
                                    {
                                        ILTypeInstance elem = ilArr[elementIdx];
                                        if (elem == null)
                                            throw new NullReferenceException();
                                        int elemMStackIdx = mStack.Count;
                                        mStack.Add(elem);
                                        *(int*)(frameBase + ip->DstOffset + 0) = elemMStackIdx;
                                        *(int*)(frameBase + ip->DstOffset + 4) = 0;
                                    }
                                    else
                                    {
                                        throw new NotImplementedException(
                                            "Step 17: ldelema on a CLR primitive array is deferred (use direct indexing)");
                                    }
                                }
                                break;
                            // Step 14: exception handling. Throw reads the exception
                            // object from its register-1 ref slot (Register1 is a raw
                            // register index -- Throw is NOT lowered by LowerNeoOffsets,
                            // so resolve it via localInfos, exactly like Ret reads its
                            // source). The frame byte at localInfos[Register1].Offset
                            // holds the mStack index of the exception object.
                            case OpCodeREnum.Throw:
                                {
                                    int exByteOff = localInfos[ip->Register1].Offset;
                                    int exIdx = *(int*)(frameBase + exByteOff);
                                    Exception ex = GetNeoException(mStack, exIdx);
                                    throw ex;
                                }
                            // Step 14: rethrow the in-flight exception captured by the
                            // outer catch into lastCaughtEx.
                            case OpCodeREnum.Rethrow:
                                throw lastCaughtEx;
                            // Step 14: Leave / Leave_S. Route through any enclosing
                            // finally whose try range straddles the leave boundary
                            // (port of Legacy ILIntepreter.Register.cs:2765-2783).
                            // ip->Operand is the leave target byte offset.
                            case OpCodeREnum.Leave:
                            case OpCodeREnum.Leave_S:
                                {
                                    if (ehs != null)
                                    {
                                        int addr = (int)(ip - ptr);
                                        var eh = FindExceptionHandlerByBranchTarget(addr, ip->Operand, ehs);
                                        if (eh != null)
                                        {
                                            finallyEndAddress = ip->Operand;
                                            ip = ptr + eh.HandlerStart;
                                            continue;
                                        }
                                    }
                                    ip = ptr + ip->Operand;
                                    continue;
                                }
                            // Step 14: Endfinally. If the finally was entered for an
                            // in-flight exception (finallyEndAddress < 0 sentinel set
                            // in HandleException), re-throw lastCaughtEx so the search
                            // resumes. Otherwise resume at the recorded leave target,
                            // routing through any further enclosing finally. Port of
                            // Legacy ILIntepreter.Register.cs:2785-2806.
                            case OpCodeREnum.Endfinally:
                                {
                                    if (finallyEndAddress < 0)
                                    {
                                        finallyEndAddress = 0;
                                        throw lastCaughtEx;
                                    }
                                    int addr = (int)(ip - ptr);
                                    var eh = FindExceptionHandlerByBranchTarget(addr, finallyEndAddress, ehs);
                                    if (eh != null)
                                    {
                                        ip = ptr + eh.HandlerStart;
                                        continue;
                                    }
                                    ip = ptr + finallyEndAddress;
                                    finallyEndAddress = 0;
                                    continue;
                                }
                            // IL filter blocks (filter / endfilter): out of scope for
                            // Step 14 (rare in C#). Remains a Step-tagged NIE.
                            case OpCodeREnum.Endfilter:
                                throw new NotImplementedException("Neo: IL filter blocks (endfilter) are not implemented (Step 14, out of scope)");
                            // Step 17 (D-CONSTRAINED): the JIT moves `Constrained`
                            // to AFTER the callvirt (which it flags with
                            // Operand4 == 1) and stamps the constrained type token
                            // in Operand. Full constrained.-on-value-type dispatch
                            // requires the callvirt to accept a byref `this` (the
                            // struct's managed address produced by ldarga/ldloca)
                            // and dispatch to the constrained type's concrete
                            // override -- that callvirt-byref-this work is the
                            // deferred sub-case (Step 13b / follow-up). Until then
                            // the constrained callvirt NIEs at the call (its byref
                            // `this` reads as a null object index); this arm throws
                            // a Step-17-tagged NIE so the case is surfaced rather
                            // than silently mishandled.
                            case OpCodeREnum.Constrained:
                                throw new NotImplementedException(
                                    "Step 17: constrained.callvirt on a value type is deferred (callvirt byref-this dispatch lands in Step 13b / a follow-up)");
                            default:
                                throw new NotImplementedException(string.Format("Neo: opcode {0} not yet implemented (Step 6)", code));
                        }
                        ip++;
                    }
                    catch (Exception ex)
                    {
                        var oriESP = (StackObject*)newEsp;
                        StackObject* tmpEsp = oriESP;
                        bool isJmp = HandleException(ex, ref tmpEsp, ehs, method, (int)(ip - ptr), ref frame, ref lastCaughtEx, ref unhandledException, ref finallyEndAddress, out int jmpTarget, out bool isCatch);
                        if (isCatch)
                        {
                            // Truncate mStack back to this frame's reserved region
                            int targetCount = frameRefBase + totalRefSize;
                            if (mStack.Count > targetCount)
                            {
                                mStack.RemoveRange(targetCount, mStack.Count - targetCount);
                            }
                            // Step 14: write the caught exception object into the catch
                            // handler's reserved exception-variable slot. The slot's
                            // byte offset and ref offset were stamped by the JIT
                            // (CompiledFrame.NeoCatchExceptionByteOffset /
                            // NeoCatchExceptionRefOffset) at the catch exception
                            // register's post-compaction index -- mirroring Legacy's
                            // AssignToRegister(exReg = paramCnt + locCnt, ex) at
                            // ILIntepreter.Register.cs:5326-5327. `ex` is already the
                            // unwrapped inner exception when the propagated object was
                            // an ILRuntimeException (HandleException does the unwrap),
                            // matching Legacy semantics. The slot is -1 only when the
                            // method has no catch handler (then isCatch can't be true).
                            int catchByteOff = nf.NeoCatchExceptionByteOffset;
                            int catchRefOff = nf.NeoCatchExceptionRefOffset;
                            if (catchByteOff >= 0)
                            {
                                int catchSlotIdx = frameRefBase + catchRefOff;
                                mStack[catchSlotIdx] = ex;
                                *(int*)(frameBase + catchByteOff) = catchSlotIdx;
                            }
                        }
                        if (isJmp)
                        {
                            ip = ptr + jmpTarget;
                            continue;
                        }
                        if (unhandledException)
                        {
                            // Step 14: re-throw path (e.g. Endfinally re-throws
                            // lastCaughtEx and no handler matches). Stash and let
                            // the bottom cleanup run before re-throwing.
                            pendingThrow = ex;
                            returned = true;
                            break;
                        }
                        unhandledException = true;
                        returned = true;
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
                        if (!AppDomain.DebugService.Break(this, ex))
#endif
                        {
                            // Step 14: stash the wrapped exception and break out
                            // so the bottom-of-method cleanup (frame pop + mStack
                            // truncate) runs BEFORE the re-throw -- making this
                            // Neo frame self-cleaning instead of relying on the
                            // caller's HandleException frame-pop loop to clean up
                            // a leaked frame/mStack reservation.
                            pendingThrow = new ILRuntimeException(ex.Message, this, method, oriESP, ex);
                            break;
                        }
                    }
                }
            }

            // VT-THIS-ADDR: the slot-0 -> caller-dest copy-back for a value-type
            // ctor invoked via the runtime Newobj IL-VT branch is performed in
            // the Ret arm ABOVE (before the mStack pop, which removes the slot-0
            // ref entries). Nothing to do here on the exception-unwind path: a
            // ctor that throws abandons the construction (no copy-back).

            // Unwind: pop frame, truncate mStack back to entry baseline.
            // Frames stack popping: best-effort (BasePointer compares by pointer).
            if (stack.Frames.Count > 0 && stack.Frames.Peek().BasePointer == frame.BasePointer)
            {
                stack.Frames.Pop();
            }
            if (mStack.Count > frameRefBase)
            {
                mStack.RemoveRange(frameRefBase, mStack.Count - frameRefBase);
            }

#if DEBUG && !NO_PROFILER
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == AppDomain.UnityMainThreadID)
#if UNITY_5_5_OR_NEWER
                UnityEngine.Profiling.Profiler.EndSample();
#else
                UnityEngine.Profiler.EndSample();
#endif
#endif
            // Step 14: if an exception escaped this frame unhandled, re-throw it
            // now -- AFTER the frame/mStack cleanup above, so this Neo frame is
            // self-cleaning. The exception propagates via the C# stack into the
            // caller's per-iteration catch, where HandleException searches the
            // CALLER's handler table at the call-site address (cross-frame
            // propagation). Because this frame was already popped and its mStack
            // reservation already released, the caller's HandleException
            // frame-pop loop finds the caller's frame on top and does not need
            // to clean up this frame.
            if (pendingThrow != null)
                throw pendingThrow;
            return frameBase;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ILTypeInstance GetNeoILInstance(AutoList mStack, int objIndex)
        {
            if (objIndex < 0)
                throw new NullReferenceException();
            ILTypeInstance ins = mStack[objIndex] as ILTypeInstance;
            if (ins == null)
                // A CLR object reached an IL-instance field/address path (ldfld/
                // stfld heap arm, or stind/ldind/stobj/ldobj on an mStack target).
                // CLR-object field access via field hash is deferred to Step 13b,
                // so surface it as a Step-tagged NIE rather than a raw cast.
                throw new NotImplementedException(
                    "Step 17/13b: field/element access on a CLR object via the IL-instance path is deferred (CLR field-hash plumbing lands in Step 13b)");
            return ins;
        }

        // Step 14: resolve a Throw operand's exception object from its mStack ref
        // slot. A null exception object (ref index -1) is itself a
        // NullReferenceException in the CLR. The object may be an
        // ILRuntimeException wrapping a deeper IL-thrown exception; the shared
        // HandleException unwraps it on catch entry, so here we throw the object
        // as-is.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Exception GetNeoException(AutoList mStack, int objIndex)
        {
            if (objIndex < 0)
                throw new NullReferenceException();
            Exception ex = mStack[objIndex] as Exception;
            if (ex == null)
                throw new NullReferenceException();
            return ex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe int ReadConvI4(byte* frameBase, ushort offset, NeoPrimitiveTypeTag tag)
        {
            unchecked
            {
                switch (tag)
                {
                    case NeoPrimitiveTypeTag.U4:
                        return (int)*(uint*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.I8:
                        return (int)*(long*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.U8:
                        return (int)*(ulong*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.R4:
                        return (int)*(float*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.R8:
                        return (int)*(double*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.I4:
                    default:
                        return *(int*)(frameBase + offset);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe uint ReadConvU4(byte* frameBase, ushort offset, NeoPrimitiveTypeTag tag)
        {
            unchecked
            {
                switch (tag)
                {
                    case NeoPrimitiveTypeTag.U4:
                        return *(uint*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.I8:
                        return (uint)*(long*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.U8:
                        return (uint)*(ulong*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.R4:
                        return (uint)*(float*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.R8:
                        return (uint)*(double*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.I4:
                    default:
                        return (uint)*(int*)(frameBase + offset);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe long ReadConvI8(byte* frameBase, ushort offset, NeoPrimitiveTypeTag tag)
        {
            unchecked
            {
                switch (tag)
                {
                    case NeoPrimitiveTypeTag.U4:
                        return *(uint*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.I8:
                        return *(long*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.U8:
                        return (long)*(ulong*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.R4:
                        return (long)*(float*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.R8:
                        return (long)*(double*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.I4:
                    default:
                        return *(int*)(frameBase + offset);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe ulong ReadConvU8(byte* frameBase, ushort offset, NeoPrimitiveTypeTag tag)
        {
            unchecked
            {
                switch (tag)
                {
                    case NeoPrimitiveTypeTag.U4:
                        return *(uint*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.I8:
                        return (ulong)*(long*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.U8:
                        return *(ulong*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.R4:
                        return (ulong)*(float*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.R8:
                        return (ulong)*(double*)(frameBase + offset);
                    case NeoPrimitiveTypeTag.I4:
                    default:
                        return (ulong)*(int*)(frameBase + offset);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe float ReadConvR4(byte* frameBase, ushort offset, NeoPrimitiveTypeTag tag)
        {
            switch (tag)
            {
                case NeoPrimitiveTypeTag.U4:
                    return *(uint*)(frameBase + offset);
                case NeoPrimitiveTypeTag.I8:
                    return *(long*)(frameBase + offset);
                case NeoPrimitiveTypeTag.U8:
                    return *(ulong*)(frameBase + offset);
                case NeoPrimitiveTypeTag.R4:
                    return *(float*)(frameBase + offset);
                case NeoPrimitiveTypeTag.R8:
                    return (float)*(double*)(frameBase + offset);
                case NeoPrimitiveTypeTag.I4:
                default:
                    return *(int*)(frameBase + offset);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe double ReadConvR8(byte* frameBase, ushort offset, NeoPrimitiveTypeTag tag)
        {
            switch (tag)
            {
                case NeoPrimitiveTypeTag.U4:
                    return *(uint*)(frameBase + offset);
                case NeoPrimitiveTypeTag.I8:
                    return *(long*)(frameBase + offset);
                case NeoPrimitiveTypeTag.U8:
                    return *(ulong*)(frameBase + offset);
                case NeoPrimitiveTypeTag.R4:
                    return *(float*)(frameBase + offset);
                case NeoPrimitiveTypeTag.R8:
                    return *(double*)(frameBase + offset);
                case NeoPrimitiveTypeTag.I4:
                default:
                    return *(int*)(frameBase + offset);
            }
        }

        // Copies frame byte region + frame mStack refs into an ILTypeInstance.
        // Used by Box (frame → boxed instance).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe void CopyFrameToIL(byte* frameBase, int primOffset, int refOffset,
                                         int primSize, int refCount,
                                         AutoList mStack, int frameRefBase,
                                         ILTypeInstance dst)
        {
            if (primSize > 0 && dst.Primitives != null)
            {
                ref byte dstP = ref MemoryMarshal.GetReference(dst.Primitives.AsSpan());
                Unsafe.CopyBlock(ref dstP, ref *(frameBase + primOffset), (uint)primSize);
            }
            if (refCount > 0)
            {
                var dstRefs = dst.ManagedObjects;
                int srcBase = frameRefBase + refOffset;
                for (int i = 0; i < refCount; i++)
                    dstRefs[i] = mStack[srcBase + i];
            }
        }

        // Copies ILTypeInstance contents back to the frame byte region + frame mStack refs.
        // Used by Unbox / Unbox_Any (boxed instance → frame).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe void CopyILToFrame(ILTypeInstance src,
                                         byte* frameBase, int primOffset, int refOffset,
                                         int primSize, int refCount,
                                         AutoList mStack, int frameRefBase)
        {
            if (primSize > 0 && src.Primitives != null)
            {
                ref byte srcP = ref MemoryMarshal.GetReference(src.Primitives.AsSpan());
                Unsafe.CopyBlock(ref *(frameBase + primOffset), ref srcP, (uint)primSize);
            }
            if (refCount > 0)
            {
                var srcRefs = src.ManagedObjects;
                int dstBase = frameRefBase + refOffset;
                for (int i = 0; i < refCount; i++)
                    mStack[dstBase + i] = srcRefs[i];
            }
        }

        // Step 6 entry shim: convert raw bytes the Neo interpreter wrote into
        // retDst back into a CLR object the outer Run() pipeline can consume.
        // Step 8 will replace this whole code path with the proper Neo call
        // convention; for now it's only invoked from the top-level Invoke entry
        // for primitive return types.
        static unsafe object NeoBoxReturnValue(IType returnType, byte* retDst, int retSize)
        {
            var clr = returnType.TypeForCLR;
            if (clr == typeof(int))
                return *(int*)retDst;
            if (clr == typeof(uint))
                return *(uint*)retDst;
            if (clr == typeof(long))
                return *(long*)retDst;
            if (clr == typeof(ulong))
                return *(ulong*)retDst;
            if (clr == typeof(short))
                return *(short*)retDst;
            if (clr == typeof(ushort))
                return *(ushort*)retDst;
            if (clr == typeof(byte))
                return *retDst;
            if (clr == typeof(sbyte))
                return *(sbyte*)retDst;
            if (clr == typeof(bool))
                return *retDst != 0;
            if (clr == typeof(char))
                return *(char*)retDst;
            if (clr == typeof(float))
                return *(float*)retDst;
            if (clr == typeof(double))
                return *(double*)retDst;
            // Fallback: raw bytes as int
            return retSize >= 4 ? (object)*(int*)retDst : (object)(int)*retDst;
        }

        // Step 13: box a sized primitive value of the given CLR Type from a raw
        // frame byte pointer into a boxed object (the inverse of NeoBoxReturnValue
        // without the IType lookup -- callers already hold the System.Type).
        // NOTE: currently has no call sites after the Box-enum arm was removed
        // (MINOR-1). Retained intentionally -- it may be revived by Step 15
        // (enum cross-cast). Do NOT remove.
        static unsafe object NeoBoxPrimitiveByType(Type clr, byte* src)
        {
            if (clr == typeof(int))
                return *(int*)src;
            if (clr == typeof(uint))
                return *(uint*)src;
            if (clr == typeof(long))
                return *(long*)src;
            if (clr == typeof(ulong))
                return *(ulong*)src;
            if (clr == typeof(short))
                return *(short*)src;
            if (clr == typeof(ushort))
                return *(ushort*)src;
            if (clr == typeof(byte))
                return *src;
            if (clr == typeof(sbyte))
                return *(sbyte*)src;
            if (clr == typeof(bool))
                return *src != 0;
            if (clr == typeof(char))
                return *(char*)src;
            if (clr == typeof(float))
                return *(float*)src;
            if (clr == typeof(double))
                return *(double*)src;
            if (clr == typeof(IntPtr))
                return *(IntPtr*)src;
            if (clr == typeof(UIntPtr))
                return *(UIntPtr*)src;
            // Fallback: raw bytes as int.
            return (object)*(int*)src;
        }

        // Step 13: the inverse of NeoBoxPrimitiveByType -- write a boxed CLR
        // primitive back into a raw frame byte pointer by the value's CLR type.
        static unsafe void NeoWritePrimitiveToFrame(object obj, byte* dst)
        {
            Type clr = obj.GetType();
            if (clr == typeof(int))
                *(int*)dst = (int)obj;
            else if (clr == typeof(uint))
                *(uint*)dst = (uint)obj;
            else if (clr == typeof(long))
                *(long*)dst = (long)obj;
            else if (clr == typeof(ulong))
                *(ulong*)dst = (ulong)obj;
            else if (clr == typeof(short))
                *(short*)dst = (short)obj;
            else if (clr == typeof(ushort))
                *(ushort*)dst = (ushort)obj;
            else if (clr == typeof(byte))
                *dst = (byte)obj;
            else if (clr == typeof(sbyte))
                *(sbyte*)dst = (sbyte)obj;
            else if (clr == typeof(bool))
                *dst = (bool)obj ? (byte)1 : (byte)0;
            else if (clr == typeof(char))
                *(char*)dst = (char)obj;
            else if (clr == typeof(float))
                *(float*)dst = (float)obj;
            else if (clr == typeof(double))
                *(double*)dst = (double)obj;
            else if (clr == typeof(IntPtr))
                *(IntPtr*)dst = (IntPtr)obj;
            else if (clr == typeof(UIntPtr))
                *(UIntPtr*)dst = (UIntPtr)obj;
            else
                throw new NotImplementedException("Neo: unsupported CLR primitive for Unbox: " + clr.FullName);
        }
    }
}
#endif
