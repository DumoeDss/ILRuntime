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
                    var def = MatchGenericDefinition(iltype, mref.Name, wantParam);
                    if (def == null)
                    {
                        report.Skipped.Add(("generic def not matched", typeFullName + "." + (mref.Name ?? "?")));
                        continue;
                    }
                    // The VariableTypes re-resolution closure (TypeRef idx -> Cecil
                    // TypeReference, same-AppDomain). A generic-parameter name maps to
                    // definition.Definition.GenericParameters[k]; an IL type to
                    // iltype.TypeReference; a CLR type to ImportReference(TypeForCLR).
                    // A miss returns null -> BuildFromNeoRecord skips the bind.
                    Func<int, ILRuntime.Mono.Cecil.TypeReference> resolveVariableType = idx =>
                        ResolveVariableType(appdomain, model, def, idx);
                    var tpl = Runtime.Intepreter.RegisterVM.GenericMethodTemplateOps.BuildFromNeoRecord(
                        def, appdomain, model, trec, resolveVariableType);
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

        // Step 25 S2: re-resolve a VariableTypeRefIdx (-> TypeRef table) to a Cecil
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
            try
            {
                var gps = definition.Definition.GenericParameters;
                for (int i = 0; i < gps.Count; i++)
                    if (gps[i].Name == fullName) return gps[i];
            }
            catch { }
            // (b) Any loaded IType (IL via LoadedTypes; CLR via GetType). IL ->
            //     ILType.TypeReference; CLR -> ImportReference(TypeForCLR).
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
                if (module == null) return null;
                try { return module.ImportReference(clr.TypeForCLR); } catch { return null; }
            }
            return null;
        }

        // Resolve a TypeRef index to the runtime IType for EH catch-type matching.
        // IL catch types resolve via LoadedTypes (the S1 probe's catch types); CLR
        // catch types route through GetType(fullName) (aqname / assembly scan). The
        // try-both order is safe and does not depend on the NeoTypeRefKind byte
        // (IL-vs-CLR discrimination robustness is a round-2 concern).
        static IType ResolveTypeRefToIType(ILRuntime.Runtime.Enviorment.AppDomain appdomain,
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
