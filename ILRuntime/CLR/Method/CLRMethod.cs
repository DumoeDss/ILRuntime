using ILRuntime.CLR.TypeSystem;
using ILRuntime.CLR.Utils;
using ILRuntime.Runtime.Enviorment;
using ILRuntime.Runtime.Intepreter;
using ILRuntime.Runtime.Intepreter.RegisterVM;
using ILRuntime.Runtime.Stack;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;

#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
using AutoList = System.Collections.Generic.List<object>;
#else
using AutoList = ILRuntime.Other.UncheckedList<object>;
#endif
namespace ILRuntime.CLR.Method
{
    public sealed class CLRMethod : IMethod
    {
        MethodInfo def;
        ConstructorInfo cDef;
        List<IType> parameters;
        ParameterInfo[] parametersCLR;
        ILRuntime.Runtime.Enviorment.AppDomain appdomain;
        CLRType declaringType;
        bool isConstructor;
        CLRRedirectionDelegate redirect;
        CLRRedirectionDelegateNeo redirectNeo;
        IType[] genericArguments;
        Type[] genericArgumentsCLR;
        object[] invocationParam;
        bool isDelegateInvoke, isDelegateDynamicInvoke;
        int hashCode = -1;
        static int instance_id = 0x20000000;

        public IType DeclearingType
        {
            get
            {
                return declaringType;
            }
        }
        public string Name
        {
            get
            {
                return def != null ? def.Name : cDef.Name;
            }
        }
        public bool HasThis
        {
            get
            {
                return isConstructor ? !cDef.IsStatic : !def.IsStatic;
            }
        }

        int _genericParameterCount = -1;
        public int GenericParameterCount
        {
            get
            {
                if (_genericParameterCount == -1)
                {
                    if (def.ContainsGenericParameters && def.IsGenericMethodDefinition)
                        _genericParameterCount = def.GetGenericArguments().Length;
                    else
                        _genericParameterCount = 0;
                }
                return _genericParameterCount;
            }
        }

        public bool IsGenericInstance
        {
            get
            {
                return genericArguments != null;
            }
        }

        public bool IsDelegateInvoke
        {
            get
            {
                return isDelegateInvoke;
            }
        }

        public bool IsDelegateDynamicInvoke
        {
            get
            {
                return isDelegateDynamicInvoke;
            }
        }

        public bool IsStatic
        {
            get
            {
                if (cDef != null)
                    return cDef.IsStatic;
                else
                    return def.IsStatic;
            }
        }

        bool TryGetRedirection<T>(Dictionary<MethodBase, T> map, out T redirect)
        {
            redirect = default;
            if (def != null)
            {
                if (def.IsGenericMethod && !def.IsGenericMethodDefinition)
                {
                    if (!map.TryGetValue(def.GetGenericMethodDefinition(), out redirect))
                        map.TryGetValue(def, out redirect);
                }
                else
                    map.TryGetValue(def, out redirect);
                return redirect != null;
            }
            else if (cDef != null)
            {
                map.TryGetValue(cDef, out redirect);
                return redirect != null;
            }
            return false;
        }

        public CLRRedirectionDelegate Redirection
        {
            get
            {
                if (redirect == null)
                {
                    TryGetRedirection(appdomain.RedirectMap, out redirect);
                }
                return redirect;
            }
        }

        public CLRRedirectionDelegateNeo RedirectionNeo
        {
            get
            {
                if (redirectNeo == null)
                {
                    TryGetRedirection(appdomain.RedirectMapNeo, out redirectNeo);
                }
                return redirectNeo;
            }
        }

        public MethodInfo MethodInfo { get { return def; } }

        public ConstructorInfo ConstructorInfo { get { return cDef; } }

        public IType[] GenericArguments { get { return genericArguments; } }

