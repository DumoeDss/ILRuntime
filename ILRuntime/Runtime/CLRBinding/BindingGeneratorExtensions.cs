using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Text;
using ILRuntime.Runtime.Enviorment;
using ILRuntime.CLR.Utils;

namespace ILRuntime.Runtime.CLRBinding
{
    static class BindingGeneratorExtensions
    {
        // Step 13b: does a CLR value type contain any reference-type (managed)
        // instance field (recursively)? Used by the Neo autogen to emit a clear
        // NIE for structs that cannot be read/written as flat bytes without a
        // ValueTypeBinder (GC refs are unmappable). Mirrors CLRMethod's runtime
        // NeoClrStructHasReferenceField so the codegen-time decision and the
        // reflection-fallback runtime decision agree.
        internal static bool NeoBindingHasReferenceField(Type t)
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
                    if (NeoBindingHasReferenceField(ft))
                        return true;
                }
            }
            return false;
        }

        internal static bool ShouldSkipField(this Type type, FieldInfo i)
        {
            if (i.IsPrivate)
                return true;
            //EventHandler is currently not supported
            if (i.IsSpecialName)
            {
                return true;
            }
            if (i.GetCustomAttributes(typeof(ObsoleteAttribute), true).Length > 0)
                return true;
            return false;
        }

        internal static bool ShouldSkipMethod(this Type type, MethodBase i)
        {
            if (i.IsPrivate)
                return true;
            if (i.IsGenericMethodDefinition)
                return true;
            if (i.IsConstructor && type.IsAbstract)
                return true;
            if (i is MethodInfo && ((MethodInfo)i).ReturnType.IsByRef)
                return true;
            //EventHandler is currently not supported
            var param = i.GetParameters();
            if (i.IsSpecialName)
            {
                string[] t = i.Name.Split('_');
                if (t[0] == "add" || t[0] == "remove")
                    return false;
                if (t[0] == "get" || t[0] == "set")
                {
                    Type[] ts;
                    var cnt = t[0] == "set" ? param.Length - 1 : param.Length;

                    if (cnt > 0)
                    {
                        ts = new Type[cnt];
                        for (int j = 0; j < cnt; j++)
                        {
                            ts[j] = param[j].ParameterType;
                        }
                    }
                    else
                        ts = new Type[0];
                    var prop = type.GetProperties().FirstOrDefault(p => p.Name == t[1] && p.GetIndexParameters().Select(pp => pp.ParameterType).SequenceEqual(ts));
                    if (prop == null)
                    {
                        return true;
                    }
                    if (prop.GetCustomAttributes(typeof(ObsoleteAttribute), true).Length > 0)
                        return true;
                }
            }
            if (i.GetCustomAttributes(typeof(ObsoleteAttribute), true).Length > 0)
                return true;
            foreach (var j in param)
            {
                if (j.ParameterType.IsPointer)
                    return true;
            }
            return false;
        }

        internal static void AppendParameters(this ParameterInfo[] param, StringBuilder sb, bool isMultiArr = false, int skipLast = 0)
        {
            bool first = true;
            for (int i = 0; i < param.Length - skipLast; i++)
            {
                if (first)
                    first = false;
                else
                    sb.Append(", ");
                var j = param[i];
                if (j.IsOut && j.ParameterType.IsByRef)
                    sb.Append("out ");
                else if (j.IsIn && j.ParameterType.IsByRef)
                    sb.Append("in ");
                else if (j.ParameterType.IsByRef)
                    sb.Append("ref ");
                if (isMultiArr)
                {
                    sb.Append("a");
                    sb.Append(i + 1);
                }
                else
                {
                    sb.Append("@");
                    sb.Append(j.Name);
                }
            }
        }

        internal static void AppendArgumentCodeNeo(this Type p, StringBuilder sb, int idx, string name, List<Type> valueTypeBinders, bool isMultiArr)
        {
            string clsName, realClsName;
            bool isByRef;
            p.GetClassName(out clsName, out realClsName, out isByRef);
            var pt = p.IsByRef ? p.GetElementType() : p;
            string varName;
            if (isMultiArr)
            {
                varName = "a" + idx;
            }
            else
            {
                varName = (name != null ? "@" + name : "a" + idx);
            }

            // Step 13 Area 4c: capture the dest-slot offset at read time so the
            // post-call write-back epilogue (AppendNeoWriteBackCode) can store a
            // ref/out param's mutated value back into the callee param region.
            // Emitted for every param (cheap); the write-back is gated on p.IsByRef
            // + the ref/out modifier in the epilogue.
            if (!isMultiArr)
            {
                sb.AppendLine($"            int __off_{idx} = __curPrim;");
            }

            if (pt.IsValueType && !pt.IsPrimitive && valueTypeBinders != null && valueTypeBinders.Contains(pt))
            {
                // Step 13b (D5): CLR struct param WITH a registered ValueTypeBinder.
                // For a pure-primitive binder struct (the common case, e.g.
                // TestVector3 -- 3 floats) the flat-bytes read matches the callee
                // layout exactly. A binder struct WITH reference fields would need
                // the binder's ref-mapping on the Neo cursor (a Neo-cursor binder
                // API does not exist yet); emit a clear Step-13b NIE for that case
                // so it fails loudly instead of silently mis-reading GC refs.
                if (NeoBindingHasReferenceField(pt))
                {
                    sb.AppendLine($"            {realClsName} {varName} = default({realClsName});");
                    sb.AppendLine($"            throw new NotImplementedException(\"CLR value type with reference fields via binder in Neo autogen (Step 13b): register a flat-bytes binder path. Type: {pt.FullName}\");");
                }
                else
                {
                    sb.AppendLine($"            int __sz_{idx} = ILIntepreter.GetNeoValueTypeManagedSize(typeof({realClsName}));");
                    sb.AppendLine($"            {realClsName} {varName} = ({realClsName})ILIntepreter.ReadNeoValueType(typeof({realClsName}), __frameBase, ref __curPrim, __sz_{idx});");
                }
            }
            else
            {
                // Step 13 Area 4c (D2): the `pt.IsByRef` arm that was here is DEAD
                // (`pt` is de-byref'd at the top, so pt.IsByRef is always false).
                // A byref PARAM is now handled by the per-param write-back epilogue
                // (AppendNeoWriteBackCode) keyed on `p.IsByRef`, and the READ below
                // dispatches on the element type (`pt`) exactly like a by-value
                // param -- the dest slot is sized by the element type + deref'd by
                // CopyNeoCallArguments. The `pt == typeof(TypedReference)` shape
                // stays a default+TODO (genuinely unsupported).
                if (pt == typeof(System.TypedReference))
                {
                    sb.AppendLine($"            {realClsName} {varName} = default({realClsName});");
                    sb.AppendLine("            // TODO: TypedReference param in Neo (rare; not a byref marshal).");
                }
                else if (pt.IsValueType && !pt.IsPrimitive && !pt.IsEnum)
                {
                    // Step 13b (D5): CLR struct param WITHOUT a binder. A struct
                    // WITH reference fields cannot be read (GC refs unmappable) ->
                    // clear Step-13b NIE; a pure-primitive struct reads via the
                    // shared ReadNeoValueType helper (byte-consistent with the
                    // callee layout by construction -- same size source).
                    if (NeoBindingHasReferenceField(pt))
                    {
                        sb.AppendLine($"            {realClsName} {varName} = default({realClsName});");
                        sb.AppendLine($"            throw new NotImplementedException(\"CLR value type with reference fields and no ValueTypeBinder (Step 13b): register a binder. Type: {pt.FullName}\");");
                    }
                    else
                    {
                        sb.AppendLine($"            int __sz_{idx} = ILIntepreter.GetNeoValueTypeManagedSize(typeof({realClsName}));");
                        sb.AppendLine($"            {realClsName} {varName} = ({realClsName})ILIntepreter.ReadNeoValueType(typeof({realClsName}), __frameBase, ref __curPrim, __sz_{idx});");
                    }
                }
                else
                {
                    if (pt.IsPrimitive || pt.IsEnum)
                    {
                        if (pt == typeof(int) || pt.IsEnum) { sb.AppendLine($"            {realClsName} {varName} = ({realClsName})ILIntepreter.ReadNeoInt32(__frameBase, ref __curPrim);"); }
                        else if (pt == typeof(long)) { sb.AppendLine($"            {realClsName} {varName} = ILIntepreter.ReadNeoInt64(__frameBase, ref __curPrim);"); }
                        else if (pt == typeof(float)) { sb.AppendLine($"            {realClsName} {varName} = ILIntepreter.ReadNeoFloat(__frameBase, ref __curPrim);"); }
                        else if (pt == typeof(double)) { sb.AppendLine($"            {realClsName} {varName} = ILIntepreter.ReadNeoDouble(__frameBase, ref __curPrim);"); }
                        else if (pt == typeof(bool)) { sb.AppendLine($"            {realClsName} {varName} = ILIntepreter.ReadNeoBoolean(__frameBase, ref __curPrim);"); }
                        else if (pt == typeof(byte)) { sb.AppendLine($"            {realClsName} {varName} = ILIntepreter.ReadNeoUInt8(__frameBase, ref __curPrim);"); }
                        else if (pt == typeof(sbyte)) { sb.AppendLine($"            {realClsName} {varName} = ILIntepreter.ReadNeoInt8(__frameBase, ref __curPrim);"); }
                        else if (pt == typeof(short)) { sb.AppendLine($"            {realClsName} {varName} = ILIntepreter.ReadNeoInt16(__frameBase, ref __curPrim);"); }
                        else if (pt == typeof(ushort)) { sb.AppendLine($"            {realClsName} {varName} = ILIntepreter.ReadNeoUInt16(__frameBase, ref __curPrim);"); }
                        else if (pt == typeof(uint)) { sb.AppendLine($"            {realClsName} {varName} = ILIntepreter.ReadNeoUInt32(__frameBase, ref __curPrim);"); }
                        else if (pt == typeof(ulong)) { sb.AppendLine($"            {realClsName} {varName} = ILIntepreter.ReadNeoUInt64(__frameBase, ref __curPrim);"); }
                        else if (pt == typeof(char)) { sb.AppendLine($"            {realClsName} {varName} = ILIntepreter.ReadNeoChar(__frameBase, ref __curPrim);"); }
                        else 
                        { 
                            sb.AppendLine($"            {realClsName} {varName} = default({realClsName});");
                            sb.AppendLine($"            // TODO: Primitive {pt.Name}"); 
                        }
                    }
                    else
                    {
                        // Step 19: a delegate-typed param arrives as an
                        // IDelegateAdapter (the bridge), not a real CLR delegate.
                        // Unwrap via CheckCLRTypes(TypeFlags.IsDelegate) -- mirrors
                        // the Legacy AppendArgumentCode which emits CheckCLRTypes
                        // for delegate params. Without this, passing an IL delegate
                        // to a CLR method (e.g. List.ForEach(action)) throws.
                        if (typeof(Delegate).IsAssignableFrom(pt))
                        {
                            sb.AppendLine($"            {realClsName} {varName} = ({realClsName})typeof({realClsName}).CheckCLRTypes(ILIntepreter.ReadNeoReference(__frameBase, ref __curPrim, __mStack), (ILRuntime.CLR.Utils.Extensions.TypeFlags)8);");
                        }
                        else
                        {
                            sb.AppendLine($"            {realClsName} {varName} = ({realClsName})ILIntepreter.ReadNeoReference(__frameBase, ref __curPrim, __mStack);");
                        }
                    }
                }
            }
        }

        // Step 13 Area 4c: the autogen write-back epilogue. After the CLR call, for
        // each ref/out param, write the (possibly-mutated) local back into the
        // callee param region at the offset captured in AppendArgumentCodeNeo
        // (__off_<idx>). The post-call reverse copy (CopyNeoCallThisBack) then
        // propagates it to the caller's local/field. The element-type write mirrors
        // the reflection path's write-back (primitive/enum/struct via
        // WriteNeoValueType; reference via the mStack index). An `in`-only param is
        // NOT written back (CLR contract forbids mutation -- the gate).
        internal static void AppendNeoWriteBackCode(this StringBuilder sb, System.Reflection.ParameterInfo[] param, bool isMultiArr)
        {
            if (param == null)
                return;
            for (int j = 0; j < param.Length; j++)
            {
                var p = param[j];
                if (!p.ParameterType.IsByRef)
                    continue;
                // D5: write back for ref/out, NOT for in-only.
                if (p.IsIn && !p.IsOut)
                    continue;
                var elem = p.ParameterType.GetElementType();
                int idx = j + 1; // AppendArgumentCodeNeo uses idx = j + 1 (slot 0 = this)
                string varName = isMultiArr ? ("a" + idx) : ("@" + p.Name);
                string offVar = "__off_" + idx;
                if (elem.IsValueType)
                {
                    string szVar = "__wb_sz_" + idx;
                    sb.AppendLine($"            int {szVar} = ILIntepreter.GetNeoValueTypeManagedSize(typeof({elem.FullName}));");
                    sb.AppendLine($"            ILIntepreter.WriteNeoValueType({varName}, __frameBase + {offVar}, {szVar});");
                }
                else
                {
                    // reference-type element: store the mStack index (park the object).
                    sb.AppendLine($"            if ({varName} == null) *(int*)(__frameBase + {offVar}) = -1;");
                    sb.AppendLine($"            else {{ int __wb_idx = __mStack.Count; __mStack.Add({varName}); *(int*)(__frameBase + {offVar}) = __wb_idx; }}");
                }
            }
        }

        internal static void AppendArgumentCode(this Type p, StringBuilder sb, int idx, string name, List<Type> valueTypeBinders, bool isMultiArr, bool hasByRef, bool needFree)
        {
            string clsName, realClsName;
            bool isByRef;
            p.GetClassName(out clsName, out realClsName, out isByRef);
            var pt = p.IsByRef ? p.GetElementType() : p;
            string shouldFreeParam = hasByRef ? "false" : "true";

            if (pt.IsValueType && !pt.IsPrimitive && valueTypeBinders != null && valueTypeBinders.Contains(pt))
            {
                if (isMultiArr)
                    sb.AppendLine(string.Format("            {0} a{1} = new {0}();", realClsName, idx));
                else
                    sb.AppendLine(string.Format("            {0} @{1} = new {0}();", realClsName, name));

                sb.AppendLine(string.Format("            if (ILRuntime.Runtime.Generated.CLRBindings.s_{0}_Binder != null) {{", clsName));

                if (isMultiArr)
                    sb.AppendLine(string.Format("                ILRuntime.Runtime.Generated.CLRBindings.s_{1}_Binder.ParseValue(ref a{0}, __intp, ptr_of_this_method, __mStack, {2});", idx, clsName, shouldFreeParam));
                else
                    sb.AppendLine(string.Format("                ILRuntime.Runtime.Generated.CLRBindings.s_{1}_Binder.ParseValue(ref @{0}, __intp, ptr_of_this_method, __mStack, {2});", name, clsName, shouldFreeParam));

                sb.AppendLine("            } else {");

                if (isByRef)
                    sb.AppendLine("                ptr_of_this_method = ILIntepreter.GetObjectAndResolveReference(ptr_of_this_method);");
                if (isMultiArr)
                    sb.AppendLine(string.Format("                a{0} = {1};", idx, p.GetRetrieveValueCode(realClsName)));
                else
                    sb.AppendLine(string.Format("                @{0} = {1};", name, p.GetRetrieveValueCode(realClsName)));
                if (!hasByRef && needFree)
                    sb.AppendLine("                __intp.Free(ptr_of_this_method);");

                sb.AppendLine("            }");
            }
            else
            {
                if (isByRef)
                {
                    if (p.GetElementType().IsPrimitive)
                    {
                        if (pt == typeof(int) || pt == typeof(uint) || pt == typeof(short) || pt == typeof(ushort) || pt == typeof(byte) || pt == typeof(sbyte) || pt == typeof(char))
                        {
                            if (pt == typeof(int))
                                sb.AppendLine(string.Format("            {0} @{1} = __intp.RetriveInt32(ptr_of_this_method, __mStack);", realClsName, name));
                            else
                                sb.AppendLine(string.Format("            {0} @{1} = ({0})__intp.RetriveInt32(ptr_of_this_method, __mStack);", realClsName, name));
                        }
                        else if (pt == typeof(long) || pt == typeof(ulong))
                        {
                            if (pt == typeof(long))
                                sb.AppendLine(string.Format("            {0} @{1} = __intp.RetriveInt64(ptr_of_this_method, __mStack);", realClsName, name));
                            else
                                sb.AppendLine(string.Format("            {0} @{1} = ({0})__intp.RetriveInt64(ptr_of_this_method, __mStack);", realClsName, name));
                        }
                        else if (pt == typeof(float))
                        {
                            sb.AppendLine(string.Format("            {0} @{1} = __intp.RetriveFloat(ptr_of_this_method, __mStack);", realClsName, name));
                        }
                        else if (pt == typeof(double))
                        {
                            sb.AppendLine(string.Format("            {0} @{1} = __intp.RetriveDouble(ptr_of_this_method, __mStack);", realClsName, name));
                        }
                        else if (pt == typeof(bool))
                        {
                            sb.AppendLine(string.Format("            {0} @{1} = __intp.RetriveInt32(ptr_of_this_method, __mStack) == 1;", realClsName, name));
                        }
                        else
                            throw new NotSupportedException();
                    }
                    else if (p.GetElementType().IsEnum)
                    {
                        sb.AppendLine(string.Format("            {0} @{1} = ({0})__intp.RetriveInt32(ptr_of_this_method, __mStack);", realClsName, name));
                    }
                    else
                    {
                        sb.AppendLine(string.Format("            {0} @{1} = ({0})typeof({0}).CheckCLRTypes(__intp.RetriveObject(ptr_of_this_method, __mStack), (ILRuntime.CLR.Utils.Extensions.TypeFlags){2});", realClsName, name, (int)p.GetTypeFlagsRecursive()));
                    }

                }
                else
                {
                    if (isMultiArr)
                        sb.AppendLine(string.Format("            {0} a{1} = {2};", realClsName, idx, p.GetRetrieveValueCode(realClsName)));
                    else
                        sb.AppendLine(string.Format("            {0} @{1} = {2};", realClsName, name, p.GetRetrieveValueCode(realClsName)));
                    if (!hasByRef && !p.IsPrimitive && needFree)
                        sb.AppendLine("            __intp.Free(ptr_of_this_method);");

                }
            }
        }

        internal static string GetRetrieveValueCode(this Type type, string realClsName)
        {
            if (type.IsByRef)
                type = type.GetElementType();
            if (type.IsPrimitive)
            {
                if (type == typeof(int))
                {
                    return "ptr_of_this_method->Value";
                }
                else if (type == typeof(long))
                {
                    return "*(long*)&ptr_of_this_method->Value";
                }
                else if (type == typeof(short))
                {
                    return "(short)ptr_of_this_method->Value";
                }
                else if (type == typeof(bool))
                {
                    return "ptr_of_this_method->Value == 1";
                }
                else if (type == typeof(ushort))
                {
                    return "(ushort)ptr_of_this_method->Value";
                }
                else if (type == typeof(float))
                {
                    return "*(float*)&ptr_of_this_method->Value";
                }
                else if (type == typeof(double))
                {
                    return "*(double*)&ptr_of_this_method->Value";
                }
                else if (type == typeof(byte))
                {
                    return "(byte)ptr_of_this_method->Value";
                }
                else if (type == typeof(sbyte))
                {
                    return "(sbyte)ptr_of_this_method->Value";
                }
                else if (type == typeof(uint))
                {
                    return "(uint)ptr_of_this_method->Value";
                }
                else if (type == typeof(char))
                {
                    return "(char)ptr_of_this_method->Value";
                }
                else if (type == typeof(ulong))
                {
                    return "*(ulong*)&ptr_of_this_method->Value";
                }
                else
                    throw new NotImplementedException();
            }
            else
            {
                return string.Format("({0})typeof({0}).CheckCLRTypes(StackObject.ToObject(ptr_of_this_method, __domain, __mStack), (ILRuntime.CLR.Utils.Extensions.TypeFlags){1})", realClsName, (int)type.GetTypeFlagsRecursive());
            }
        }

        internal static void GetRefWriteBackValueCode(this Type type, StringBuilder sb, string paramName)
        {
            if (type.IsPrimitive)
            {
                if (type == typeof(int))
                {
                    sb.AppendLine("                        ___dst->ObjectType = ObjectTypes.Integer;");
                    sb.Append("                        ___dst->Value = @" + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(long))
                {
                    sb.AppendLine("                        ___dst->ObjectType = ObjectTypes.Long;");
                    sb.Append("                        *(long*)&___dst->Value = @" + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(short))
                {
                    sb.AppendLine("                        ___dst->ObjectType = ObjectTypes.Integer;");
                    sb.Append("                        ___dst->Value = @" + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(bool))
                {
                    sb.AppendLine("                        ___dst->ObjectType = ObjectTypes.Integer;");
                    sb.Append("                        ___dst->Value = @" + paramName + " ? 1 : 0;");
                    sb.AppendLine(";");
                }
                else if (type == typeof(ushort))
                {
                    sb.AppendLine("                        ___dst->ObjectType = ObjectTypes.Integer;");
                    sb.Append("                        ___dst->Value = @" + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(float))
                {
                    sb.AppendLine("                        ___dst->ObjectType = ObjectTypes.Float;");
                    sb.Append("                        *(float*)&___dst->Value = @" + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(double))
                {
                    sb.AppendLine("                        ___dst->ObjectType = ObjectTypes.Double;");
                    sb.Append("                        *(double*)&___dst->Value = @" + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(byte))
                {
                    sb.AppendLine("                        ___dst->ObjectType = ObjectTypes.Integer;");
                    sb.Append("                        ___dst->Value = @" + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(sbyte))
                {
                    sb.AppendLine("                        ___dst->ObjectType = ObjectTypes.Integer;");
                    sb.Append("                        ___dst->Value = @" + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(uint))
                {
                    sb.AppendLine("                        ___dst->ObjectType = ObjectTypes.Integer;");
                    sb.Append("                        ___dst->Value = (int)@" + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(char))
                {
                    sb.AppendLine("                        ___dst->ObjectType = ObjectTypes.Integer;");
                    sb.Append("                        ___dst->Value = (int)@" + paramName);
                    sb.AppendLine(";");
                }
                else if (type == typeof(ulong))
                {
                    sb.AppendLine("                        ___dst->ObjectType = ObjectTypes.Long;");
                    sb.Append("                        *(ulong*)&___dst->Value = @" + paramName);
                    sb.AppendLine(";");
                }
                else
                    throw new NotImplementedException();
            }
            else if(type.IsEnum)
            {
                sb.AppendLine("                        ___dst->ObjectType = ObjectTypes.Integer;");
                sb.Append("                        ___dst->Value = (int)@" + paramName);
                sb.AppendLine(";");
            }
            else
            {
                sb.Append(@"                        object ___obj = @");
                sb.Append(paramName);
                sb.AppendLine(";");
                sb.AppendLine(@"                        if (___dst->ObjectType >= ObjectTypes.Object)
                        {
                            if (___obj is CrossBindingAdaptorType)
                                ___obj = ((CrossBindingAdaptorType)___obj).ILInstance;
                            __mStack[___dst->Value] = ___obj;
                        }
                        else
                        {
                            ILIntepreter.UnboxObject(___dst, ___obj, __mStack, __domain);
                        }");
                /*if (!type.IsValueType)
                {
                    sb.Append(@"                        object ___obj = ");
                    sb.Append(paramName);
                    sb.AppendLine(";");

                    sb.AppendLine(@"                        if (___obj is CrossBindingAdaptorType)
                            ___obj = ((CrossBindingAdaptorType)___obj).ILInstance;
                        __mStack[___dst->Value] = ___obj; ");
                }
                else
                {
                    sb.Append("                        __mStack[___dst->Value] = ");
                    sb.Append(paramName);
                    sb.AppendLine(";");
                }*/
            }
        }

        internal static void GetReturnValueCodeNeo(this Type type, StringBuilder sb)
        {
            if (type.IsPrimitive || type.IsEnum)
            {
                if (type == typeof(int) || type.IsEnum) sb.AppendLine("            if (__retDst != null) *(int*)__retDst = (int)result_of_this_method;");
                else if (type == typeof(long)) sb.AppendLine("            if (__retDst != null) *(long*)__retDst = (long)result_of_this_method;");
                else if (type == typeof(float)) sb.AppendLine("            if (__retDst != null) *(float*)__retDst = (float)result_of_this_method;");
                else if (type == typeof(double)) sb.AppendLine("            if (__retDst != null) *(double*)__retDst = (double)result_of_this_method;");
                else if (type == typeof(bool)) sb.AppendLine("            if (__retDst != null) *(int*)__retDst = result_of_this_method ? 1 : 0;");
                else if (type == typeof(byte)) sb.AppendLine("            if (__retDst != null) *(int*)__retDst = (byte)result_of_this_method;");
                else if (type == typeof(sbyte)) sb.AppendLine("            if (__retDst != null) *(int*)__retDst = (sbyte)result_of_this_method;");
                else if (type == typeof(short)) sb.AppendLine("            if (__retDst != null) *(int*)__retDst = (short)result_of_this_method;");
                else if (type == typeof(ushort)) sb.AppendLine("            if (__retDst != null) *(int*)__retDst = (ushort)result_of_this_method;");
                else if (type == typeof(uint)) sb.AppendLine("            if (__retDst != null) *(uint*)__retDst = (uint)result_of_this_method;");
                else if (type == typeof(ulong)) sb.AppendLine("            if (__retDst != null) *(ulong*)__retDst = (ulong)result_of_this_method;");
                else if (type == typeof(char)) sb.AppendLine("            if (__retDst != null) *(int*)__retDst = (char)result_of_this_method;");
                else sb.AppendLine($"            throw new NotImplementedException(\"Primitive return {type.Name}\");");
            }
            else if (type.IsValueType)
            {
                // Step 13b (D5): write a CLR struct return value's flat managed
                // bytes into the caller's dest slot via the shared WriteNeoValueType
                // (the inverse of the param read path -- same size source, so the
                // write matches the caller's dest layout by construction). A struct
                // WITH reference fields and no binder is unwriteable the same way it
                // is unreadable; emit a clear Step-13b NIE for that case.
                if (!type.IsPrimitive && !type.IsEnum && NeoBindingHasReferenceField(type))
                {
                    sb.AppendLine($"            throw new NotImplementedException(\"CLR value-type return with reference fields and no ValueTypeBinder (Step 13b): register a binder. Type: {type.FullName}\");");
                }
                else
                {
                    sb.AppendLine($"            if (__retDst != null) {{ int __retSz = ILIntepreter.GetNeoValueTypeManagedSize(typeof({type.FullName})); ILIntepreter.WriteNeoValueType(result_of_this_method, __retDst, __retSz); }}");
                }
            }
            else
            {
                sb.AppendLine(@"            if (__retDst != null)
            {
                if (__retRefBase >= __mStack.Count)
                    __mStack.Add(result_of_this_method);
                else
                    __mStack[__retRefBase] = result_of_this_method;
                *(int*)__retDst = __retRefBase;
            }");
            }
        }

        internal static void GetReturnValueCode(this Type type, StringBuilder sb, Enviorment.AppDomain domain)
        {
            if (type.IsPrimitive)
            {
                if (type == typeof(int))
                {
                    sb.AppendLine("            __ret->ObjectType = ObjectTypes.Integer;");
                    sb.AppendLine("            __ret->Value = result_of_this_method;");
                }
                else if (type == typeof(long))
                {
                    sb.AppendLine("            __ret->ObjectType = ObjectTypes.Long;");
                    sb.AppendLine("            *(long*)&__ret->Value = result_of_this_method;");
                }
                else if (type == typeof(short))
                {
                    sb.AppendLine("            __ret->ObjectType = ObjectTypes.Integer;");
                    sb.AppendLine("            __ret->Value = result_of_this_method;");
                }
                else if (type == typeof(bool))
                {
                    sb.AppendLine("            __ret->ObjectType = ObjectTypes.Integer;");
                    sb.AppendLine("            __ret->Value = result_of_this_method ? 1 : 0;");
                }
                else if (type == typeof(ushort))
                {
                    sb.AppendLine("            __ret->ObjectType = ObjectTypes.Integer;");
                    sb.AppendLine("            __ret->Value = result_of_this_method;");
                }
                else if (type == typeof(float))
                {
                    sb.AppendLine("            __ret->ObjectType = ObjectTypes.Float;");
                    sb.AppendLine("            *(float*)&__ret->Value = result_of_this_method;");
                }
                else if (type == typeof(double))
                {
                    sb.AppendLine("            __ret->ObjectType = ObjectTypes.Double;");
                    sb.AppendLine("            *(double*)&__ret->Value = result_of_this_method;");
                }
                else if (type == typeof(byte))
                {
                    sb.AppendLine("            __ret->ObjectType = ObjectTypes.Integer;");
                    sb.AppendLine("            __ret->Value = result_of_this_method;");
                }
                else if (type == typeof(sbyte))
                {
                    sb.AppendLine("            __ret->ObjectType = ObjectTypes.Integer;");
                    sb.AppendLine("            __ret->Value = result_of_this_method;");
                }
                else if (type == typeof(uint))
                {
                    sb.AppendLine("            __ret->ObjectType = ObjectTypes.Integer;");
                    sb.AppendLine("            __ret->Value = (int)result_of_this_method;");
                }
                else if (type == typeof(char))
                {
                    sb.AppendLine("            __ret->ObjectType = ObjectTypes.Integer;");
                    sb.AppendLine("            __ret->Value = (int)result_of_this_method;");
                }
                else if (type == typeof(ulong))
                {
                    sb.AppendLine("            __ret->ObjectType = ObjectTypes.Long;");
                    sb.AppendLine("            *(ulong*)&__ret->Value = result_of_this_method;");
                }
                else
                    throw new NotImplementedException();
                sb.AppendLine("            return __ret + 1;");

            }
            else
            {
                string isBox;
                if (type == typeof(object))
                    isBox = ", true";
                else
                    isBox = "";
                if (!type.IsSealed && type != typeof(ILRuntime.Runtime.Intepreter.ILTypeInstance))
                {
                    if(domain == null || CheckAssignableToCrossBindingAdapters(domain, type))
                    {
                        sb.Append(@"            object obj_result_of_this_method = result_of_this_method;
            if(obj_result_of_this_method is CrossBindingAdaptorType)
            {    
                return ILIntepreter.PushObject(__ret, __mStack, ((CrossBindingAdaptorType)obj_result_of_this_method).ILInstance");
                        sb.Append(isBox);
                        sb.AppendLine(@");
            }");
                    }
                    else if (typeof(CrossBindingAdaptorType).IsAssignableFrom(type))
                    {
                        sb.AppendLine(string.Format("            return ILIntepreter.PushObject(__ret, __mStack, result_of_this_method.ILInstance{0});", isBox));
                        return;
                    }
                    
                }
                sb.AppendLine(string.Format("            return ILIntepreter.PushObject(__ret, __mStack, result_of_this_method{0});", isBox));
            }
        }

        static bool CheckAssignableToCrossBindingAdapters(Enviorment.AppDomain domain, Type type, HashSet<Type> crossbindingTypes=null)
        {
            if (type == typeof(object))
                return true;
            if(crossbindingTypes == null)
            {
                crossbindingTypes = new HashSet<Type>();
                foreach (var i in domain.CrossBindingAdaptors)
                {
                    crossbindingTypes.Add(i.Key);
                    List<Type> bases = new List<Type>();
                    bases.Add(i.Key.BaseType);
                    bases.AddRange(i.Key.GetInterfaces());
                    foreach (var t in bases)
                    {
                        var curT = t;
                        while (curT != null && curT != typeof(object))
                        {
                            crossbindingTypes.Add(curT);
                            curT = curT.BaseType;
                        }
                    }
                }
            }
            bool res = crossbindingTypes.Contains(type);
            if (!res)
            {
                var baseType = type.BaseType;
                if (baseType != null && baseType != typeof(object))
                {
                    res = CheckAssignableToCrossBindingAdapters(domain, baseType, crossbindingTypes);
                }
            }
            if (!res)
            {
                var interfaces = type.GetInterfaces();
                foreach(var i in interfaces)
                {
                    res = CheckAssignableToCrossBindingAdapters(domain, i, crossbindingTypes);
                    if (res)
                        break;
                }
            }
            if (res)
            {

            }
            return res;
        }

        internal static bool HasByRefParam(this ParameterInfo[] param)
        {
            for (int j = param.Length; j > 0; j--)
            {
                var p = param[j - 1];
                if (p.ParameterType.IsByRef)
                    return true;
            }
            return false;
        }
    }
}
