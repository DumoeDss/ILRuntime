using System;
using System.Collections.Generic;

using ILRuntime.Mono.Cecil;
using ILRuntime.Mono.Cecil.Cil;

using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.Runtime.Intepreter.OpCodes;

namespace ILRuntime.Runtime.Intepreter.RegisterVM
{
#if ENABLE_NEO_MODE
    // ===== Step 22: generic-method TEMPLATE mechanism (Neo AOT infrastructure) =====
    //
    // A generic method DEFINITION (ILMethod with GenericParameterCount > 0 &&
    // !IsGenericInstance) is compiled once to a TEMPLATE: the register-index
    // OpCodeR[] body captured AFTER CleanupRegister and BEFORE
    // TypeSpecializeNeoOpcodes (the latest T-invariant artifact in the Compile
    // pipeline), plus a PatchEntry[] table of the T-IDENTITY operand sites.
    //
    // At instantiation, CloneAndPatch produces a NeoExecuteBody + frame layout
    // EQUIVALENT to the per-occurrence JIT body for the same generic args, by:
    //   1. cloning the template body,
    //   2. applying the PatchEntry table (re-resolving the T-IDENTITY tokens via
    //      the concrete generic args -- the front-half T-dependence),
    //   3. rebuilding the auto-Initobj local prefix for the concrete T (the only
    //      front-half T-dependence that changes the instruction COUNT -- fires
    //      when a generic-param local's concrete T is a non-primitive value type),
    //   4. re-running the T-dependent back-half (TypeSpecialize + Allocate +
    //      Lower) via JITCompiler.RunNeoBackHalf.
    //
    // The per-occurrence JIT path STAYS as the reference + fallback when no
    // template is cached. Legacy ExecuteR is unaffected (this whole file is
    // gated #if ENABLE_NEO_MODE).

    /// <summary>
    /// Which standalone OpCodeR field a PatchEntry stamps. Restricted to the
    /// standalone fields (Operand @8 / Operand2 @12 / Operand4 @20); MUST NOT be
    /// Register1/2/3 (those alias DstOffset/SrcOffset/OperandOffset and hold
    /// byte offsets post-LowerNeoOffsets -- Kind B territory, re-derived not
    /// patched). Grounded in the per-occurrence JIT dump diff.
    /// </summary>
    internal enum PatchField
    {
        Operand = 0,    // @offset 8  (type-token on Initobj/Box/Unbox/Unbox_Any/Isinst/Castclass/Newarr/Stobj/Ldobj/Constrained; is-ref flag on Move)
        Operand2 = 1,   // @offset 12 (method-token on T-qualified Call/Callvirt/Call_Redirect)
        Operand4 = 2,   // @offset 20
    }

    /// <summary>
    /// How a PatchEntry's concrete value is derived from the concrete generic arg.
    /// </summary>
    internal enum PatchKind
    {
        // The operand is a type-token hash; concrete value = the concrete type's
        // hash (via AppDomain.CacheType + IType.GetHashCode, mirroring
        // ILMethod.GetTypeTokenHashCode for generic-parameter tokens).
        TypeToken = 0,
        // The operand is a method-token hash for a T-qualified call; concrete
        // value = the method resolved on the concrete T.
        MethodToken = 1,
        // The operand is the Move is-ref flag (0 = value byte-copy, 1 = ref-copy).
        // (Re-derived by TypeSpecializeNeoOpcodes in Step 22; recorded for the
        // Step-23 serialize-without-Cecil path.)
        IsRefMoveFlag = 2,
    }

    /// <summary>
    /// A T-IDENTITY operand site in the template body. The byte-offset sites
    /// (Kind B, cumulative) are NOT recorded here -- they are re-derived by re-
    /// running AllocateLocalStackSpaces + LowerNeoOffsets.
    /// </summary>
    internal struct PatchEntry
    {
        public int InstrIdx;         // index into the template OpCodeR[] body
        public PatchField Field;     // which standalone OpCodeR field
        public PatchKind Kind;       // how to derive the concrete value
        public int GenericParamIdx;  // which generic arg (0-based) drives the value (Step-23 contract)
        public object CecilToken;    // the Cecil token to re-resolve via the instance (Step-22 apply path)
    }

    internal sealed class GenericMethodTemplate
    {
        public ILMethod Definition;
        // The T-invariant register-index body (post-CleanupRegister, pre-
        // TypeSpecialize). The CloneAndPatch capture point.
        public OpCodeR[] TemplateBody;
        // T-IDENTITY operand sites (Kind A). Kind B (byte offsets) re-derived.
        public PatchEntry[] Patches;
        // Front-half metadata needed to re-run the back-half (RunNeoBackHalf
        // inputs) -- all T-invariant.
        public short LocVarRegStart;
        public int TotalRegCnt;
        public short NeoCatchExRegFinal;
        public int StackRegisterCount;
        public Dictionary<int, int[]> SwitchTargets;
        public Dictionary<int, RegisterVMSymbol> Symbols;
        // Cecil Instruction -> template body index (for exception-handler
        // resolution in InitCodeBody). Shifted by delta at CloneAndPatch for
        // struct-T (Initobj prefix growth).
        public Dictionary<Instruction, int> Addr;
        // The auto-Initobj local prefix in the template body (CheckNeedInitObj-
        // driven locals; T-invariant -- the open definition has IsValueType==
        // false for generic-param locals so no generic-param-value-T Initobj).
        public int InitObjPrefixLength;
        public int[] InitObjPrefixRegisters;  // Register1 of each prefix op, in order
        // Cecil VariableTypes (captured so CloneAndPatch does not depend on
        // def.Body, which Release builds null out).
        public TypeReference[] VariableTypes;
        public int VarCnt;
        // The `constrained. T` Cecil prefix TypeReferences, in CIL order
        // (BLOCKER-1). The Constrained op's recorded symbol points at the
        // trailing callvirt (or a scrambled unrelated op after CleanupRegister),
        // NOT at the `constrained.` prefix, so the T TypeReference cannot be
        // recovered from the symbol. Captured at template-capture time by
        // scanning the CIL body; consumed in body order by ExtractPatches (CIL
        // order == body order -- each constrained.+callvirt pair becomes exactly
        // one Constrained op, never inlined/reordered). Null if the body has no
        // `constrained.` instruction.
        public TypeReference[] ConstrainedTypeTokens;
        // The trailing callvirt's Cecil MethodReference per `constrained.` pair,
        // paired 1:1 with ConstrainedTypeTokens (MAJOR-2). A T-qualified callvirt
        // (e.g. IComparable<T>::CompareTo) has a concrete-T-dependent method
        // token (Operand2); CloneAndPatch re-emits it so the runtime Constrained
        // arm dispatches on the right method. Null entry if not T-qualified.
        public MethodReference[] ConstrainedMethodTokens;