        public Type[] GenericArgumentsCLR
        {
            get
            {
                if (genericArgumentsCLR == null)
                {
                    if (cDef != null)
                        genericArgumentsCLR = cDef.GetGenericArguments();
                    else
                        genericArgumentsCLR = def.GetGenericArguments();
                }
                return genericArgumentsCLR;
            }
        }

        internal CLRMethod(MethodInfo def, CLRType type, ILRuntime.Runtime.Enviorment.AppDomain domain)
        {
            this.def = def;
            declaringType = type;
            this.appdomain = domain;
            if (!def.ReturnType.ContainsGenericParameters)
            {
                ReturnType = domain.GetType(def.ReturnType.FullName);
                if (ReturnType == null)
                {
                    ReturnType = domain.GetType(def.ReturnType.AssemblyQualifiedName);
                }
            }
            if (type.IsDelegate)
            {
                if (def.Name == "Invoke")
                    isDelegateInvoke = true;
                if (def.Name == "DynamicInvoke")
                {
                    isDelegateInvoke = true;
                    isDelegateDynamicInvoke = true;
                }

            }
            isConstructor = false;
        }
        internal CLRMethod(ConstructorInfo def, CLRType type, ILRuntime.Runtime.Enviorment.AppDomain domain)
        {
            this.cDef = def;
            declaringType = type;
            this.appdomain = domain;
            if (!def.ContainsGenericParameters)
            {
                ReturnType = type;
            }
            isConstructor = true;
        }

        public int ParameterCount
        {
            get
            {
                return Parameters.Count;
            }
        }


        public List<IType> Parameters
        {
            get
            {
                if (parameters == null)
                {
                    InitParameters();
                }
                return parameters;
            }
        }

        public ParameterInfo[] ParametersCLR
        {
            get
            {
                if (parametersCLR == null)
                {
                    if (cDef != null)
                        parametersCLR = cDef.GetParameters();
                    else
                        parametersCLR = def.GetParameters();
                }
                return parametersCLR;
            }
        }

        public IType ReturnType
        {
            get;
            private set;
        }

        string signatureString;
        public string SignatureString
        {
            get
            {
                if (signatureString == null)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append(Name);
                    sb.Append('|');
                    sb.Append(GenericParameterCount);
                    sb.Append('(');
                    var ps = Parameters;
                    if (ps != null)
                    {
                        for (int i = 0; i < ps.Count; i++)
                        {
                            if (i > 0)
                                sb.Append(',');
                            sb.Append(ps[i] != null ? ps[i].FullName : string.Empty);
                        }
                    }
                    sb.Append(")->");
                    sb.Append(ReturnType != null ? ReturnType.FullName : string.Empty);
                    signatureString = sb.ToString();
                }
                return signatureString;
            }
        }

        public bool IsConstructor
        {
            get
            {
                return cDef != null;
            }
        }

        void InitParameters()
        {
            parameters = new List<IType>();
            foreach (var i in ParametersCLR)
            {
                IType type = appdomain.GetType(i.ParameterType.FullName);
                if (type == null)
                    type = appdomain.GetType(i.ParameterType.AssemblyQualifiedName);
                if (i.ParameterType.IsGenericTypeDefinition)
                {
                    if (type == null)
                        type = appdomain.GetType(i.ParameterType.GetGenericTypeDefinition().FullName);
                    if (type == null)
                        type = appdomain.GetType(i.ParameterType.GetGenericTypeDefinition().AssemblyQualifiedName);
                }
                if (i.ParameterType.ContainsGenericParameters)
                {
                    var t = i.ParameterType;
                    if (t.HasElementType)
                        t = i.ParameterType.GetElementType();
                    else if (t.GetGenericArguments().Length > 0)
                    {
                        t = t.GetGenericArguments()[0];
                    }
                    type = new ILGenericParameterType(t.Name);
                }
                if (type == null)
                    throw new TypeLoadException();
                parameters.Add(type);
            }
        }

