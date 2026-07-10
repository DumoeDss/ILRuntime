#if ENABLE_NEO_MODE
using System;
using System.Collections.Generic;

using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;

namespace ILRuntime.Runtime.NeoAOT
{
    // ===== Step 25: the runtime .neo -> ILMethod binder (S1) =====
    //
    // The consumer of the Step-23 .neo format + the Step-24 NeoCompiler driver.
    // Attach(appdomain, model) binds each deserialized NeoMethodDefRecord to a
    // LIVE ILMethod in the SAME AppDomain that compiled the .neo, and populates
    // its CompiledFrame from the record via InitCodeBodyFromNeo (bypass JIT).
    // The runtime token hash maps (mapTypeToken / mapMethod), populated at
    // Cecil-load + JIT-compile time in this same AppDomain, resolve the
    // deserialized bodies' token operands NATURALLY -- S1 does NO cross-
    // AppDomain hash re-resolution (deferred to S3).
    //
    // S1 operates on NON-GENERIC methods only. A NeoMethodDefRecord is always
    // non-generic by the Step-24 partition (generic definitions live in the
    // TemplateTable, which S1 does NOT consume -- deferred to S2). A type not
    // loaded, or a method that does not match, is recorded in the skip report
    // and omitted -- the method KEEPS its JIT path (the additive contract). The
    // loader NEVER aborts on a miss.
    //
    // Additive + Neo-only: the whole class is #if ENABLE_NEO_MODE. No JIT /
    // runtime / ExecuteNeo / Step-22 / Step-23 / Step-24 behavior change.
    internal static class NeoAssemblyLoader
    {
        /// <summary>
        /// Bind each NeoMethodDefRecord in `model` to a live ILMethod in
        /// `appdomain` and populate its CompiledFrame from the .neo (bypass JIT).
        /// Same-AppDomain: the deserialized bodies' token operands resolve via
        /// the EXISTING hash maps. Returns a report of attached / skipped methods.
        /// </summary>
        internal static NeoLoadReport Attach(ILRuntime.Runtime.Enviorment.AppDomain appdomain,
                                             NeoAssemblyModel model)
        {
            var report = new NeoLoadReport();
            if (appdomain == null || model == null || model.MethodDefs == null)
                return report;

            // ===== Step 25 (neo-aot-delegate-exe-parity): re-register the .neo's
            // APPROACH-1 token bindings into THIS execution AppDomain's mapTypeToken
            // / mapMethod BEFORE binding any bodies. A .neo compiled in a DIFFERENT
            // AppDomain (the normal case -- ilrt_neoc builds a fresh AppDomain, and a
            // host-side Compile(testCasesDllPath, ...) does too) bakes method/type
            // token hashes from the COMPILE AppDomain. Those hashes are absent from
            // the execution AppDomain's maps, so every Ldftn/Call/Callvirt/Newobj/
            // Ldvirtftn operand in the attached bodies resolved to NULL and was
            // silently skipped at ExecuteNeo (the `if (targetMethod == null) ip++;
            // continue` guard) -- producing wrong results (e.g. a delegate Invoke
            // skipped -> the dest register stayed stale -> a downstream DivideByZero
            // on a wrong-result assertion). Re-registering the bindings under their
            // recorded compile-time hashes (re-resolved by NAME in THIS AppDomain)
            // makes the baked operands resolve. This is the SAME pass LoadNeoAssembly
            // runs for the Cecil-free path (AppDomain.ReRegisterTokenBindings); Attach
            // -- the SAME-AppDomain S1 path -- previously skipped it, which is why the
            // byref-wireup probe (Compile in a fresh AppDomain + Attach to the session
            // AppDomain) surfaced delegate/callback shapes failing on AOT-exec while
            // passing on JIT. Idempotent + Neo-only; a binding whose name does not
            // resolve here is skipped (the additive contract). =====
            appdomain.ReRegisterTokenBindings(model);

            // The catch-type resolver closure (TypeRef idx -> runtime IType). IL
            // catch types resolve via LoadedTypes; CLR catch types via GetType
            // (aqname / assembly scan). Handed to InitCodeBodyFromNeo so the EH
            // rebuild can populate CatchType for CheckExceptionType matching.
            Func<int, IType> resolveCatchType = idx => ResolveTypeRefToIType(appdomain, model, idx);

            foreach (var rec in model.MethodDefs)
            {
                if (rec.MethodRefIdx < 0 || rec.MethodRefIdx >= model.MethodRefs.Length)
                {
                    report.Skipped.Add(("bad MethodRefIdx", "#" + rec.MethodRefIdx));
                    continue;
                }
                var mr = model.MethodRefs[rec.MethodRefIdx];
                // Defensive: skip a generic-method instance (a runtime artifact,
                // absent from MethodDefs by the Step-24 partition).
                if (mr.IsGenericInstance)
                {
                    report.Skipped.Add(("generic instance (S2)", mr.Name ?? "?"));
                    continue;
                }
                string typeFullName = mr.DeclaringType != null ? mr.DeclaringType.Name : null;
                if (string.IsNullOrEmpty(typeFullName))
                {
                    report.Skipped.Add(("empty declaring type", mr.Name ?? "?"));
                    continue;
                }
                if (!appdomain.LoadedTypes.TryGetValue(typeFullName, out var itype) || !(itype is ILType iltype))
                {
                    report.Skipped.Add(("type not loaded", typeFullName));
                    continue;
                }
                int wantParam = mr.Parameters != null ? mr.Parameters.Length : 0;
                var ilm = MatchMethod(iltype, mr.Name, wantParam);
                if (ilm == null)
                {
                    report.Skipped.Add(("method not matched", typeFullName + "." + mr.Name));
                    continue;
                }
                // Defensive: a generic DEFINITION routes to the TemplateTable,
                // NOT MethodDefs -- tolerate a malformed .neo by skipping.
                if (ilm.GenericParameterCount > 0)
                {
                    report.Skipped.Add(("generic definition (S2)", typeFullName + "." + mr.Name));
                    continue;
                }
                ilm.InitCodeBodyFromNeo(rec, resolveCatchType);
                report.Attached.Add(typeFullName + "." + mr.Name);
            }

            // ===== Step 25 S2: consume the .neo TemplateTable. For each
            // NeoTemplateRecord, reconstruct the GenericMethodTemplate + bind it to
            // the matching open generic-method DEFINITION's GenericMethodTemplateCache
            // (OVERWRITING any JIT-captured template). A subsequent generic-instance
            // call then routes through Step-22 CloneAndPatch from the AOT template
            // instead of per-occurrence JIT. A miss (type not loaded, generic def not
            // matched, T-identity-token patch, or a VariableTypes re-resolution miss)
            // is SKIPPED -- the generic method keeps JIT (the additive contract). =====
            if (model.Templates != null)
            {
                foreach (var trec in model.Templates)
                {
                    if (trec.DefinitionMethodRefIdx < 0 || trec.DefinitionMethodRefIdx >= model.MethodRefs.Length)
                    {
                        report.Skipped.Add(("bad DefinitionMethodRefIdx", "#" + trec.DefinitionMethodRefIdx));
                        continue;
                    }
                    var mref = model.MethodRefs[trec.DefinitionMethodRefIdx];
                    string typeFullName = mref.DeclaringType != null ? mref.DeclaringType.Name : null;
                    if (string.IsNullOrEmpty(typeFullName))
                    {
                        report.Skipped.Add(("template empty declaring type", mref.Name ?? "?"));
                        continue;
                    }
                    if (!appdomain.LoadedTypes.TryGetValue(typeFullName, out var itype) || !(itype is ILType iltype))
                    {
                        report.Skipped.Add(("template type not loaded", typeFullName));
                        continue;
                    }
                    int wantParam = mref.Parameters != null ? mref.Parameters.Length : 0;
                    // V5 (neo-aot-generic-cecilfree): a Cecil-free generic-def SHELL
                    // cannot report GenericParameterCount > 0 (the .neo-stamped names
                    // are not yet on the shell), so the Cecil-present
                    // MatchGenericDefinition (which requires GenericParameterCount > 0)
                    // never matches. The Cecil-free matcher matches by name +
                    // param-count + !IsGenericInstance, THEN stamps the .neo
                    // GenericParamNames onto the shell (so GenericParameterCount > 0
                    // for the rest of the engine).
                    var def = MatchGenericDefinitionCecilFree(iltype, mref.Name, wantParam);
                    if (def == null)
                    {
                        // The generic DEFINITION shell is NOT created at Cecil-free
                        // type build (the .neo MethodDefs table carries NON-generic
                        // methods only by the Step-24 partition; generic defs live
                        // ONLY in the TemplateTable). The template's MethodRef IS the
                        // open def's ref -- build the def shell from it + register it
                        // on the type so the S2 bind + runtime generic dispatch find it.
                        def = BuildAndRegisterGenericDefShell(iltype, mref, wantParam);
                        if (def == null)
                        {
                            report.Skipped.Add(("generic def not matched", typeFullName + "." + (mref.Name ?? "?")));
                            continue;
                        }
                    }
                    // Stamp the .neo generic-param names onto the shell BEFORE the
                    // VariableType re-resolution (ResolveVariableType reads them).
                    var gpn = trec.GenericParamNames;
                    def.SetNeoShellGenericParamNames(gpn != null && gpn.Length > 0 ? gpn : null);
                    // V5: set the open def's return type from the template's
                    // ReturnTypeRefIdx (a generic-param return -> ILGenericParameterType
                    // so the instance's MakeGenericMethodShell substitutes the concrete
                    // arg; a fixed return -> the resolved CLR/IL type). The MethodRef
                    // omits the return type; the .neo V5 template carries it.
                    var retType = ResolveReturnTypeFromTemplate(appdomain, model, trec, def);
                    if (retType != null) def.SetNeoShellReturnType(retType);
                    // The VariableTypes re-resolution closure (TypeRef idx -> Cecil
                    // TypeReference, same-AppDomain). A generic-parameter name maps to
                    // definition.Definition.GenericParameters[k]; an IL type to
                    // iltype.TypeReference; a CLR type to ImportReference(TypeForCLR).
                    // A miss returns null -> BuildFromNeoRecord skips the bind.
                    Func<int, ILRuntime.Mono.Cecil.TypeReference> resolveVariableType = idx =>
                        ResolveVariableType(appdomain, model, def, idx);
                    // V6 (MethodToken T-identity): the MethodRef re-resolution closure
                    // (MethodRef idx -> Cecil MethodReference, Cecil-free). A T-
                    // qualified method token (a constrained. T callvirt) is re-resolved
                    // into PatchEntry.CecilToken so DoCloneAndPatch's existing
                    // GetMethodTokenHash path re-derives the concrete-T method hash.
                    Func<int, ILRuntime.Mono.Cecil.MethodReference> resolveMethodRef = idx =>
                        ResolveMethodRef(idx, model);
                    var tpl = Runtime.Intepreter.RegisterVM.GenericMethodTemplateOps.BuildFromNeoRecord(
                        def, appdomain, model, trec, resolveVariableType, resolveMethodRef);
                    if (tpl == null)
                    {
                        report.Skipped.Add(("template rebuild miss (S3 case)", typeFullName + "." + (mref.Name ?? "?")));
                        continue;
                    }
                    def.InitTemplateFromNeo(tpl);
                    report.Attached.Add(typeFullName + "." + (mref.Name ?? "?") + "<T>");
                }
            }
            return report;
        }