        // Lazily-built shared "ref body" for the all-reference-type-args AND
        // no-T-identity-token case (ref-share: object == string byte-for-byte
        // when the body has no Box T / Isinst T). Null until first used.
        public CompiledFrame RefBody;
        public Dictionary<Instruction, int> RefBodyAddr;
        // neo-typeof-generic-param: the capture instance's concrete generic args
        // (the T-values the template body was specialized for). The Neo JIT emits
        // TYPED opcodes at Translate time (Stfld_I4 vs Stfld_R8, Ldfld_*, unbox-
        // any, conv, etc.) based on the concrete T's type category, and these
        // typed arms are baked into the captured template body. CloneAndPatch
        // patches T-identity TOKENS (ldtoken/Box/Isinst/...) but NOT the typed
        // opcode CODE, so a concrete T whose typed-field-opcode category differs
        // from the capture T would execute a wrong-typed arm (e.g. GetRows<int,
        // int> captured -> Stfld_I4 for V; GetRows<int,double> reuses it -> V is
        // written as 4 bytes instead of 8 -> corrupt double). The fall-back in
        // TryInstantiate compares each concrete arg's Stfld category to the
        // capture arg's; a mismatch -> per-occurrence JIT (the correct reference).
        public IType[] CaptureTypeArgs;

        public bool HasIdentityToken()
        {
            // A token-bearing body: any TypeToken/MethodToken patch whose value
            // differs across reference types. (IsRefMoveFlag does NOT block ref-
            // share -- it is 1 for every ref-T uniformly.)
            if (Patches == null) return false;
            for (int i = 0; i < Patches.Length; i++)
            {
                if (Patches[i].Kind == PatchKind.TypeToken || Patches[i].Kind == PatchKind.MethodToken)
                    return true;
            }
            return false;
        }
    }

    // ===== static logic =====
    internal static class GenericMethodTemplateOps
    {
        // ---- token classification (mirrors ILMethod.CheckHasGenericParamter) ----
        internal static bool HasGenericParameter(object token)
        {
            if (token is MethodReference mr)
            {
                // A method token is T-qualified if its declaring type or its own
                // generic arguments contain a generic parameter (e.g.
                // IComparable<T>::CompareTo, or a generic-method call G<T>::M()).
                if (mr.DeclaringType != null && HasGenericParameter(mr.DeclaringType)) return true;
                if (mr is GenericInstanceMethod gim)
                {
                    foreach (var a in gim.GenericArguments)
                        if (HasGenericParameter(a)) return true;
                }
                return false;
            }
            if (token is TypeReference tr)
            {
                if (tr.IsArray) return HasGenericParameter(((ArrayType)tr).ElementType);
                if (tr.IsGenericParameter) return true;
                if (tr.IsGenericInstance)
                {
                    var gi = (GenericInstanceType)tr;
                    foreach (var a in gi.GenericArguments)
                        if (HasGenericParameter(a)) return true;
                    return false;
                }
                return false;
            }
            return false;
        }

        internal static int ResolveGenericParamIdx(object token, ILMethod def)
        {
            // Method-generic-param index (0-based). -1 if the token is not a
            // method generic param (e.g. a declaring-type generic param) -- Step 22
            // apply uses the CecilToken regardless, so -1 only narrows the Step-23
            // contract.
            if (token is TypeReference tr && tr.IsGenericParameter)
            {
                var gps = def.Definition.GenericParameters;
                for (int i = 0; i < gps.Count; i++)
                    if (gps[i].Name == tr.Name) return i;
            }
            else if (token is MethodReference mr)
            {
                // For a T-qualified method token, find the FIRST method-generic-
                // param that appears in the declaring type or the method's own
                // generic args (Step-23 contract; Step-22 apply uses CecilToken).
                var gps = def.Definition.GenericParameters;
                // method generic args (generic instance method)
                if (mr is GenericInstanceMethod gim)
                {
                    foreach (var a in gim.GenericArguments)
                        if (a is TypeReference at && at.IsGenericParameter)
                        {
                            for (int i = 0; i < gps.Count; i++)
                                if (gps[i].Name == at.Name) return i;
                        }
                }
                // declaring-type generic param (e.g. IComparable<T>) -- a method-
                // generic-param index does not apply, return -1 (Step-22 apply
                // uses CecilToken regardless).
                return -1;
            }
            return -1;
        }

        // Re-resolve a Cecil MethodReference's token hash for `instance`,
        // mirroring JITCompiler.InitializeFunctionParam (appdomain.GetMethod ->
        // m.GetHashCode() when the token is generic-param-bearing / "invalid",
        // else token.GetHashCode()). Used by DoCloneAndPatch to re-emit T-qualified
        // method tokens (Operand2 on the Constrained pair's trailing callvirt) for
        // the concrete T (MAJOR-2).
        internal static int GetMethodTokenHash(ILRuntime.Runtime.Enviorment.AppDomain appdomain, ILType declaringType, ILMethod instance, object cecilMethodRef)
        {
            bool invalidToken;
            var m = appdomain.GetMethod(cecilMethodRef, declaringType, instance, out invalidToken);
            if (m != null)
                return invalidToken ? m.GetHashCode() : cecilMethodRef.GetHashCode();
            return cecilMethodRef.GetHashCode();
        }