        unsafe StackObject* Minus(StackObject* a, int b)
        {
            return (StackObject*)((long)a - sizeof(StackObject) * b);
        }

#if ENABLE_NEO_MODE
        public unsafe object Invoke(byte* targetBase, AutoList mStack, bool isNewObj = false)
        {
            if (parameters == null)
            {
                InitParameters();
            }
            int paramCount = ParameterCount;
            if (invocationParam == null)
                invocationParam = new object[paramCount];
            object[] param = invocationParam;

            int curPrim = 0;
            object instance = null;
            // Step 13 Area 4b: when the `this` is a CLR value type, the call-lowering
            // + CopyNeoCallArguments dereference the ldloca-produced byref into the
            // `this` slot's flat bytes. The reflection path boxes the struct off
            // those bytes, calls the method (which mutates the BOX in place -- CLR
            // MethodInfo.Invoke / ConstructorInfo.Invoke semantics for a boxed-VT
            // call), and then writes the (possibly-mutated) box's flat bytes BACK
            // to the same slot. A post-call reverse copy in the Neo Call arm
            // propagates that back to the caller's in-frame local (the byref's
            // target), so a ctor (`new VT(args)` -> initobj;ldloca;call ctor) and a
            // mutating instance method (`v.Reset()`) land their mutations in the
            // caller's local -- matching CLR `ref this` struct semantics.
            Type vtThisType = null;
            int vtThisSlotOff = 0;
            int vtThisSz = 0;

            if (isNewObj)
            {
                curPrim += 4; // Skip retRefBase
            }
            else if (HasThis)
            {
                // Step 13 Area 4b: discriminate the `this` representation by the
                // declaring type. A CLR value-type instance `this` arrives as the
                // struct's FLAT BYTES in the callee param region (the call-lowering
                // + CopyNeoCallArguments dereference the ldloca-produced byref and
                // lay the struct bytes into the `this` slot, sized by
                // AllocateNeoCallParamSlot's IsValueType branch). So a VT `this`
                // reads exactly like a by-value VT param (ReadNeoValueType). A
                // reference-type `this` is the byte-identical 4-byte mStack-index
                // read (UNCHANGED for !IsValueType -- keys on IsValueType).
                if (DeclearingType is CLRType thisClr && thisClr.IsValueType
                    && !thisClr.TypeForCLR.IsPrimitive && !thisClr.TypeForCLR.IsEnum)
                {
                    // Mirror the 13b param-read NIE guards: a struct `this` WITH
                    // reference fields and NO binder cannot be materialized from
                    // flat bytes (GC refs unmappable); a binder struct WITH ref
                    // fields needs the binder's Neo-cursor ref-mapping (does not
                    // exist yet). Both stay clearly-tagged NIEs (matches 13b).
                    if (thisClr.ValueTypeBinder != null)
                    {
                        thisClr.GetValueTypeSize(out _, out int managedCount);
                        if (managedCount > 0)
                            throw new NotImplementedException("CLR value-type `this` with reference fields via binder in reflection fallback: register a CLR binding redirect (Step 13 Area 4b). Type: " + thisClr.TypeForCLR.FullName);
                    }
                    else if (NeoClrStructHasReferenceField(thisClr.TypeForCLR))
                    {
                        throw new NotImplementedException("CLR value-type `this` with reference fields and no ValueTypeBinder (Step 13 Area 4b): register a binder. Type: " + thisClr.TypeForCLR.FullName);
                    }
                    vtThisType = thisClr.TypeForCLR;
                    vtThisSlotOff = curPrim;
                    vtThisSz = Optimizer.GetNeoValueTypeManagedSize(vtThisType);
                    instance = ILIntepreter.ReadNeoValueType(vtThisType, targetBase, ref curPrim, vtThisSz);
                }
                else
                {
                    int thisIdx = *(int*)(targetBase + curPrim);
                    // neo-array-multidim Gap 2: a null `this` arrives as the Neo
                    // null-ref sentinel (thisIdx < 0). Indexing mStack[-1] throws
                    // ArgumentOutOfRangeException, masking the NullReferenceException
                    // the call must surface (Legacy has the explicit guard at the
                    // read + a null-instance check). Materialize null directly
                    // (mirrors the reference-param read pattern below at
                    // `idx < 0 ? null : mStack[idx]`); the existing null-instance
                    // guards in the ctor (cDef.IsStatic) / method (!def.IsStatic)
                    // branches then throw NRE. Benefits any reflection-fallback
                    // call on a null `this`, not only multi-dim.
                    instance = thisIdx < 0 ? null : mStack[thisIdx];
                    curPrim += 4;
                }
            }