        // Match by name + parameter count (V1; the S1 probe declares no colliding
        // overloads). GetMethods()/GetConstructors() return the type's OWN declared
        // methods only (no inherited). Generic instances + generic definitions are
        // skipped (S2). A collision (2+ matches) is reported as a miss -> skip
        // (additive contract; full signature matching is round-2).
        static ILMethod MatchMethod(ILType iltype, string name, int paramCount)
        {
            if (string.IsNullOrEmpty(name)) return null;
            ILMethod hit = null;
            if (iltype.GetMethods() != null)
            {
                foreach (var m in iltype.GetMethods())
                {
                    var ilm = m as ILMethod;
                    if (ilm == null || ilm.IsGenericInstance) continue;
                    if (ilm.GenericParameterCount > 0) continue;   // generic def -> S2
                    if (ilm.Name != name) continue;
                    if (ilm.ParameterCount != paramCount) continue;
                    if (hit != null) return null;                  // collision -> skip
                    hit = ilm;
                }
            }
            if (iltype.GetConstructors() != null)
            {
                foreach (var ilm in iltype.GetConstructors())
                {
                    if (ilm == null || ilm.IsGenericInstance) continue;
                    if (ilm.GenericParameterCount > 0) continue;
                    if (ilm.Name != name) continue;
                    if (ilm.ParameterCount != paramCount) continue;
                    if (hit != null) return null;
                    hit = ilm;
                }
            }
            return hit;
        }