        // ---- PatchEntry extractor (task 2.2) ----
        // Scans the register-index template body for T-IDENTITY operand sites and
        // records a PatchEntry per site. Kind A only (Operand-level); Kind B (byte
        // offsets) are re-derived. Auto-inserted Initobj (Operand2==1) are handled
        // by the prefix rebuild, NOT recorded here.
        //
        // Constrained (BLOCKER-1): the Constrained op's recorded symbol does NOT
        // point at the `constrained.` Cecil prefix -- the JIT emits Constrained+
        // callvirt as a pair (Translate removes the Constrained op from its slot,
        // appends Push ops, re-adds it, and re-keys InstructionMapping to the
        // trailing callvirt's Cecil instruction), and CleanupRegister can scramble
        // it further to an unrelated branch/ret. So `sym.Instruction.Operand` is a
        // MethodReference (or worse), HasGenericParameter returns false, and no
        // patch is recorded -> the cloned body keeps the capture-instance type
        // hash -> wrong-type runtime dispatch for every T != capture-T. The fix:
        // the `constrained. T` TypeReference is pre-captured at template-capture
        // time (CIL body scan, see CaptureTemplate), and consumed HERE in body
        // order (CIL order == body order: each constrained.+callvirt pair becomes
        // exactly one Constrained op and is never inlined/reordered -- the
        // hasConstrained path disables inlining for the trailing callvirt).
        internal static PatchEntry[] ExtractPatches(GenericMethodTemplate template)
        {
            if (template.Symbols == null) return Array.Empty<PatchEntry>();
            var body = template.TemplateBody;
            var def = template.Definition;
            var patches = new List<PatchEntry>();
            var constrainedTokens = template.ConstrainedTypeTokens;
            var constrainedMethods = template.ConstrainedMethodTokens;
            int constrainedIdx = 0;
            for (int i = 0; i < body.Length; i++)
            {
                var op = body[i];
                PatchField field;
                switch (op.Code)
                {
                    case OpCodeREnum.Constrained:
                        // Constrained T type-token (BLOCKER-1) + the trailing
                        // callvirt's T-qualified method-token (MAJOR-2). Both are
                        // sourced from the pre-captured CIL pair list, NOT the
                        // (unreliable) symbol. The Constrained op carries the type
                        // hash in Operand AND (vestigially, for V1 body-equality)
                        // the callvirt method hash in Operand2; the trailing
                        // callvirt op (i+1) carries the runtime-load-bearing method
                        // hash in Operand2 (the runtime Constrained arm reads
                        // cv->Operand2 at ILIntepreter.Neo.cs). Re-emit both via
                        // instance re-resolution at CloneAndPatch.
                        if (constrainedTokens != null && constrainedIdx < constrainedTokens.Length)
                        {
                            var ct = constrainedTokens[constrainedIdx];
                            MethodReference cm = null;
                            if (constrainedMethods != null && constrainedIdx < constrainedMethods.Length)
                                cm = constrainedMethods[constrainedIdx];
                            constrainedIdx++;
                            if (ct != null && HasGenericParameter(ct))
                            {
                                patches.Add(new PatchEntry
                                {
                                    InstrIdx = i,
                                    Field = PatchField.Operand,
                                    Kind = PatchKind.TypeToken,
                                    GenericParamIdx = ResolveGenericParamIdx(ct, def),
                                    CecilToken = ct,
                                });
                            }
                            // MAJOR-2: the trailing callvirt's method token, if
                            // T-qualified (e.g. IComparable<T>::CompareTo). Patch
                            // BOTH the Constrained op's Operand2 (vestigial; V1
                            // body-equality) and the trailing callvirt's Operand2
                            // (runtime-load-bearing). For a non-T-qualified
                            // callvirt (e.g. Object::GetHashCode) the method token
                            // is T-invariant -> no patch.
                            if (cm != null && HasGenericParameter(cm))
                            {
                                patches.Add(new PatchEntry
                                {
                                    InstrIdx = i,
                                    Field = PatchField.Operand2,
                                    Kind = PatchKind.MethodToken,
                                    GenericParamIdx = ResolveGenericParamIdx(cm, def),
                                    CecilToken = cm,
                                });
                                if (i + 1 < body.Length)
                                {
                                    patches.Add(new PatchEntry
                                    {
                                        InstrIdx = i + 1,
                                        Field = PatchField.Operand2,
                                        Kind = PatchKind.MethodToken,
                                        GenericParamIdx = ResolveGenericParamIdx(cm, def),
                                        CecilToken = cm,
                                    });
                                }
                            }
                        }
                        continue;
                    case OpCodeREnum.Box:
                    case OpCodeREnum.Unbox:
                    case OpCodeREnum.Unbox_Any:
                    case OpCodeREnum.Isinst:
                    case OpCodeREnum.Castclass:
                    case OpCodeREnum.Newarr:
                    case OpCodeREnum.Stobj:
                    case OpCodeREnum.Ldobj:
                        field = PatchField.Operand;
                        break;
                    case OpCodeREnum.Initobj:
                        // IL-source Initobj (Operand2==0) carries a T-token; the
                        // auto-inserted prefix (Operand2==1) is rebuilt separately.
                        if (op.Operand2 == 1) continue;
                        field = PatchField.Operand;
                        break;
                    // neo-typeof-generic-param: a type-path `ldtoken <T>` (the
                    // producer half of `typeof(T)`) stores the resolved type-token
                    // hash in OperandLong (@12-19) -- specifically its LOW dword,
                    // Operand2 (@12), with the high dword (Operand3 @16) zero
                    // (JIT emits op.OperandLong = method.GetTypeTokenHashCode(token),
                    // int->long zero-extended). Operand/Operand2/Operand4 are the
                    // only stampable PatchFields; Operand2 is the one that aliases
                    // the type-token low dword, so DoCloneAndPatch's
                    // `body[idx].Operand2 = newHash` re-resolves typeof(T) for the
                    // concrete instance (GetTypeTokenHashCode -> FindGenericArgument
                    // -> the concrete T). Without this patch the cloned template
                    // keeps the CAPTURE-T hash (e.g. int for both A and B captured
                    // from GetRows<int,int>), so a later GetRows<int,double> call
                    // resolves typeof(B) to int instead of double (the
                    // TestGenericMethod2 failure). LowerNeoOffsets' Ldtoken case
                    // touches only Operand4 + DstOffset, and TypeSpecialize has no
                    // Ldtoken arm, so the patched Operand2 survives the back-half.
                    // The field-path ldtoken (Operand==0) carries a FieldReference
                    // Cecil token -> HasGenericParameter returns false below -> no
                    // patch recorded, so the static-field path is unaffected.
                    case OpCodeREnum.Ldtoken:
                        field = PatchField.Operand2;
                        break;
                    // T-qualified Call/Callvirt method-token. The method hash lives
                    // in Operand2 (InitializeFunctionParam: m.GetHashCode() when the
                    // token is generic-param-bearing / "invalid", else token.GetHashCode
                    // ()). A generic-method call whose generic arg is a method-generic-
                    // param T (e.g. LoadAsset<T> from inside CLRBindingTest06Sub<T>)
                    // resolves to a T-dependent hash, so the cloned template body MUST
                    // re-emit it per instance. Without this patch, HasIdentityToken()
                    // returns false for an otherwise token-free body -> TryInstantiate
                    // wrongfully ref-shares the capture-T body across all all-ref
                    // instantiations -> wrong-type CLR dispatch (CLRBindingTest07/08:
                    // LoadAsset<TestCLRBinding> served for <String>/<Int32> callers).
                    // Skip the trailing callvirt of a constrained pair: the Constrained
                    // case above already recorded its Operand2 patch from the pre-
                    // captured (reliable) Cecil pair, and this op's symbol may be
                    // scrambled (BLOCKER-1) -> would overwrite the correct token.
                    case OpCodeREnum.Call:
                    case OpCodeREnum.Callvirt:
                    case OpCodeREnum.Callvirt_IL:
                    case OpCodeREnum.Callvirt_CLR:
                    case OpCodeREnum.Call_Redirect:
                        if (i > 0 && body[i - 1].Code == OpCodeREnum.Constrained)
                            continue;
                        if (!template.Symbols.TryGetValue(i, out var symCall))
                            continue;
                        var callToken = symCall.Instruction.Operand;
                        if (callToken == null) continue;
                        if (!HasGenericParameter(callToken)) continue;  // T-invariant -> no patch
                        patches.Add(new PatchEntry
                        {
                            InstrIdx = i,
                            Field = PatchField.Operand2,
                            Kind = PatchKind.MethodToken,
                            GenericParamIdx = ResolveGenericParamIdx(callToken, def),
                            CecilToken = callToken,
                        });
                        continue;
                    // Ldelem_Any / Stelem_Any carry no element-type token in this
                    // JIT (the array kind is on the Newarr); Initobj prefix is
                    // rebuilt.
                    default:
                        continue;
                }
                if (!template.Symbols.TryGetValue(i, out var sym)) continue;
                var token = sym.Instruction.Operand;
                if (token == null) continue;
                if (!HasGenericParameter(token)) continue;  // T-invariant -> no patch
                patches.Add(new PatchEntry
                {
                    InstrIdx = i,
                    Field = field,
                    Kind = PatchKind.TypeToken,
                    GenericParamIdx = ResolveGenericParamIdx(token, def),
                    CecilToken = token,
                });
            }
            return patches.Count == 0 ? Array.Empty<PatchEntry>() : patches.ToArray();
        }