            // Step 13 Area 4c: byref-param write-back tracking. The call-lowering
            // sizes a byref param's dest slot by the ELEMENT type and
            // CopyNeoCallArguments derefs the byref into those flat bytes, so the
            // reader below reads the element value exactly like a by-value param.
            // After the call, CLR MethodInfo.Invoke mutates the `param[i]` box in
            // place for a ref/out param; this write-back re-flattens the (possibly-
            // mutated) `param[i]` into the SAME dest slot, so the post-call reverse
            // copy (CopyNeoCallThisBack) propagates it to the caller's local/field.
            // The IsIn/IsOut gate mirrors the optimizer's (D5): ref/out writes back,
            // in-only does not.
            int[] byRefSlotOff = paramCount > 0 ? new int[paramCount] : null;
            bool[] byRefWriteBack = paramCount > 0 ? new bool[paramCount] : null;
            Type[] byRefElemType = paramCount > 0 ? new Type[paramCount] : null;
            for (int i = 0; i < paramCount; i++)
            {
                byRefSlotOff[i] = -1;
                byRefWriteBack[i] = false;
            }
            ParameterInfo[] pinfos = ParametersCLR;

            for (int i = 0; i < paramCount; i++)
            {
                var ptRaw = Parameters[i];
                // Step 13 Area 4c: a byref param's dest slot is sized by the ELEMENT
                // type and deref'd by CopyNeoCallArguments, so the reader reads it
                // exactly like a by-value param of the element type. De-byref `pt`
                // here so ALL arms below (CLR struct / IL / reference / primitive)
                // dispatch on the element type. The original byref-ness + the write-
                // back gate are captured from `ptRaw` / ParameterInfo below.
                var pt = ptRaw.IsByRef && ptRaw.ElementType != null ? ptRaw.ElementType : ptRaw;
                Type t = pt.TypeForCLR;

                // Step 13 Area 4c: detect a byref param. Record the slot offset +
                // element type + write-back gate for the post-call write-back.
                if (ptRaw.IsByRef)
                {
                    byRefSlotOff[i] = curPrim;
                    byRefElemType[i] = t;
                    bool isOutOnly = pinfos != null && i < pinfos.Length && pinfos[i].IsOut && !pinfos[i].IsIn;
                    if (pinfos != null && i < pinfos.Length)
                        byRefWriteBack[i] = !pinfos[i].IsIn || pinfos[i].IsOut;
                    else
                        byRefWriteBack[i] = true; // ref (no IsIn/IsOut metadata): write back
                    // An `out`-only reference-type param is UNINITIALIZED at the call
                    // site; reading its dest slot as an mStack index is garbage / OOB.
                    // Skip the read (param[i] = null); the method overwrites it and the
                    // write-back stores the assigned reference. Advance the cursor by
                    // the reference slot width (4) to stay byte-consistent with the
                    // layout. (Primitive/struct `out` reads harmlessly -- the value is
                    // overwritten -- so only the reference sub-case skips.)
                    if (isOutOnly && !(pt is CLRType cct && cct.IsValueType) && !(pt is ILType) && !t.IsPrimitive && !t.IsEnum)
                    {
                        param[i] = null;
                        curPrim += 4;
                        continue;
                    }
                }

                if (pt is CLRType clrType && clrType.IsValueType && !clrType.TypeForCLR.IsPrimitive && !clrType.TypeForCLR.IsEnum)
                {
                    // Step 13b (D2): read a CLR struct param by its flat-byte slot
                    // width. The callee param region laid out a CLR struct as flat
                    // managed bytes sized by Optimizer.GetNeoValueTypeManagedSize
                    // (the SAME size ReadNeoValueType advances the cursor by), so
                    // this stays byte-consistent with the layout. A struct WITH
                    // reference-type fields and NO registered ValueTypeBinder
                    // cannot be read (the GC refs are not mappable without a
                    // binder) -> clear Step-13b NIE; a struct WITH a binder that
                    // has ref fields needs the binder's ref-mapping, which the
                    // Legacy StackObject binder API cannot feed here -- the
                    // reflection fallback only supports binder structs whose ref
                    // count is zero (pure-primitive like TestVector3), handled by
                    // the flat-bytes read below. (The autogen path owns the full
                    // binder ref-mapping for binder structs; this is the no-
                    // redirect reflection path.)
                    if (clrType.ValueTypeBinder != null)
                    {
                        clrType.GetValueTypeSize(out _, out int managedCount);
                        if (managedCount > 0)
                            throw new NotImplementedException("CLR value type with reference fields via binder in reflection fallback: register a CLR binding redirect (Step 13b). Type: " + t.FullName);
                    }
                    else if (NeoClrStructHasReferenceField(t))
                    {
                        throw new NotImplementedException("CLR value type with reference fields and no ValueTypeBinder (Step 13b): register a binder. Type: " + t.FullName);
                    }
                    int vtSize = Optimizer.GetNeoValueTypeManagedSize(t);
                    param[i] = ILIntepreter.ReadNeoValueType(t, targetBase, ref curPrim, vtSize);
                    continue;
                }

                if (pt is ILType || !t.IsPrimitive && !t.IsEnum)
                {
                    int idx = *(int*)(targetBase + curPrim);
                    // A null reference param is encoded as mStack index -1 (the Neo
                    // null-ref sentinel). mStack[-1] would throw, so materialize
                    // null directly. (Pre-existing gap surfaced by the Area 4d
                    // probes that pass a null string to a CLR method.)
                    object pval = idx < 0 ? null : mStack[idx];
                    // Step 19: a delegate-typed param arrives as an IDelegateAdapter
                    // (the bridge), not a real CLR delegate. Unwrap it so a CLR
                    // method receiving a delegate (e.g. List.ForEach(action))
                    // gets the real Delegate (mirrors the autogen CheckCLRTypes
                    // unwrap + the Legacy path).
                    if (typeof(Delegate).IsAssignableFrom(t))
                        param[i] = t.CheckCLRTypes(pval, Extensions.TypeFlags.IsDelegate);
                    else
                        param[i] = pval;
                    curPrim += 4;
                }
                else
                {
                    if (t == typeof(int) || t.IsEnum) { param[i] = ILIntepreter.ReadNeoInt32(targetBase, ref curPrim); }
                    else if (t == typeof(long)) { param[i] = ILIntepreter.ReadNeoInt64(targetBase, ref curPrim); }
                    else if (t == typeof(float)) { param[i] = ILIntepreter.ReadNeoFloat(targetBase, ref curPrim); }
                    else if (t == typeof(double)) { param[i] = ILIntepreter.ReadNeoDouble(targetBase, ref curPrim); }
                    else if (t == typeof(bool)) { param[i] = ILIntepreter.ReadNeoBoolean(targetBase, ref curPrim); }
                    else if (t == typeof(byte)) { param[i] = ILIntepreter.ReadNeoUInt8(targetBase, ref curPrim); }
                    else if (t == typeof(sbyte)) { param[i] = ILIntepreter.ReadNeoInt8(targetBase, ref curPrim); }
                    else if (t == typeof(short)) { param[i] = ILIntepreter.ReadNeoInt16(targetBase, ref curPrim); }
                    else if (t == typeof(ushort)) { param[i] = ILIntepreter.ReadNeoUInt16(targetBase, ref curPrim); }
                    else if (t == typeof(uint)) { param[i] = ILIntepreter.ReadNeoUInt32(targetBase, ref curPrim); }
                    else if (t == typeof(ulong)) { param[i] = ILIntepreter.ReadNeoUInt64(targetBase, ref curPrim); }
                    else if (t == typeof(char)) { param[i] = ILIntepreter.ReadNeoChar(targetBase, ref curPrim); }
                }
            }