        // Step 25 S2: match the OPEN generic-method DEFINITION by name + parameter
        // count + GenericParameterCount > 0 + !IsGenericInstance. The inverse of
        // MatchMethod's generic-def skip. Constructors are excluded (a generic .ctor
        // is not a method-template target). A collision (2+ matches) -> null -> skip
        // (additive contract; generic-arity disambiguation is round-2 -- the S2 probe
        // declares no same-name+same-paramcount generic overloads of different arity).
        static ILMethod MatchGenericDefinition(ILType iltype, string name, int paramCount)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (iltype.GetMethods() == null) return null;
            ILMethod hit = null;
            foreach (var m in iltype.GetMethods())
            {
                var ilm = m as ILMethod;
                if (ilm == null) continue;
                if (ilm.IsGenericInstance) continue;          // instance, not the def
                if (ilm.GenericParameterCount <= 0) continue;  // not a generic def
                if (ilm.Name != name) continue;
                if (ilm.ParameterCount != paramCount) continue;
                if (hit != null) return null;                  // collision -> skip
                hit = ilm;
            }
            return hit;
        }

        // V5 (neo-aot-generic-cecilfree): match the OPEN generic-method
        // DEFINITION by name + parameter count + !IsGenericInstance on a Cecil-
        // free shell. Unlike MatchGenericDefinition, this does NOT filter on
        // GenericParameterCount > 0 -- a Cecil-free generic-def shell cannot report
        // its arity until the .neo GenericParamNames are stamped (which the S2
        // caller does AFTER this match). A collision (2+ matches) -> null -> skip
        // (the additive contract). This is the Cecil-free twin of MatchGenericDefinition.
        static ILMethod MatchGenericDefinitionCecilFree(ILType iltype, string name, int paramCount)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (iltype.GetMethods() == null) return null;
            ILMethod hit = null;
            foreach (var m in iltype.GetMethods())
            {
                var ilm = m as ILMethod;
                if (ilm == null) continue;
                if (ilm.IsGenericInstance) continue;          // instance, not the def
                if (ilm.Name != name) continue;
                if (ilm.ParameterCount != paramCount) continue;
                // A Cecil-free OWN declared method that is NOT a generic def has
                // already been bound by the S1 Attach flow (an AOT body); skip it
                // (it is not a template target). The discrimiator: isNeoAotBody
                // (S1 bound a non-generic method body) vs a generic-def shell (no
                // AOT body -- S1 skips generic defs).
                if (ilm.IsNeoAotBodyBound) continue;
                if (hit != null) return null;                  // collision -> skip
                hit = ilm;
            }
            return hit;
        }

        // V5 (neo-aot-generic-cecilfree): build a Cecil-free generic-DEFINITION
        // shell from a .neo template's open-def MethodRef + register it on the
        // type. A generic def is absent from MethodDefs (Step-24 partition ->
        // generic defs live ONLY in the TemplateTable), so the Cecil-free type
        // build never created a shell for it. The shell's parameters are the
        // open def's params (a generic-param arg "T" -> an ILGenericParameterType
        // named "T"; a concrete type resolves by name). The return type is void
        // here (the MethodRef omits it; the instance's RunNeoBackHalf resolves the
        // concrete return via FindGenericArgument on the concrete arg). The caller
        // stamps GenericParamNames AFTER. Neo-only.
        static ILMethod BuildAndRegisterGenericDefShell(ILType iltype,
            ILRuntime.Hybrid.MethodReferencePatchInfo mref, int wantParam)
        {
            if (mref == null || string.IsNullOrEmpty(mref.Name)) return null;
            var domain = iltype.AppDomain;
            int pc = mref.Parameters != null ? mref.Parameters.Length : 0;
            var parameters = new List<IType>(pc);
            for (int i = 0; i < pc; i++)
            {
                var pi = mref.Parameters[i];
                string pname = pi != null ? pi.Name : null;
                parameters.Add(ResolveGenericDefParamType(domain, pname));
            }
            var shell = ILMethod.CreateFromNeoShell(mref.Name, iltype, domain, parameters,
                domain.VoidType, false, mref.IsStatic);
            iltype.AddNeoAotShell(mref.Name, shell);
            return shell;
        }

        // V5: resolve a generic-DEFINITION parameter type by name. A generic-param
        // name (e.g. "T") -> an ILGenericParameterType (so MakeGenericMethodShell's
        // SubstituteGenericParam can swap the concrete arg). A concrete type ->
        // LoadedTypes / GetType. null name -> null (the caller tolerates it).
        static IType ResolveGenericDefParamType(ILRuntime.Runtime.Enviorment.AppDomain domain, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            IType it;
            if (domain.LoadedTypes.TryGetValue(name, out it) && it != null) return it;
            try
            {
                var resolved = domain.GetType(name);
                if (resolved != null) return resolved;
            }
            catch { }
            // A name that is NOT a loaded type is a generic-parameter name -> an
            // ILGenericParameterType (the open def's param). MakeGenericMethodShell
            // substitutes it with the concrete arg.
            return new CLR.TypeSystem.ILGenericParameterType(name);
        }

        // V5 (neo-aot-generic-cecilfree): the synthetic GenericParameter owner.
        // A Cecil-free AppDomain has no Cecil ModuleDefinition/MethodDefinition to
        // own a GenericParameter, but GenericParameter's ctor requires a non-null
        // IGenericParameterProvider. This tiny owner satisfies the contract (the
        // GenericParameter is only ever read for Name + IsGenericParameter by the
        // Cecil-free generic path). One shared owner instance backs all synthetic
        // params (the Name distinguishes them). Neo-only.
        sealed class NeoSyntheticGenericParamOwner
            : ILRuntime.Mono.Cecil.IGenericParameterProvider
        {
            public bool HasGenericParameters { get { return false; } }
            public bool IsDefinition { get { return false; } }
            public ILRuntime.Mono.Cecil.ModuleDefinition Module { get { return null; } }
            public Mono.Collections.Generic.Collection<ILRuntime.Mono.Cecil.GenericParameter> GenericParameters
                { get { throw new NotSupportedException(); } }
            public ILRuntime.Mono.Cecil.GenericParameterType GenericParameterType
                { get { return ILRuntime.Mono.Cecil.GenericParameterType.Method; } }
            public ILRuntime.Mono.Cecil.MetadataToken MetadataToken { get; set; }
        }

        // V5: one shared owner (the GenericParameters getter is never called).
        static readonly NeoSyntheticGenericParamOwner s_syntheticGpOwner = new NeoSyntheticGenericParamOwner();
        // V5: cache Name -> synthetic GenericParameter. The same logical generic
        // param "T" must resolve to a STABLE Cecil object across the S2 bind +
        // the runtime back-half, or mapTypeToken.GetHashCode()-keyed lookups (which
        // use the GenericParameter's identity hash) would miss. One per name.
        static readonly System.Collections.Generic.Dictionary<string, ILRuntime.Mono.Cecil.GenericParameter> s_syntheticGps
            = new System.Collections.Generic.Dictionary<string, ILRuntime.Mono.Cecil.GenericParameter>();

        static ILRuntime.Mono.Cecil.GenericParameter AcquireSyntheticGenericParam(string name)
        {
            ILRuntime.Mono.Cecil.GenericParameter gp;
            if (!s_syntheticGps.TryGetValue(name, out gp))
            {
                gp = new ILRuntime.Mono.Cecil.GenericParameter(name, s_syntheticGpOwner);
                s_syntheticGps[name] = gp;
            }
            return gp;
        }

        // TypeReference in the SAME AppDomain. A generic-parameter name (e.g. "T")
        // maps to definition.Definition.GenericParameters[k]; an IL type to
        // iltype.TypeReference; a CLR type to module.ImportReference(TypeForCLR).
        // A miss returns null (-> BuildFromNeoRecord skips the bind). The IL/CLR
        // discrimination tries LoadedTypes (IL/CLR) then GetType(fullName), so it
        // does not depend on the NeoTypeRefKind byte (round-2 robustness).
        static ILRuntime.Mono.Cecil.TypeReference ResolveVariableType(
            ILRuntime.Runtime.Enviorment.AppDomain appdomain, NeoAssemblyModel model,
            ILMethod definition, int typeRefIdx)
        {
            if (typeRefIdx < 0 || typeRefIdx >= model.TypeRefs.Length) return null;
            var trInfo = model.TypeRefs[typeRefIdx];
            var fullName = trInfo != null ? trInfo.Name : null;
            if (string.IsNullOrEmpty(fullName)) return null;
            // (a) A method generic parameter (e.g. "T"). The TypeRef table stores
            //     the generic param's NAME; map it back to the Cecil GenericParameter
            //     on the live definition. BuildInitObjPrefix + GetTypeTokenHashCode
            //     resolve the concrete T via FindGenericArgument(name).
            //     V5 (neo-aot-generic-cecilfree): a Cecil-free generic-def SHELL has
            //     no Definition.GenericParameters; synthesize a Cecil GenericParameter
            //     from the .neo-stamped names (a lightweight owner; no Cecil module
            //     needed). The synthetic carries the NAME + IsGenericParameter=true,
            //     which is all BuildInitObjPrefix / GetTypeTokenHashCode /
            //     appdomain.GetType read.
            var shellNames = definition.NeoShellGenericParamNames;
            if (shellNames != null)
            {
                for (int i = 0; i < shellNames.Length; i++)
                    if (shellNames[i] == fullName) return AcquireSyntheticGenericParam(fullName);
            }
            else
            {
                try
                {
                    var gps = definition.Definition.GenericParameters;
                    for (int i = 0; i < gps.Count; i++)
                        if (gps[i].Name == fullName) return gps[i];
                }
                catch { }
            }
            // (b) Any loaded IType (IL via LoadedTypes; CLR via GetType). IL ->
            //     ILType.TypeReference; CLR -> ImportReference(TypeForCLR) when a
            //     Cecil module is present, ELSE a bare Cecil TypeReference whose
            //     FullName resolves via appdomain.GetType (V5: a Cecil-free
            //     AppDomain B has NO Cecil module -- ImportReference is unavailable;
            //     the bare ref carries only Name + namespace + IsGenericParameter=
            //     false, which is all BuildInitObjPrefix / appdomain.GetType read).
            IType it = null;
            if (!appdomain.LoadedTypes.TryGetValue(fullName, out it) || it == null)
            {
                try { it = appdomain.GetType(fullName); } catch { it = null; }
            }
            if (it == null) return null;
            if (it is ILType ilt) return ilt.TypeReference;
            if (it is CLRType clr)
            {
                var module = appdomain.LoadedModules != null && appdomain.LoadedModules.Count > 0
                    ? appdomain.LoadedModules[0] : null;
                if (module != null)
                {
                    try { return module.ImportReference(clr.TypeForCLR); } catch { }
                }
                // V5 Cecil-free fallback: a bare Cecil TypeReference (no module).
                // appdomain.GetType(token) reads _ref.FullName + _ref.Scope(null)
                // -> resolves by name via mapType / GetType(string). The ref is
                // IsGenericParameter=false (a plain TypeReference), so it is never
                // mistaken for a method generic param.
                return BuildBareCecilTypeReference(fullName);
            }
            return null;
        }

        // V5: a bare Cecil TypeReference for a CLR type in a Cecil-free AppDomain.
        // Split the FullName into namespace + name (last '.' is the separator; a
        // name with no '.' is all name). No module/scope -- appdomain.GetType
        // resolves it by FullName. Neo-only.
        static ILRuntime.Mono.Cecil.TypeReference BuildBareCecilTypeReference(string fullName)
        {
            int dot = fullName.LastIndexOf('.');
            if (dot < 0) return new ILRuntime.Mono.Cecil.TypeReference(string.Empty, fullName, null, null);
            return new ILRuntime.Mono.Cecil.TypeReference(fullName.Substring(0, dot), fullName.Substring(dot + 1), null, null);
        }

        // V6 (MethodToken T-identity): rebuild a Cecil TypeReference Cecil-free
        // from a deserialized TypeReferencePatchInfo (the .neo MethodRef table's
        // DeclaringType / Parameters carriers). Mirrors the serialize-side
        // TypeReferencePatchInfo.Create(TypeReference) INVERSE: reconstructs a
        // generic-param / generic-instance / plain ref that appdomain.GetType /
        // appdomain.GetMethod resolve by Name + FullName (no Cecil module needed).
        //   - IsGenericParameter: a synthetic Cecil GenericParameter (Name="T"),
        //     so GetType's IsGenericParameter arm resolves the concrete method-
        //     generic arg via contextMethod.FindGenericArgument.
        //   - IsGenericInstance: a Cecil GenericInstanceType whose ElementType is a
        //     bare TypeReference (FullName == the open def, e.g. System.IComparable`
        //     1) + whose GenericParameters are populated from the .neo keys (so
        //     GetType's generic-instance arm reads tr.GenericParameters[i].Name)
        //     + whose GenericArguments are the rebuilt arg refs.
        //   - IsByReference / IsArray: wrap the rebuilt ElementType.
        //   - plain: a bare TypeReference (FullName == Name). null on a miss.
        // Used to rebuild a T-qualified method-token's Cecil MethodReference at the
        // S2 Cecil-free template rebuild (the MethodToken T-identity follow-up).
        static ILRuntime.Mono.Cecil.TypeReference BuildCecilTypeRefFromPatchInfo(
            ILRuntime.Hybrid.TypeReferencePatchInfo info)
        {
            if (info == null) return null;
            if (info.IsGenericParameter)
            {
                // A method-generic-param name (e.g. "T"). Resolve via the synthetic
                // GenericParameter cache (stable identity for hash-keyed lookups).
                return string.IsNullOrEmpty(info.Name) ? null : AcquireSyntheticGenericParam(info.Name);
            }
            if (info.IsByReference)
            {
                var et = BuildCecilTypeRefFromPatchInfo(info.ElementType);
                if (et == null) return null;
                return new ILRuntime.Mono.Cecil.ByReferenceType(et);
            }
            if (info.IsArray)
            {
                var et = BuildCecilTypeRefFromPatchInfo(info.ElementType);
                if (et == null) return null;
                return new ILRuntime.Mono.Cecil.ArrayType(et);
            }
            if (info.IsGenericInstance)
            {
                var elementType = BuildCecilTypeRefFromPatchInfo(info.ElementType);
                if (elementType == null) return null;
                var git = new ILRuntime.Mono.Cecil.GenericInstanceType(elementType);
                // Populate the open def's GenericParameters from the .neo keys so
                // appdomain.GetType's generic-instance arm (AppDomain.cs:1676,
                // tr.GenericParameters[i].Name) reads the right param names.
                if (info.GenericArguments != null)
                {
                    for (int i = 0; i < info.GenericArguments.Length; i++)
                    {
                        var key = info.GenericArguments[i].Key;
                        if (!string.IsNullOrEmpty(key))
                            elementType.GenericParameters.Add(new ILRuntime.Mono.Cecil.GenericParameter(key, elementType));
                        var argRef = BuildCecilTypeRefFromPatchInfo(info.GenericArguments[i].Value);
                        if (argRef == null) return null;
                        git.GenericArguments.Add(argRef);
                    }
                }
                return git;
            }
            // plain ref -- FullName == Name (GetSafeFullNames on the serialize side).
            return string.IsNullOrEmpty(info.Name) ? null : BuildBareCecilTypeReference(info.Name);
        }

        // V6 (MethodToken T-identity): rebuild a Cecil MethodReference Cecil-free
        // from a deserialized MethodReferencePatchInfo (the .neo MethodRef table
        // entry a MethodToken patch's TokenRefIdx points at). The rebuilt ref
        // drives appdomain.GetMethod's NORMAL resolution (Name + DeclaringType +
        // Parameters), which resolves the concrete-T declaring type via
        // contextMethod.FindGenericArgument + finds the method on it. The MethodRef
        // table omits the return type (HybridPatch's MethodReferencePatchInfo) --
        // appdomain.GetMethod reads _ref.ReturnType but GetMethod(name,...) does NOT
        // match on return type, so a bare System.Int32 placeholder suffices (any
        // non-null ref whose FullName appdomain.GetType resolves). null on a miss.
        static ILRuntime.Mono.Cecil.MethodReference BuildMethodReferenceCecilFree(
            ILRuntime.Hybrid.MethodReferencePatchInfo mref)
        {
            if (mref == null) return null;
            // A generic-instance method is out of scope for the constrained-callvirt
            // surface (the constraint's callvirt method token is never a generic-
            // instance method); the .neo carries IsGenericInstance but a T-identity
            // method patch is always the non-generic-instance shape. Reject if set.
            if (mref.IsGenericInstance) return null;
            if (string.IsNullOrEmpty(mref.Name)) return null;
            var declType = BuildCecilTypeRefFromPatchInfo(mref.DeclaringType);
            if (declType == null) return null;
            // ReturnType placeholder: the MethodRef table omits it, GetMethod does
            // not match on it. A bare System.Int32 ref resolves via appdomain.GetType.
            var retType = BuildBareCecilTypeReference("System.Int32");
            var mr = new ILRuntime.Mono.Cecil.MethodReference(mref.Name, retType, declType);
            if (mref.Parameters != null)
            {
                for (int i = 0; i < mref.Parameters.Length; i++)
                {
                    var pt = BuildCecilTypeRefFromPatchInfo(mref.Parameters[i]);
                    if (pt == null) return null;
                    mr.Parameters.Add(new ILRuntime.Mono.Cecil.ParameterDefinition(pt));
                }
            }
            return mr;
        }

        // V6 (MethodToken T-identity): the MethodRef re-resolution closure (MethodRef
        // idx -> Cecil MethodReference, Cecil-free). Used by BuildFromNeoRecord's
        // RebuildPatchesNoCecil to re-resolve a T-qualified method token (a
        // constrained. T callvirt) into PatchEntry.CecilToken so DoCloneAndPatch's
        // existing GetMethodTokenHash path re-derives the concrete-T method hash.
        // null on a miss (-> BuildFromNeoRecord skips the bind, the additive
        // contract). Neo-only.
        static ILRuntime.Mono.Cecil.MethodReference ResolveMethodRef(int methodRefIdx, NeoAssemblyModel model)
        {
            if (model.MethodRefs == null || methodRefIdx < 0 || methodRefIdx >= model.MethodRefs.Length) return null;
            return BuildMethodReferenceCecilFree(model.MethodRefs[methodRefIdx]);
        }

        // V5: resolve the open generic def's RETURN type from the .neo template's
        // ReturnTypeRefIdx. A generic-param name (e.g. "T") -> an
        // ILGenericParameterType (so MakeGenericMethodShell's SubstituteGenericParam
        // swaps the concrete arg). A concrete type -> LoadedTypes / GetType. null /
        // void on a miss. Neo-only.
        static IType ResolveReturnTypeFromTemplate(ILRuntime.Runtime.Enviorment.AppDomain appdomain,
            NeoAssemblyModel model, NeoTemplateRecord trec, ILMethod definition)
        {
            if (trec.ReturnTypeRefIdx < 0 || trec.ReturnTypeRefIdx >= model.TypeRefs.Length) return null;
            var tr = model.TypeRefs[trec.ReturnTypeRefIdx];
            string name = tr != null ? tr.Name : null;
            if (string.IsNullOrEmpty(name)) return null;
            var shellNames = definition.NeoShellGenericParamNames;
            if (shellNames != null)
            {
                for (int i = 0; i < shellNames.Length; i++)
                    if (shellNames[i] == name) return new CLR.TypeSystem.ILGenericParameterType(name);
            }
            IType it;
            if (appdomain.LoadedTypes.TryGetValue(name, out it) && it != null) return it;
            try { return appdomain.GetType(name); } catch { return null; }
        }

        // Resolve a TypeRef index to the runtime IType. Used for EH catch-type
        // matching (S1) + by the S3 ILType rebuild builder for interface TypeRefs
        // + VTable declaring-type resolution. IL types resolve via LoadedTypes;
        // CLR types route through GetType(fullName) (aqname / assembly scan). The
        // try-both order is safe and does not depend on the NeoTypeRefKind byte
        // (IL-vs-CLR discrimination robustness is a round-2 concern).
        internal static IType ResolveTypeRefToIType(ILRuntime.Runtime.Enviorment.AppDomain appdomain,
                                           NeoAssemblyModel model, int typeRefIdx)
        {
            if (typeRefIdx < 0 || typeRefIdx >= model.TypeRefs.Length) return null;
            var tr = model.TypeRefs[typeRefIdx];
            var fullName = tr != null ? tr.Name : null;
            if (string.IsNullOrEmpty(fullName)) return null;
            IType it;
            if (appdomain.LoadedTypes.TryGetValue(fullName, out it) && it != null) return it;
            try { return appdomain.GetType(fullName); }
            catch { return null; }
        }

        // ===== Step 25 S3: the ILType layout/VTable rebuild resolution helper.
        //
        // Resolve a NeoTypeDefRecord's VTableMethodRefIdxs[] -> the live IMethod[]
        // (the Neo VTable slot array), via the same-AppDomain maps. Each slot's
        // MethodRef carries the slot method's DeclaringType -- which for an
        // INHERITED slot is the base ILType or a CLR type (e.g. System.Object's
        // virtuals), NOT `iltype`. So the slot is resolved on its declaring type
        // (resolved by name via LoadedTypes / GetType), falling back to `iltype`
        // (whose GetMethod walks the full base hierarchy) when the declaring-type
        // name is absent / unresolvable. This is hierarchy-aware (unlike
        // MatchMethod, which is own-methods-only and used by the S1 Attach flow
        // to bind OWN method bodies); VTable slots include inherited base / CLR
        // slots that own-only matching would miss. A miss is reported via
        // `unresolved` (a forward signal for sub-surface 2's Cecil-free load,
        // which needs every slot); the entry is left null. No change to the S1/S2
        // Attach flow (this helper is driven by the DEBUG self-check, not Attach).
        internal static IMethod[] ResolveVTableFromRecord(ILType iltype, NeoAssemblyModel model,
                                                          NeoTypeDefRecord rec, out int unresolved)
        {
            unresolved = 0;
            var idxs = rec.VTableMethodRefIdxs;
            var vt = new IMethod[idxs != null ? idxs.Length : 0];
            if (idxs == null || iltype == null || model == null || model.MethodRefs == null)
                return vt;
            var appdomain = iltype.AppDomain;
            for (int i = 0; i < idxs.Length; i++)
            {
                int mridx = idxs[i];
                if (mridx < 0 || mridx >= model.MethodRefs.Length) { unresolved++; continue; }
                var mr = model.MethodRefs[mridx];
                if (mr == null || mr.IsGenericInstance) { unresolved++; continue; }
                int paramCount = mr.Parameters != null ? mr.Parameters.Length : 0;
                // Resolve the slot's declaring type. Own-probe slots declare on
                // `iltype`; inherited base-IL / CLR-Object slots declare elsewhere.
                IType declType = iltype;
                if (mr.DeclaringType != null && !string.IsNullOrEmpty(mr.DeclaringType.Name)
                    && mr.DeclaringType.Name != iltype.FullName)
                {
                    var dn = mr.DeclaringType.Name;
                    IType resolved = null;
                    if (!appdomain.LoadedTypes.TryGetValue(dn, out resolved) || resolved == null)
                    {
                        try { resolved = appdomain.GetType(dn); } catch { resolved = null; }
                    }
                    if (resolved != null) declType = resolved;
                }
                IMethod m = null;
                try { m = declType.GetMethod(mr.Name, paramCount, false); }
                catch { m = null; }
                vt[i] = m;
                if (m == null) unresolved++;
            }
            return vt;
        }
    }

    /// <summary>
    /// Step 25 S3: the rebuilt ILType layout + Neo VTable + interface map, as
    /// PURE DATA reconstructed from a deserialized NeoTypeDefRecord by
    /// ILType.RebuildFromNeoRecord. Consumed ONLY by the DEBUG+Neo host-side
    /// self-check (NeoStep25LoadExecCheck) to compare against the Cecil-computed
    /// values. The rebuild is NOT installed on a live ILType (D1); this struct
    /// is the comparison payload. Neo-only (lives under #if ENABLE_NEO_MODE).
    /// </summary>
    internal struct NeoTypeRebuild
    {
        // Instance field layout (read from the record).
        public int TotalPrimitiveSize;
        public int TotalReferenceCount;
        public int[] FieldPrimitiveOffsets;
        public int[] FieldReferenceOffsets;
        // naturalAlignment is NOT carried in the record; RE-DERIVED from the
        // resolved own field types (max natural size). Proves the re-derivation
        // matches the Cecil-computed ILType.NaturalAlignment.
        public int NaturalAlignment;
        // Neo VTable (VTableMethodRefIdxs resolved to live IMethods) + the slot-
        // key map re-derived from each slot method's SignatureString.
        public IMethod[] VTable;
        public string[] VTableSlotKeys;
        public int UnresolvedVTableSlots;
        // Interface offset map (each entry's InterfaceType resolved; VTableOffset
        // / MethodSlotKeys / ClassSlotRemap carried verbatim from the record).
        public ResolvedInterfaceEntry[] Interfaces;
        public int UnresolvedInterfaces;

        public struct ResolvedInterfaceEntry
        {
            public IType InterfaceType;     // null = the interface TypeRef did not resolve
            public int VTableOffset;
            public string[] MethodSlotKeys;
            public int[] ClassSlotRemap;
        }
    }

    /// <summary>
    /// The attach report: which methods got an AOT body, which were skipped (and
    /// why). A skip means the method KEEPS its JIT path (the additive contract).
    /// </summary>
    internal sealed class NeoLoadReport
    {
        public List<string> Attached = new List<string>();
        public List<(string reason, string target)> Skipped = new List<(string, string)>();
        public int Total => Attached.Count + Skipped.Count;
    }
}
#endif