        // ---- template build (task 3.1): capture from the first concrete
        //      instantiation's front-half (NOT a Compile of the open definition) ----
        // Compiling the open definition was found to corrupt shared AppDomain
        // state (type/method/field caches resolve differently for the open generic
        // parameter and leave stale entries). Instead, the template is captured
        // during a NORMAL per-occurrence Compile of a concrete instance whose
        // typeArgs keep the front-half T-INVARIANT (every generic arg is a refer-
        // ence type or a primitive -- so no generic-param-value-T local fires the
        // Initobj insertion, the only front-half T-dependence that changes the
        // instruction stream). For such an instance the front-half output is
        // byte-identical to the open definition's, so it is the T-invariant
        // template base. Subsequent instantiations (any T, including struct-T)
        // CloneAndPatch from it.
        internal static GenericMethodTemplate StoreFromCapture(ILMethod definition, JITCompiler.TemplateCapture cap, IType[] captureTypeArgs)
        {
            if (cap == null || cap.TemplateBody == null) return null;
            var template = new GenericMethodTemplate();
            template.Definition = definition;
            template.CaptureTypeArgs = captureTypeArgs;
            template.TemplateBody = cap.TemplateBody;
            template.LocVarRegStart = cap.LocVarRegStart;
            template.TotalRegCnt = cap.TotalRegCnt;
            template.NeoCatchExRegFinal = cap.NeoCatchExRegFinal;
            template.StackRegisterCount = cap.StackRegisterCount;
            template.SwitchTargets = cap.SwitchTargets;
            template.Symbols = cap.Symbols;
            template.Addr = cap.Addr;
            template.VariableTypes = cap.VariableTypes;
            template.VarCnt = cap.VarCnt;
            template.ConstrainedTypeTokens = cap.ConstrainedTypeTokens;
            template.ConstrainedMethodTokens = cap.ConstrainedMethodTokens;
            template.InitObjPrefixRegisters = cap.InitObjPrefixRegisters ?? Array.Empty<int>();
            template.InitObjPrefixLength = template.InitObjPrefixRegisters.Length;
            template.Patches = ExtractPatches(template);
            return template;
        }