            object res = null;
            if (isConstructor)
            {
                if (!isNewObj)
                {
                    if (!cDef.IsStatic)
                    {
                        if (instance == null)
                            throw new NullReferenceException();
                        if (instance is CrossBindingAdaptorType && paramCount == 0)
                            return null;
                        try { cDef.Invoke(instance, param); }
                        catch (TargetInvocationException tie) { ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); throw; }
                        // Step 13 Area 4b: a struct ctor mutates `instance` (the
                        // boxed struct) in place; write the mutated flat bytes back
                        // to the `this` slot so the post-call reverse copy
                        // propagates them to the caller's in-frame local.
                        if (vtThisType != null)
                            ILIntepreter.WriteNeoValueType(instance, targetBase + vtThisSlotOff, vtThisSz);
                    }
                    else
                        throw new NotImplementedException();
                }
                else
                {
                    try { res = cDef.Invoke(param); }
                    catch (TargetInvocationException tie) { ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); throw; }
                }
            }
            else
            {
                if (!def.IsStatic)
                {
                    if (!(instance is Reflection.ILRuntimeWrapperType))
                        instance = declaringType.TypeForCLR.CheckCLRTypes(instance);
                    if (instance == null)
                        throw new NullReferenceException();
                }
                try { res = def.Invoke(instance, param); }
                catch (TargetInvocationException tie) { ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); throw; }
                // Step 13 Area 4b: a struct instance method may mutate `this`
                // (e.g. Reset()); write the (possibly-mutated) flat bytes back to
                // the `this` slot so the post-call reverse copy propagates them.
                if (vtThisType != null)
                    ILIntepreter.WriteNeoValueType(instance, targetBase + vtThisSlotOff, vtThisSz);
            }

            // Step 13 Area 4c: byref-param write-back. CLR MethodInfo.Invoke /
            // ConstructorInfo.Invoke mutate the `param[i]` box in place for a
            // ref/out param; re-flatten the (possibly-mutated) value into the dest
            // slot so CopyNeoCallThisBack propagates it to the caller's local/field.
            // The element type was captured above (byRefElemType[i]); a primitive/
            // enum flattens via WriteNeoValueType (a boxed-primitive write); a CLR
            // struct re-flattens the mutated boxed struct; a reference type stores
            // its mStack index. The IsIn/IsOut gate (D5) drops an in-only param.
            if (byRefSlotOff != null)
            {
                for (int i = 0; i < paramCount; i++)
                {
                    if (!byRefWriteBack[i])
                        continue;
                    int slotOff = byRefSlotOff[i];
                    if (slotOff < 0)
                        continue;
                    Type et = byRefElemType[i];
                    object pv = param[i];
                    if (et != null && (et.IsPrimitive || et.IsEnum || et.IsValueType))
                    {
                        int sz = Optimizer.GetNeoValueTypeManagedSize(et);
                        if (pv != null)
                            ILIntepreter.WriteNeoValueType(pv, targetBase + slotOff, sz);
                    }
                    else
                    {
                        // reference-type element: store the mStack index. The post-
                        // call reverse copy (CopyNeoCallThisBack -> frame-native)
                        // copies these 4 bytes to the caller's local ref slot.
                        if (pv == null)
                            *(int*)(targetBase + slotOff) = -1;
                        else
                        {
                            int newIdx = mStack.Count;
                            mStack.Add(pv);
                            *(int*)(targetBase + slotOff) = newIdx;
                        }
                    }
                }
            }

            Array.Clear(invocationParam, 0, invocationParam.Length);
            return res;
        }

        // Step 13b: does a CLR value type contain any reference-type (managed)
        // instance field (recursively)? Such a struct's flat-bytes slot holds GC
        // references that cannot be materialized without a ValueTypeBinder, so the
        // no-binder reader throws a clear NIE. Primitives/enums and pure-value
        // structs (e.g. TestVector3 -- 3 floats) return false.
        static bool NeoClrStructHasReferenceField(Type t)
        {
            if (t == null || !t.IsValueType)
                return false;
            if (t.IsPrimitive || t.IsEnum)
                return false;
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var ft = f.FieldType;
                if (ft.IsPointer)
                    continue;
                if (!ft.IsValueType)
                    return true;
                if (!ft.IsPrimitive && !ft.IsEnum)
                {
                    // Nested CLR struct: recurse (an unmanaged-only nested struct
                    // is fine; one with a ref field is not).
                    if (NeoClrStructHasReferenceField(ft))
                        return true;
                }
            }
            return false;
        }