        // ---- Step 25 S2: reconstruct a GenericMethodTemplate from a deserialized
        //      .neo NeoTemplateRecord (no live JIT capture). Mirrors StoreFromCapture
        //      but reads the Cecil-free fields from the record + re-resolves the
        //      Cecil-typed VariableTypes from VariableTypeRefIdxs via a loader-
        //      provided closure (same-AppDomain Cecil module available). Returns
        //      null (-> loader skips the bind -> generic method keeps JIT) when the
        //      record hits a deferred S3 case:
        //        - a T-identity token patch (TypeToken/MethodToken with a non-none
        //          CecilTokenKind) -- needs Cecil TypeReference/MethodReference
        //          recovery or a Cecil-free patch applier keyed on GenericParamIdx.
        //        - a VariableTypes re-resolution miss (a local type the live Cecil
        //          module cannot resolve).
        //      The S2 slice covers the no-T-identity-token generic method (patch
        //      table IsRefMoveFlag-only or empty), so CecilToken is set null on
        //      every rebuilt patch (DoCloneAndPatch skips a null CecilToken at
        //      :485; the back-half re-derives the is-ref flag). Addr/Symbols/
        //      Constrained tokens are null (the S2 probe's generic methods carry no
        //      EH and no constrained. prefix; those are S3).
        internal static GenericMethodTemplate BuildFromNeoRecord(
            ILMethod definition,
            ILRuntime.Runtime.Enviorment.AppDomain appdomain,
            ILRuntime.Runtime.NeoAOT.NeoAssemblyModel model,
            ILRuntime.Runtime.NeoAOT.NeoTemplateRecord rec,
            Func<int, TypeReference> resolveVariableType,
            Func<int, Mono.Cecil.MethodReference> resolveMethodRef)
        {
            if (rec.TemplateBody == null) return null;
            var tpl = new GenericMethodTemplate();
            tpl.Definition = definition;
            tpl.TemplateBody = rec.TemplateBody;
            tpl.LocVarRegStart = rec.LocVarRegStart;
            tpl.TotalRegCnt = rec.TotalRegCnt;
            tpl.NeoCatchExRegFinal = rec.NeoCatchExRegFinal;
            tpl.StackRegisterCount = rec.StackRegisterCount;
            tpl.VarCnt = rec.VarCnt;
            tpl.InitObjPrefixLength = rec.InitObjPrefixLength;
            tpl.InitObjPrefixRegisters = rec.InitObjPrefixRegisters ?? Array.Empty<int>();
            tpl.SwitchTargets = RebuildSwitchTargetsFromNeo(rec.SwitchTargets);

            // Re-resolve VariableTypes from VariableTypeRefIdxs. BuildInitObjPrefix
            // reads template.VariableTypes[v] unconditionally for v in [0, varCnt),
            // so the array MUST be non-null of length >= varCnt. A miss -> skip.
            int varCnt = rec.VarCnt;
            var vtIdxs = rec.VariableTypeRefIdxs;
            if (varCnt > 0)
            {
                if (vtIdxs == null || vtIdxs.Length < varCnt) return null;  // OQ1: incomplete
                var vts = new TypeReference[varCnt];
                for (int v = 0; v < varCnt; v++)
                {
                    var resolved = resolveVariableType(vtIdxs[v]);
                    if (resolved == null) return null;  // OQ1 miss -> skip (additive)
                    vts[v] = resolved;
                }
                tpl.VariableTypes = vts;
            }
            else
            {
                tpl.VariableTypes = Array.Empty<TypeReference>();
            }

            // Rebuild Patches. IsRefMoveFlag patches (CecilTokenKind 2=none) keep
            // CecilToken = null (the back-half re-derives the is-ref flag). A T-
            // identity TypeToken patch (CecilTokenKind 0=TypeReference -- Box T /
            // Ldobj T / Initobj T / Stobj T / Unbox.Any T where T is a method generic
            // param) is RE-RESOLVED Cecil-free: the patch's TokenRefIdx points at
            // the generic-param's TypeRef (whose Name is e.g. "T"), the
            // resolveVariableType closure returns a synthetic Cecil GenericParameter
            // for that name, and CloneAndPatch's existing TypeToken path
            // (instance.GetTypeTokenHashCode(CecilToken)) re-derives the concrete T
            // hash via FindGenericArgument(token.Name) -- no Cecil module needed.
            // A T-identity MethodToken patch (CecilTokenKind 1=MethodReference -- a
            // `constrained.` T-qualified callvirt) is ALSO re-resolved Cecil-free
            // (V6): the patch's TokenRefIdx points at the MethodRef table entry
            // (DeclaringType + Name + Parameters), the resolveMethodRef closure
            // rebuilds a Cecil MethodReference whose declaring type is the synthetic
            // GenericParameter / a generic instance over it, and CloneAndPatch's
            // existing GetMethodTokenHash path re-derives the concrete-T method hash
            // via appdomain.GetMethod (which resolves the declaring type via
            // contextMethod.FindGenericArgument). A resolution miss falls through to
            // reject below.
            tpl.Patches = RebuildPatchesNoCecil(rec.Patches, resolveVariableType, resolveMethodRef,
                out bool hasUnresolvableToken);
            if (hasUnresolvableToken) return null;  // OQ3: a T-identity token miss -> S3

            // Not read at CloneAndPatch / ExecuteNeo for the S2 slice (no EH, no
            // constrained. prefix in the probe's generic methods). S3 owns these.
            tpl.Addr = null;
            tpl.Symbols = null;
            tpl.ConstrainedTypeTokens = null;
            tpl.ConstrainedMethodTokens = null;
            return tpl;
        }

        static Dictionary<int, int[]> RebuildSwitchTargetsFromNeo(
            KeyValuePair<int, int[]>[] pairs)
        {
            if (pairs == null || pairs.Length == 0) return null;
            var dict = new Dictionary<int, int[]>(pairs.Length);
            for (int i = 0; i < pairs.Length; i++)
                dict[pairs[i].Key] = pairs[i].Value;
            return dict;
        }

        static PatchEntry[] RebuildPatchesNoCecil(
            ILRuntime.Runtime.NeoAOT.NeoPatchEntryRecord[] recs,
            Func<int, TypeReference> resolveVariableType,
            Func<int, Mono.Cecil.MethodReference> resolveMethodRef,
            out bool hasUnresolvableToken)
        {
            hasUnresolvableToken = false;
            if (recs == null) return Array.Empty<PatchEntry>();
            var res = new PatchEntry[recs.Length];
            for (int i = 0; i < recs.Length; i++)
            {
                var r = recs[i];
                object cecilToken = null;
                if (r.Kind == (int)PatchKind.TypeToken && r.CecilTokenKind == 0)
                {
                    // T-identity TypeToken: re-resolve the generic-param's TypeRef
                    // (TokenRefIdx -> the closure -> a synthetic Cecil
                    // GenericParameter whose IsGenericParameter=true + Name="T").
                    // CloneAndPatch's GetTypeTokenHashCode re-derives the concrete T
                    // hash via FindGenericArgument(token.Name). A resolution miss
                    // (closure returned null) falls through to reject below.
                    if (r.TokenRefIdx >= 0)
                        cecilToken = resolveVariableType(r.TokenRefIdx);
                    if (cecilToken == null)
                    {
                        // Could not re-resolve Cecil-free -> still reject this
                        // template (the additive contract: keep JIT).
                        hasUnresolvableToken = true;
                    }
                }
                else if (r.Kind == (int)PatchKind.MethodToken && r.CecilTokenKind != 2)
                {
                    // T-identity MethodToken (a `constrained.` T-qualified callvirt):
                    // re-resolve the method-token's MethodRef (TokenRefIdx -> the
                    // resolveMethodRef closure -> a Cecil MethodReference whose
                    // declaring type is the synthetic GenericParameter / a generic
                    // instance over it + Name + Parameters). CloneAndPatch's
                    // GetMethodTokenHash re-derives the concrete-T method hash via
                    // appdomain.GetMethod (resolves the declaring type via
                    // contextMethod.FindGenericArgument). A resolution miss (closure
                    // returned null) falls through to reject below.
                    if (r.TokenRefIdx >= 0)
                        cecilToken = resolveMethodRef(r.TokenRefIdx);
                    if (cecilToken == null)
                    {
                        hasUnresolvableToken = true;
                    }
                }
                res[i] = new PatchEntry
                {
                    InstrIdx = r.InstrIdx,
                    Field = (PatchField)r.Field,
                    Kind = (PatchKind)r.Kind,
                    GenericParamIdx = r.GenericParamIdx,
                    CecilToken = cecilToken,
                };
            }
            return res;
        }

        // neo-typeof-generic-param: a stable "typed field opcode" category for a
        // concrete generic arg, used by TryInstantiate's category fall-back. Two
        // types whose category differs would select DIFFERENT typed arms
        // (Stfld_I4 vs Stfld_R8) at JIT Translate time, so the cloned template
        // body (pre-specialized for the capture T) cannot serve the concrete T.
        // Returns the OpCodeREnum ordinal GetNeoStfldCodeForType selects (the
        // authoritative source the JIT itself uses), or -1 for a type the helper
        // cannot classify (two -1's compare equal -> conservative no-fall-back).
        static int FieldOpcodeCategory(IType t, ILRuntime.Runtime.Enviorment.AppDomain appdomain)
        {
            try
            {
                return (int)JITCompiler.GetNeoStfldCodeForType(t, appdomain);
            }
            catch
            {
                return -1;
            }
        }

        // Does `instance`'s concrete typeArgs keep the front-half T-invariant?
        // (every arg a reference type or a primitive -> no generic-param-value-T
        // local -> no Initobj insertion -> the front-half stream is T-invariant).
        // Used by InitCodeBody to decide whether THIS instantiation is eligible to
        // capture the template (only ref/primitive instantiations are).
        internal static bool IsCaptureEligible(IType[] typeArgs)
        {
            if (typeArgs == null) return false;
            for (int i = 0; i < typeArgs.Length; i++)
            {
                var t = typeArgs[i];
                if (t == null) return false;
                if (t.IsValueType && !t.IsPrimitive) return false;  // non-primitive value type -> defer
            }
            return true;
        }

        // ---- Initobj prefix rebuild (struct-T: the front-half T-dep the design
        //      mistakenly attributed to TypeSpecialize) ----
        // Re-derives the auto-Initobj local prefix for the concrete T: the union of
        // (a) the template's prefix locals (CheckNeedInitObj-driven, T-invariant)
        // and (b) generic-param locals whose concrete T is a non-primitive value
        // type (T-dependent; absent from the open-def template). In local-register
        // order, one Initobj per qualifying local -- mirroring the front-half loop.
        static List<OpCodeR> BuildInitObjPrefix(GenericMethodTemplate template, ILMethod instance)
        {
            var prefix = new List<OpCodeR>();
            short locVarRegStart = template.LocVarRegStart;
            int varCnt = template.VarCnt;
            var prefixRegs = template.InitObjPrefixRegisters;
            int prefixIdx = 0;
            for (int v = 0; v < varCnt; v++)
            {
                short r = (short)(locVarRegStart + v);
                bool inTemplate = prefixIdx < prefixRegs.Length && prefixRegs[prefixIdx] == r;
                if (inTemplate) prefixIdx++;
                var vt = template.VariableTypes[v];
                bool isGenericParamValueT = false;
                if (vt.IsGenericParameter)
                {
                    var gt = instance.FindGenericArgument(vt.Name);
                    isGenericParamValueT = gt != null && gt.IsValueType && !gt.IsPrimitive;
                }
                if (inTemplate || isGenericParamValueT)
                {
                    var op = new OpCodeR();
                    op.Code = OpCodeREnum.Initobj;
                    op.Register1 = r;
                    op.Operand = instance.GetTypeTokenHashCode(vt);
                    op.Operand2 = 1;
                    prefix.Add(op);
                }
            }
            return prefix;
        }