#endif

        public unsafe object Invoke(Runtime.Intepreter.ILIntepreter intepreter, StackObject* esp, AutoList mStack, bool isNewObj = false)
        {
            if (parameters == null)
            {
                InitParameters();
            }
            int paramCount = ParameterCount;
            if (invocationParam == null)
                invocationParam = new object[paramCount];
            object[] param = invocationParam;
            for (int i = paramCount; i >= 1; i--)
            {
                var p = Minus(esp, i);
                var pt = this.ParametersCLR[paramCount - i].ParameterType;
                var obj = pt.CheckCLRTypes(StackObject.ToObject(p, appdomain, mStack));
                obj = ILIntepreter.CheckAndCloneValueType(obj, appdomain);
                param[paramCount - i] = obj;
            }

            if (isConstructor)
            {
                if (!isNewObj)
                {
                    if (!cDef.IsStatic)
                    {
                        object instance = declaringType.TypeForCLR.CheckCLRTypes(StackObject.ToObject((Minus(esp, paramCount + 1)), appdomain, mStack));
                        if (instance == null)
                            throw new NullReferenceException();
                        if (instance is CrossBindingAdaptorType && paramCount == 0)//It makes no sense to call the Adaptor's default constructor
                            return null;
                        try { cDef.Invoke(instance, param); }
                        catch (TargetInvocationException tie) { ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); throw; }
                        Array.Clear(invocationParam, 0, invocationParam.Length);
                        return null;
                    }
                    else
                    {
                        throw new NotImplementedException();
                    }
                }
                else
                {
                    object res;
                    try { res = cDef.Invoke(param); }
                    catch (TargetInvocationException tie) { ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); throw; }
                    FixReference(paramCount, esp, param, mStack, null, false);
                    Array.Clear(invocationParam, 0, invocationParam.Length);
                    return res;
                }

            }
            else
            {
                object instance = null;

                if (!def.IsStatic)
                {
                    instance = StackObject.ToObject((Minus(esp, paramCount + 1)), appdomain, mStack);
                    if (!(instance is Reflection.ILRuntimeWrapperType))
                        instance = declaringType.TypeForCLR.CheckCLRTypes(instance);
                    //if (declaringType.IsValueType)
                    //    instance = ILIntepreter.CheckAndCloneValueType(instance, appdomain);
                    if (instance == null)
                        throw new NullReferenceException();
                }
                object res = null;
                /*if (redirect != null)
                    res = redirect(new ILContext(appdomain, intepreter, esp, mStack, this), instance, param, genericArguments);
                else*/
                {
                    try { res = def.Invoke(instance, param); }
                    catch (TargetInvocationException tie) { ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); throw; }
                }

                FixReference(paramCount, esp, param, mStack, instance, !def.IsStatic);
                Array.Clear(invocationParam, 0, invocationParam.Length);
                return res;
            }
        }

        unsafe void FixReference(int paramCount, StackObject* esp, object[] param, AutoList mStack, object instance, bool hasThis)
        {
            var cnt = hasThis ? paramCount + 1 : paramCount;
            for (int i = cnt; i >= 1; i--)
            {
                var p = Minus(esp, i);
                var val = i <= paramCount ? param[paramCount - i] : instance;
                switch (p->ObjectType)
                {
                    case ObjectTypes.StackObjectReference:
                        {
                            var addr = *(long*)&p->Value;
                            var dst = (StackObject*)addr;
                            if (dst->ObjectType >= ObjectTypes.Object)
                            {
                                var obj = val;
                                if (obj is CrossBindingAdaptorType)
                                    obj = ((CrossBindingAdaptorType)obj).ILInstance;
                                mStack[dst->Value] = obj;
                            }
                            else
                            {
                                ILIntepreter.UnboxObject(dst, val, mStack, appdomain);
                            }
                        }
                        break;
                    case ObjectTypes.FieldReference:
                        {
                            var obj = mStack[p->Value];
                            if (obj is ILTypeInstance)
                            {
                                ((ILTypeInstance)obj)[p->ValueLow] = val;
                            }
                            else
                            {
                                var t = appdomain.GetType(obj.GetType()) as CLRType;
                                t.GetField(p->ValueLow).SetValue(obj, val);
                            }
                        }
                        break;
                    case ObjectTypes.StaticFieldReference:
                        {
                            var t = appdomain.GetType(p->Value);
                            if (t is ILType)
                            {
                                ((ILType)t).StaticInstance[p->ValueLow] = val;
                            }
                            else
                            {
                                ((CLRType)t).SetStaticFieldValue(p->ValueLow, val);
                            }
                        }
                        break;
                    case ObjectTypes.ArrayReference:
                        {
                            var arr = mStack[p->Value] as Array;
                            arr.SetValue(val, p->ValueLow);
                        }
                        break;
                }
            }
        }

        public IMethod MakeGenericMethod(IType[] genericArguments)
        {
            Type[] p = new Type[genericArguments.Length];
            for (int i = 0; i < genericArguments.Length; i++)
            {
                p[i] = genericArguments[i].TypeForCLR;
            }

            MethodInfo t = null;
#if UNITY_EDITOR || (DEBUG && !DISABLE_ILRUNTIME_DEBUG)
            try
            {
#endif
                t = def.MakeGenericMethod(p);
#if UNITY_EDITOR || (DEBUG && !DISABLE_ILRUNTIME_DEBUG)
            }
            catch (Exception e)
            {
                string argString = "";
                for (int i = 0; i < genericArguments.Length; i++)
                {
                    argString += genericArguments[i].TypeForCLR.FullName + ", ";
                }

                argString = argString.Substring(0, argString.Length - 2);
                throw new Exception(string.Format("MakeGenericMethod failed : {0}.{1}<{2}>", def.DeclaringType.FullName, def.Name, argString));
            }
#endif
            var res = new CLRMethod(t, declaringType, appdomain);
            res.genericArguments = genericArguments;
            return res;
        }

        public override string ToString()
        {
            if (def != null)
                return def.ToString();
            else
                return cDef.ToString();
        }

        public override int GetHashCode()
        {
            if (hashCode == -1)
                hashCode = System.Threading.Interlocked.Add(ref instance_id, 1);
            return hashCode;
        }


        bool? isExtend;
        public bool IsExtend
        {
            get
            {
                if (isExtend == null)
                {
                    isExtend = this.IsExtendMethod();
                }
                return isExtend.Value;
            }
        }
    }
}