        // ---- CloneAndPatch core (tasks 4.1, 4.2) ----
        // Produces a CompiledFrame + addr for `instance`, equivalent to the per-
        // occurrence JIT body. Fills `addr` (for exception-handler resolution) and
        // `frame`. Returns true on success.
        internal static bool DoCloneAndPatch(GenericMethodTemplate template, ILMethod instance,
            ILRuntime.Runtime.Enviorment.AppDomain appdomain, ILType declaringType,
            Dictionary<Mono.Cecil.Cil.Instruction, int> addr, ref CompiledFrame frame)
        {
            // 1. Clone the template body.
            var body = (OpCodeR[])template.TemplateBody.Clone();

            // 2. Apply PatchEntry[] (Kind A T-identity tokens) by re-resolving each
            //    Cecil token via the INSTANCE method. TypeToken re-resolves via
            //    ILMethod.GetTypeTokenHashCode; MethodToken (T-qualified callvirt,
            //    MAJOR-2) re-resolves via GetMethodTokenHash (mirrors the front-
            //    half InitializeFunctionParam: appdomain.GetMethod -> m.GetHashCode
            //    or token.GetHashCode).
            var patches = template.Patches;
            if (patches != null)
            {
                for (int p = 0; p < patches.Length; p++)
                {
                    var pe = patches[p];
                    if (pe.CecilToken == null) continue;
                    int idx = pe.InstrIdx;
                    if (pe.Kind == PatchKind.MethodToken)
                    {
                        // Reliability cross-check: the call body op's symbol can be
                        // STALE after inlining (the inliner removes an inlined call
                        // but leaves its Cecil instruction linked at a body index
                        // that now holds a DIFFERENT call). If pe.CecilToken (from
                        // the symbol) re-resolves to a method whose Name differs
                        // from the body op's CURRENT method (body[idx].Operand2 is
                        // the reliable capture-T hash), the symbol was scrambled ->
                        // SKIP the patch (keep the capture-T hash; identical to the
                        // token-free ref-share semantics). Without this guard the
                        // patch corrupts the wrong call's Operand2 (e.g. an inlined-
                        // away Output<T> token stamped onto an ACallback::Invoke
                        // callvirt -> wrong pCnt -> LowerNeoOffsets OOB).
                        var currentMethod = appdomain.GetMethod(body[idx].Operand2);
                        var resolvedMethod = appdomain.GetMethod(pe.CecilToken, declaringType, instance, out _);
                        if (currentMethod != null && resolvedMethod != null
                            && currentMethod.Name != resolvedMethod.Name)
                            continue;
                        int mh = GetMethodTokenHash(appdomain, declaringType, instance, pe.CecilToken);
                        body[idx].Operand2 = mh;
                    }
                    else
                    {
                        int newHash = instance.GetTypeTokenHashCode(pe.CecilToken);
                        switch (pe.Field)
                        {
                            case PatchField.Operand: body[idx].Operand = newHash; break;
                            case PatchField.Operand2: body[idx].Operand2 = newHash; break;
                            case PatchField.Operand4: body[idx].Operand4 = newHash; break;
                        }
                    }
                }
            }

            // 3. Rebuild the auto-Initobj prefix for concrete T (struct-T case).
            var newPrefix = BuildInitObjPrefix(template, instance);
            int oldLen = template.InitObjPrefixLength;
            int delta = newPrefix.Count - oldLen;
            OpCodeR[] finalBody = new OpCodeR[body.Length - oldLen + newPrefix.Count];
            for (int i = 0; i < newPrefix.Count; i++) finalBody[i] = newPrefix[i];
            for (int i = oldLen; i < body.Length; i++) finalBody[newPrefix.Count + (i - oldLen)] = body[i];

            // 4. Shift branch + EH-leave targets by delta (the Initobj prefix
            //    grows/shrinks the body; branch Operands, Leave/Leave_S Operands,
            //    and SwitchTargets values are all body indices). Leave/Leave_S are
            //    NOT in Optimizer.IsBranching (they are EH control-flow, resolved
            //    separately at JITCompiler.cs:555 via addr[]), but their Operand is
            //    a resolved body index all the same, so it shifts identically.
            if (delta != 0)
            {
                for (int i = newPrefix.Count; i < finalBody.Length; i++)
                {
                    var op = finalBody[i];
                    if (Optimizer.IsBranching(op.Code)) { op.Operand += delta; finalBody[i] = op; }
                    else if (op.Code == OpCodeREnum.Leave || op.Code == OpCodeREnum.Leave_S) { op.Operand += delta; finalBody[i] = op; }
                    else if (Optimizer.IsIntermediateBranching(op.Code)) { op.Operand4 += delta; finalBody[i] = op; }
                    // Switch op's Operand is a jumptable KEY (Cecil array hash, not a
                    // body index) -- unchanged; its target array is shifted below.
                }
            }

            // 5. Build SwitchTargets (shift values by delta; keys are stable hashes)
            //    + addr (shift values by delta) for exception-handler resolution.
            Dictionary<int, int[]> switchTargets = template.SwitchTargets;
            Dictionary<Mono.Cecil.Cil.Instruction, int> addrSrc = template.Addr;
            if (delta != 0)
            {
                if (template.SwitchTargets != null)
                {
                    switchTargets = new Dictionary<int, int[]>(template.SwitchTargets.Count);
                    foreach (var kv in template.SwitchTargets)
                    {
                        var arr = kv.Value;
                        var shifted = new int[arr.Length];
                        for (int j = 0; j < arr.Length; j++) shifted[j] = arr[j] + delta;
                        switchTargets[kv.Key] = shifted;
                    }
                }
                if (template.Addr != null)
                {
                    addrSrc = new Dictionary<Mono.Cecil.Cil.Instruction, int>(template.Addr.Count);
                    foreach (var kv in template.Addr) addrSrc[kv.Key] = kv.Value + delta;
                }
            }
            if (addrSrc != null)
            {
                addr.Clear();
                foreach (var kv in addrSrc) addr[kv.Key] = kv.Value;
            }

            // 6. Re-run the T-dependent back-half on the cloned+specialized body.
            frame = new CompiledFrame();
            frame.StackRegisterCount = template.StackRegisterCount;
            frame.SwitchTargets = switchTargets;
            frame.Symbols = template.Symbols;
            var resList = new List<OpCodeR>(finalBody.Length);
            for (int i = 0; i < finalBody.Length; i++) resList.Add(finalBody[i]);
            // rasen neo-overhaul-eh-table-remap: build the instance's EH table from
            // the (delta-shifted) `addr` BEFORE the back-half. The delta-shift above
            // already finalized `addr` for the pre-deletion body, so the table is
            // delta-correct here; RunNeoBackHalf then deletes Pushes and the per-
            // deletion re-map keeps the four EH fields consistent with the post-
            // deletion body (identical to the direct JIT path).
            instance.BuildExceptionHandlerRegister(addr);
            var jit = new JITCompiler(appdomain, declaringType, instance);
            jit.RunNeoBackHalf(ref frame, resList, template.LocVarRegStart, template.TotalRegCnt, template.NeoCatchExRegFinal);
            return true;
        }

        // ---- discrimination (task 4.2): ref-share vs CloneAndPatch ----
        // all-ref AND no T-identity token -> share one cached ref body; else
        // CloneAndPatch fresh. Falls back (returns false) only on a null template.
        internal static bool TryInstantiate(GenericMethodTemplate template, ILMethod instance,
            ILRuntime.Runtime.Enviorment.AppDomain appdomain, ILType declaringType,
            Dictionary<Mono.Cecil.Cil.Instruction, int> addr, ref CompiledFrame frame)
        {
            var typeArgs = instance.GenericArugmentsArray;
            if (typeArgs == null || typeArgs.Length == 0) return false;

            // neo-typeof-generic-param: typed-opcode-category fall-back. The Neo
            // JIT bakes TYPED arms (Stfld_I4/R8, Ldfld_*, conv, ...) into the
            // template body at Translate time, keyed on the capture T's type
            // category. CloneAndPatch re-resolves T-identity TOKENS but NOT the
            // typed opcode CODE, so a concrete T whose typed-field-opcode category
            // differs from the capture T would run a wrong-typed arm (e.g.
            // GetRows<int,int> captures Stfld_I4 for V; GetRows<int,double> reuses
            // it -> the double V is stored as 4 bytes -> corrupt). Compare each
            // concrete arg's Stfld category to the capture arg's; a mismatch ->
            // return false so InitCodeBody falls back to a fresh per-occurrence
            // JIT (the correct reference body). Conservative + correct: a
            // category match (incl. all-ref -> Stfld_Ref uniformly) proceeds via
            // the template; only a category mismatch falls back. Null/short
            // CaptureTypeArgs (an older/synthetic template) -> skip the check
            // (preserve prior behavior).
            if (template.CaptureTypeArgs != null && typeArgs.Length == template.CaptureTypeArgs.Length)
            {
                for (int i = 0; i < typeArgs.Length; i++)
                {
                    var ct = typeArgs[i];
                    var capt = i < template.CaptureTypeArgs.Length ? template.CaptureTypeArgs[i] : null;
                    if (ct == null || capt == null) continue;
                    if (FieldOpcodeCategory(ct, appdomain) != FieldOpcodeCategory(capt, appdomain))
                        return false;
                }
            }

            bool allRef = true;
            for (int i = 0; i < typeArgs.Length; i++)
            {
                if (typeArgs[i] != null && typeArgs[i].IsValueType) { allRef = false; break; }
            }

            if (allRef && !template.HasIdentityToken())
            {
                // Ref-share: build the shared ref body once (from this all-ref
                // instantiation), reuse for every subsequent all-ref instantiation.
                // The dump proved object == string byte-for-byte for token-free
                // bodies, so any all-ref arg-set yields the same body.
                if (template.RefBody.NeoExecuteBody == null)
                {
                    var refAddr = new Dictionary<Mono.Cecil.Cil.Instruction, int>();
                    DoCloneAndPatch(template, instance, appdomain, declaringType, refAddr, ref template.RefBody);
                    template.RefBodyAddr = refAddr;
                }
                frame = template.RefBody;  // struct copy; arrays shared (read-only at runtime)
                if (template.RefBodyAddr != null)
                {
                    addr.Clear();
                    foreach (var kv in template.RefBodyAddr) addr[kv.Key] = kv.Value;
                }
                return true;
            }

            return DoCloneAndPatch(template, instance, appdomain, declaringType, addr, ref frame);
        }

#if DEBUG
        // ===== Step 22 V1 structural-equivalence test hooks (host-side) =====
        // These let a host-side self-check compile the SAME generic instance via
        // BOTH paths -- the per-occurrence JIT (the reference) and CloneAndPatch
        // (the template path) -- and compare the resulting NeoExecuteBody arrays.
        // Gated #if DEBUG so they compile out of release builds.

        // The per-occurrence JIT body for `instance` (fresh Compile, no template).
        internal static OpCodeR[] CompilePerOccurrenceNeoBody(ILRuntime.Runtime.Enviorment.AppDomain appdomain, ILType declaringType, ILMethod instance)
        {
            var jit = new JITCompiler(appdomain, declaringType, instance);
            var addr = new Dictionary<Mono.Cecil.Cil.Instruction, int>();
            var frame = new CompiledFrame();
            jit.Compile(addr, ref frame);
            return frame.NeoExecuteBody;
        }

        // Force-build the template on `definition` by capturing from a capture-
        // eligible instance (T=int), then return the cached template.
        internal static GenericMethodTemplate ForceBuildTemplate(ILRuntime.Runtime.Enviorment.AppDomain appdomain, ILType declaringType, ILMethod definition)
        {
            if (definition.GenericMethodTemplateCache != null) return definition.GenericMethodTemplateCache;
            var capInstance = definition.MakeGenericMethod(new IType[] { appdomain.IntType }) as ILMethod;
            if (capInstance == null) return null;
            var dummy = capInstance.BodyRegister;  // triggers InitCodeBody -> capture
            return definition.GenericMethodTemplateCache;
        }

        // The CloneAndPatch body for `instance` (uses the definition's cached
        // template; CloneAndPatch fresh into a throwaway frame).
        internal static OpCodeR[] CompileViaTemplateNeoBody(ILRuntime.Runtime.Enviorment.AppDomain appdomain, ILType declaringType, ILMethod instance)
        {
            var def = instance.GenericDefinition;
            if (def == null) return null;
            var template = ForceBuildTemplate(appdomain, declaringType, def);
            if (template == null) return null;
            var addr = new Dictionary<Mono.Cecil.Cil.Instruction, int>();
            var frame = new CompiledFrame();
            if (!DoCloneAndPatch(template, instance, appdomain, declaringType, addr, ref frame)) return null;
            return frame.NeoExecuteBody;
        }

        // Step 25 S2: the CloneAndPatch body for `instance` driven by an AOT-
        // reconstructed template (built from a .neo record via BuildFromNeoRecord),
        // NOT the JIT-captured one. The structural-equivalence cell compares this
        // against CompilePerOccurrenceNeoBody (the per-occurrence JIT reference) to
        // prove the AOT-reconstructed template drives CloneAndPatch identically.
        internal static OpCodeR[] CompileViaAotTemplateNeoBody(
            ILRuntime.Runtime.Enviorment.AppDomain appdomain, ILType declaringType,
            ILMethod instance, GenericMethodTemplate aotTemplate)
        {
            if (aotTemplate == null) return null;
            var addr = new Dictionary<Mono.Cecil.Cil.Instruction, int>();
            var frame = new CompiledFrame();
            if (!DoCloneAndPatch(aotTemplate, instance, appdomain, declaringType, addr, ref frame)) return null;
            return frame.NeoExecuteBody;
        }

        // Host-side OpCodeR[] comparator: length + (Code, Register1/2/3, Operand,
        // Operand2/3/4) per index. Returns true if structurally equal.
        internal static bool BodiesEqual(OpCodeR[] a, OpCodeR[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                ref var x = ref a[i];
                ref var y = ref b[i];
                if (x.Code != y.Code) return false;
                if (x.Register1 != y.Register1) return false;
                if (x.Register2 != y.Register2) return false;
                if (x.Register3 != y.Register3) return false;
                if (x.Operand != y.Operand) return false;
                if (x.Operand2 != y.Operand2) return false;
                if (x.Operand3 != y.Operand3) return false;
                if (x.Operand4 != y.Operand4) return false;
            }
            return true;
        }
#endif
    }
#endif
}
