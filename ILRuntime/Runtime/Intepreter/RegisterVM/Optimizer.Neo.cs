#if ENABLE_NEO_MODE
using ILRuntime.Runtime.Intepreter.OpCodes;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace ILRuntime.Runtime.Intepreter.RegisterVM
{
    partial class Optimizer
    {
        public static void LowerNeoOffsets(ref CompiledFrame frame, Enviorment.AppDomain domain)
        {
            if (frame.TotalStructSize > ushort.MaxValue)
            {
                throw new NotSupportedException(string.Format("Neo frame primitive size {0} exceeds maximum byte offset {1}.", frame.TotalStructSize, ushort.MaxValue));
            }

            var localInfos = frame.LocalInfos;
            var body = frame.NeoExecuteBody;
            List<NeoCallParamMap> callParams = new List<NeoCallParamMap>();

            // Step 12: build an alias map for the address-producing opcodes
            // ldloca / ldloca.s / ldflda when they address an in-frame value
            // type. The C# compiler emits `ldloca V; stfld/ldfld` for struct
            // field access and `ldloca V; ldflda f; ...; stfld/ldfld` for a
            // nested value-type field, so the leaf field-access operand is a
            // temp holding an address, not the local. The _Inline field opcodes
            // need the owning value type's actual frame byte offset, so we
            // resolve the operand register back to (underlying local + accumulated
            // nested-field byte offset) here. Each alias entry records the base
            // local register and the accumulated byte offset to add.
            Dictionary<short, NeoAddressAlias> addrAlias = null;
            var localIsRef = frame.LocalIsReference;
            for (int s = 0; s < body.Length; s++)
            {
                OpCodeR so = body[s];
                if (so.Code == OpCodeREnum.Ldloca || so.Code == OpCodeREnum.Ldloca_S)
                {
                    short dest = so.Register1;
                    short src = so.Register2;
                    // Only alias when the source is NOT a plain reference slot.
                    // A reference local (LocalIsReference) produces a genuine
                    // pointer (ref/fixed/byref use) and is consumed by the heap
                    // field path, not the in-frame-VT inline path. A value-type
                    // local -- even a 4-byte one such as `struct { int a; string b; }`
                    // (Size 4, RefCount 1, indistinguishable from a reference by
                    // StackSlotInfo alone) -- is correctly identified here via
                    // LocalIsReference.
                    bool srcIsReference = localIsRef != null && src >= 0 && src < localIsRef.Length && localIsRef[src];
                    if (src >= 0 && src < localInfos.Length && !srcIsReference)
                    {
                        if (addrAlias == null)
                            addrAlias = new Dictionary<short, NeoAddressAlias>();
                        // Inherit any alias the source already carries.
                        NeoAddressAlias inherited;
                        if (addrAlias.TryGetValue(src, out inherited))
                            addrAlias[dest] = new NeoAddressAlias { Reg = inherited.Reg, Offset = inherited.Offset };
                        else
                            addrAlias[dest] = new NeoAddressAlias { Reg = src, Offset = 0 };
                    }
                }
                else if (so.Code == OpCodeREnum.Ldflda)
                {
                    short dest = so.Register1;
                    short src = so.Register2;
                    NeoAddressAlias inherited;
                    if (addrAlias != null && addrAlias.TryGetValue(src, out inherited))
                    {
                        // Fold the nested field's PrimitiveOffset into the
                        // accumulated byte offset.
                        addrAlias[dest] = new NeoAddressAlias
                        {
                            Reg = inherited.Reg,
                            Offset = inherited.Offset + so.Operand2
                        };
                    }
                }
            }

            // Step 17 (addrAlias reconciliation): the address-alias folding is
            // the fast path for the pure in-frame-VT pattern. It is only sound
            // when EVERY consumer of a folded dest is itself foldable (an
            // _Inline field op, Initobj, or another foldable ldflda). When a
            // dest's address ESCAPES that window -- consumed by stind/ldind/
            // stobj/ldobj, passed to a byref Call/Newobj/Push param, or any
            // other opcode the alias model does not cover (Move, Box, etc.) --
            // the dest MUST stay real so the runtime Ldloca/Ldflda arm
            // produces a genuine 8-byte Ref Slot. This pass is strictly
            // ADDITIVE: it only ever REMOVES a dest from addrAlias (making it
            // real), never adds folding. So the Steps 12-16 fast paths that
            // feed only _Inline/Initobj are untouched.
            if (addrAlias != null)
            {
                // Collect the set of alias dests that escape. Removing a dest
                // also invalidates any alias that inherits through it (a
                // chained ldflda whose base escapes is real too).
                HashSet<short> escaped = new HashSet<short>();
                // Track alias registers that have at least one FOLDABLE consumer
                // (an _Inline field op / Initobj / foldable ldflda reading them
                // while live). Eval-stack registers are REUSED across multiple
                // address computations in one method, so the same register can
                // be a folded in-frame-VT address in one live range (consumed by
                // _Inline) AND a genuine byref in another (consumed by stind/a
                // byref call). Evicting such a MIXED register would orphan its
                // _Inline consumers (which read the register as a folded frame
                // offset, not a Ref Slot). So a register is evicted ONLY IF it
                // has NO foldable consumer anywhere -- i.e. it is used PURELY
                // for escaping addresses. Mixed-reuse registers stay folded
                // (their escape consumer reverts to the pre-Step-17 junk/NIE
                // behavior; the pure-byref cases that this step targets are the
                // ones where the address register is dedicated to the byref).
                HashSet<short> hasFoldableUse = new HashSet<short>();
                // CRITICAL: at this (pre-lowering) stage registers are reused,
                // so the same register number can be BOTH an ldloca dest AND a
                // later unrelated value (a call result, etc.). We must only
                // treat a register as "an escaping alias" while it actually
                // holds the address. Track the set of alias dests that are
                // CURRENTLY LIVE as we walk forward. An address producer
                // (ldloca/ldflda) defines a live alias; any opcode that writes
                // that register for another purpose kills it. A non-foldable
                // READ of a live alias marks it escaped. This mirrors the
                // linear-scan assumption of the alias-build pass above.
                HashSet<short> liveAliases = new HashSet<short>();
                for (int s = 0; s < body.Length; s++)
                {
                    OpCodeR op = body[s];
                    bool isAddrProducer = op.Code == OpCodeREnum.Ldloca
                        || op.Code == OpCodeREnum.Ldloca_S
                        || op.Code == OpCodeREnum.Ldflda;

                    // 1) Determine this opcode's READS (operands that, if they
                    //    are a live alias, signal a byref escape).
                    short readAddr1 = -1; // a register that, when read, is the address operand
                    short readAddr2 = -1;
                    bool readsAreFoldable = false;
                    switch (op.Code)
                    {
                        case OpCodeREnum.Ldfld_I1_Inline:
                        case OpCodeREnum.Ldfld_I2_Inline:
                        case OpCodeREnum.Ldfld_I4_Inline:
                        case OpCodeREnum.Ldfld_I8_Inline:
                        case OpCodeREnum.Ldfld_U1_Inline:
                        case OpCodeREnum.Ldfld_U2_Inline:
                        case OpCodeREnum.Ldfld_U4_Inline:
                        case OpCodeREnum.Ldfld_U8_Inline:
                        case OpCodeREnum.Ldfld_R4_Inline:
                        case OpCodeREnum.Ldfld_R8_Inline:
                        case OpCodeREnum.Ldfld_Ref_Inline:
                            // R2 = owning VT address. Foldable consumer.
                            readAddr2 = op.Register2;
                            readsAreFoldable = true;
                            break;
                        case OpCodeREnum.Stfld_I1_Inline:
                        case OpCodeREnum.Stfld_I2_Inline:
                        case OpCodeREnum.Stfld_I4_Inline:
                        case OpCodeREnum.Stfld_I8_Inline:
                        case OpCodeREnum.Stfld_U1_Inline:
                        case OpCodeREnum.Stfld_U2_Inline:
                        case OpCodeREnum.Stfld_U4_Inline:
                        case OpCodeREnum.Stfld_U8_Inline:
                        case OpCodeREnum.Stfld_R4_Inline:
                        case OpCodeREnum.Stfld_R8_Inline:
                        case OpCodeREnum.Stfld_Ref_Inline:
                            // R1 = owning VT address. Foldable consumer.
                            readAddr1 = op.Register1;
                            readsAreFoldable = true;
                            break;
                        case OpCodeREnum.Initobj:
                            // R1 = target address. Foldable consumer.
                            readAddr1 = op.Register1;
                            readsAreFoldable = true;
                            break;
                        case OpCodeREnum.Ldflda:
                            // R2 = base address. Foldable IF the base is a live
                            // alias (this ldflda inherits it). The dest becomes
                            // a new live alias (handled below).
                            readAddr2 = op.Register2;
                            readsAreFoldable = true;
                            break;
                        case OpCodeREnum.Stind_I:
                        case OpCodeREnum.Stind_I1:
                        case OpCodeREnum.Stind_I2:
                        case OpCodeREnum.Stind_I4:
                        case OpCodeREnum.Stind_I8:
                        case OpCodeREnum.Stind_R4:
                        case OpCodeREnum.Stind_R8:
                        case OpCodeREnum.Stind_Ref:
                        case OpCodeREnum.Stobj:
                            // R1 = address. Non-foldable escape.
                            readAddr1 = op.Register1;
                            break;
                        case OpCodeREnum.Ldind_I:
                        case OpCodeREnum.Ldind_I1:
                        case OpCodeREnum.Ldind_I2:
                        case OpCodeREnum.Ldind_I4:
                        case OpCodeREnum.Ldind_I8:
                        case OpCodeREnum.Ldind_R4:
                        case OpCodeREnum.Ldind_R8:
                        case OpCodeREnum.Ldind_U1:
                        case OpCodeREnum.Ldind_U2:
                        case OpCodeREnum.Ldind_U4:
                        case OpCodeREnum.Ldind_Ref:
                        case OpCodeREnum.Ldobj:
                            // R2 = address. Non-foldable escape.
                            readAddr2 = op.Register2;
                            break;
                        default:
                            // Other opcodes do not consume a managed address
                            // via a known operand slot in a way the alias model
                            // tracks; they neither fold nor escape an alias.
                            break;
                    }

                    // 2) Apply the read effect: a non-foldable read of a LIVE
                    //    alias escapes it. A foldable read never escapes, but it
                    //    RECORDS a foldable use of the register (used below to
                    //    avoid evicting a register whose reuse mixes folded and
                    //    real address computations).
                    if (readsAreFoldable)
                    {
                        if (readAddr1 >= 0 && liveAliases.Contains(readAddr1))
                            hasFoldableUse.Add(readAddr1);
                        if (readAddr2 >= 0 && liveAliases.Contains(readAddr2))
                            hasFoldableUse.Add(readAddr2);
                    }
                    else
                    {
                        if (readAddr1 >= 0 && liveAliases.Contains(readAddr1))
                            escaped.Add(readAddr1);
                        if (readAddr2 >= 0 && liveAliases.Contains(readAddr2))
                            escaped.Add(readAddr2);
                    }

                    // Special-case Call-family: a byref parameter escapes the
                    // arg register IF that arg is a live alias. Resolve the
                    // declared signature to find which params are byref.
                    if (op.Code == OpCodeREnum.Call || op.Code == OpCodeREnum.Callvirt
                        || op.Code == OpCodeREnum.Callvirt_IL || op.Code == OpCodeREnum.Callvirt_CLR
                        || op.Code == OpCodeREnum.Callvirt_Interface || op.Code == OpCodeREnum.Newobj)
                    {
                        var targetMethod = domain.GetMethod(op.Operand2);
                        if (targetMethod != null)
                        {
                            int pCnt = targetMethod.ParameterCount;
                            if (targetMethod.HasThis && op.Code != OpCodeREnum.Newobj) pCnt++;
                            bool hasConstrained = op.Operand4 == 1;
                            int pushCnt = hasConstrained ? pCnt : Math.Max(pCnt - 3, 0);
                            int regCnt = pCnt - pushCnt;
                            short[] srcRegs = new short[pCnt];
                            if (regCnt > 0) srcRegs[pCnt - regCnt] = op.Register2;
                            if (regCnt > 1) srcRegs[pCnt - regCnt + 1] = op.Register3;
                            if (regCnt > 2) srcRegs[pCnt - regCnt + 2] = op.Register4;
                            int foundPushes = 0;
                            int scanIdx = s - 1;
                            while (scanIdx >= 0 && foundPushes < pushCnt)
                            {
                                if (body[scanIdx].Code == OpCodeREnum.Push)
                                {
                                    srcRegs[pushCnt - 1 - foundPushes] = body[scanIdx].Register1;
                                    foundPushes++;
                                }
                                scanIdx--;
                            }
                            for (int p = 0; p < pCnt; p++)
                            {
                                bool isByRefParam = false;
                                int paramLogical;
                                if (targetMethod.HasThis && op.Code != OpCodeREnum.Newobj && p == 0)
                                {
                                    isByRefParam = false; // `this` is not byref here
                                }
                                else
                                {
                                    if (targetMethod.HasThis && op.Code != OpCodeREnum.Newobj)
                                        paramLogical = p - 1;
                                    else
                                        paramLogical = p - ((op.Code == OpCodeREnum.Newobj) ? 1 : 0);
                                    if (paramLogical >= 0 && paramLogical < targetMethod.Parameters.Count)
                                        isByRefParam = targetMethod.Parameters[paramLogical].IsByRef;
                                }
                                if (isByRefParam && srcRegs[p] >= 0 && liveAliases.Contains(srcRegs[p]))
                                    escaped.Add(srcRegs[p]);
                            }
                        }
                    }

                    // 3) Kill liveness for any register this opcode WRITES (it
                    //    is about to be redefined for another purpose), EXCEPT
                    //    an address producer (its dest re-establishes an alias,
                    //    handled in step 4) and EXCEPT foldable consumers
                    //    (Ldfld_*_Inline/Stfld_*_Inline/Initobj read the address
                    //    via R1/R2 but do not redefine it -- the same alias may
                    //    feed several inline stores, e.g. `s.a=1; s.b=2`).
                    if (!isAddrProducer && !readsAreFoldable
                        && GetOpcodeDestRegister(ref op, out short writtenDst))
                    {
                        liveAliases.Remove(writtenDst);
                    }
                    // Some opcodes (Push, branches) have no dest register but
                    // still must not carry stale liveness; GetOpcodeDestRegister
                    // returns false for them, which is correct (they clobber
                    // nothing). Initobj has a dest (the target) but it is an
                    // address consumer, handled above as foldable.

                    // 4) Establish liveness for an address producer's dest, but
                    //    ONLY if it is a known alias AND it has not escaped.
                    if (isAddrProducer)
                    {
                        short dest = op.Register1;
                        if (addrAlias.ContainsKey(dest))
                            liveAliases.Add(dest);
                        else
                            liveAliases.Remove(dest);
                    }
                }

                // Evict escaped dests and propagate along the inheritance chain.
                // Each alias's .Reg is the register it inherits from (an ldflda
                // dest inherits from its base; an ldloca dest's .Reg is its
                // source local, which is NOT an alias key). If ANY member of an
                // inheritance chain is real (escaped), the ENTIRE chain must be
                // real: a real ldflda reads its base's Ref Slot at runtime, so
                // the base cannot stay folded (dead/uninitialized); conversely a
                // real ldloca produces a Ref Slot, so an inheritor cannot fold
                // its (now-pointer) operand. So an escape taints the whole chain
                // (the connected component in the .Reg forest). Taint upward
                // (inheritor -> base) and downward (base -> inheritors) to a
                // fixed point, then evict every tainted alias.
                //
                // MIXED-REUSE GUARD: an alias register that ALSO has a foldable
                // consumer (hasFoldableUse) is NEVER evicted, and taint does not
                // cross it -- evicting it would orphan its _Inline consumers
                // (which read it as a folded frame offset, not a Ref Slot). So a
                // register that is reused for BOTH a folded in-frame-VT address
                // AND a genuine byref stays folded; its byref consumer reverts to
                // the pre-Step-17 behavior. The pure-byref cases this step
                // targets (a register dedicated to the byref) are unaffected.
                bool CanEvict(short r)
                {
                    return !hasFoldableUse.Contains(r);
                }
                HashSet<short> tainted = new HashSet<short>();
                Queue<short> queue = new Queue<short>();
                foreach (var e in escaped)
                {
                    if (CanEvict(e) && tainted.Add(e))
                        queue.Enqueue(e);
                }
                // children[b] = set of alias keys whose .Reg == b.
                Dictionary<short, List<short>> children = new Dictionary<short, List<short>>();
                foreach (var kv in addrAlias)
                {
                    if (kv.Value.Reg != kv.Key)
                    {
                        if (!children.TryGetValue(kv.Value.Reg, out var list))
                            children[kv.Value.Reg] = list = new List<short>();
                        list.Add(kv.Key);
                    }
                }
                // BFS the taint through the forest both directions, stopping at
                // any register that has a foldable consumer (mixed-reuse guard).
                while (queue.Count > 0)
                {
                    short cur = queue.Dequeue();
                    // Up: if cur is an alias key, taint its base (.Reg) when that
                    // base is itself an evictable alias key.
                    if (addrAlias.TryGetValue(cur, out var curAlias))
                    {
                        short baseReg = curAlias.Reg;
                        if (baseReg != cur && addrAlias.ContainsKey(baseReg)
                            && CanEvict(baseReg) && tainted.Add(baseReg))
                            queue.Enqueue(baseReg);
                    }
                    // Down: taint every child (inheritor) of cur that is evictable.
                    if (children.TryGetValue(cur, out var kids))
                    {
                        foreach (var c in kids)
                        {
                            if (CanEvict(c) && tainted.Add(c))
                                queue.Enqueue(c);
                        }
                    }
                }
                foreach (var t in tainted)
                    addrAlias.Remove(t);
            }

            // B1: live-range-aware alias snapshot for the lowering walk. The
            // static `addrAlias` map is built once over the whole body and --
            // because eval-stack registers are REUSED across multiple address
            // computations -- its entry for a reused register is the LAST
            // address producer's base (last-write-wins). Resolving an _Inline
            // field access through that static map therefore folds a range-1
            // consumer to the range-2 base -> the field store/load lands on the
            // wrong local (silent corruption; e.g. `p.x=7; ...; ref x` reuses
            // one register for &p then &x). To fold each _Inline consumer to
            // the base of ITS OWN live range, the lowering maintains a
            // per-instruction `liveAliasMap` snapshot: an address producer
            // (ldloca/ldarga/ldflda) establishes its dest's alias; any opcode
            // that redefines a register for a non-address purpose drops it. The
            // _Inline / Initobj consumers below resolve through `liveAliasMap`
            // (falling back to the static map when liveAliasMap has no entry,
            // which preserves the original behavior for non-reused registers).
            // The static `addrAlias` is still authoritative for the gate's
            // escape / eviction decision (it only needs the key-set and the
            // inheritance forest, not the per-range base).
            Dictionary<short, NeoAddressAlias> liveAliasMap = addrAlias != null
                ? new Dictionary<short, NeoAddressAlias>()
                : null;

            // Resolve an _Inline / Initobj operand register to its owning
            // in-frame-VT alias for the CURRENT instruction's live range:
            // prefer the live-range-aware `liveAliasMap`; fall back to the
            // static `addrAlias` when liveAliasMap has no entry (non-reused
            // register, or a producer kind it does not model). When neither has
            // an entry the register is its own base (Reg=reg, Offset=0) -- the
            // original ResolveAddressAlias semantics.
            NeoAddressAlias ResolveLiveAlias(short reg)
            {
                if (liveAliasMap != null && liveAliasMap.TryGetValue(reg, out NeoAddressAlias live))
                    return live;
                if (addrAlias != null && addrAlias.TryGetValue(reg, out NeoAddressAlias resolved))
                    return resolved;
                return new NeoAddressAlias { Reg = reg, Offset = 0 };
            }

            for (int i = 0; i < body.Length; i++)
            {
                OpCodeR op = body[i];
                // B1: snapshot the LOGICAL register state BEFORE the lowering
                // switch runs. In Neo mode DstOffset overlays Register1 and
                // SrcOffset overlays Register2 (OpCodeR is LayoutKind.Explicit),
                // so the per-opcode lowering cases -- which stamp byte offsets
                // into DstOffset/SrcOffset -- DESTROY Register1/Register2. The
                // live-alias maintenance below needs the original register
                // numbers (both to read the address-producer dest/src and to ask
                // GetOpcodeDestRegister whether the opcode writes a register and
                // which), hence the snapshot copy `preOp` here.
                OpCodeR preOp = op;
                bool handled = true;
                switch (op.Code)
                {
                    case OpCodeREnum.Add:
                    case OpCodeREnum.Sub:
                    case OpCodeREnum.Mul:
                    case OpCodeREnum.Div:
                    case OpCodeREnum.Div_Un:
                    case OpCodeREnum.Rem:
                    case OpCodeREnum.Rem_Un:
                    case OpCodeREnum.And:
                    case OpCodeREnum.Or:
                    case OpCodeREnum.Xor:
                    case OpCodeREnum.Shl:
                    case OpCodeREnum.Shr:
                    case OpCodeREnum.Shr_Un:
                    case OpCodeREnum.Ceq:
                    case OpCodeREnum.Cgt:
                    case OpCodeREnum.Cgt_Un:
                    case OpCodeREnum.Clt:
                    case OpCodeREnum.Clt_Un:
                    case OpCodeREnum.Add_I8:
                    case OpCodeREnum.Sub_I8:
                    case OpCodeREnum.Mul_I8:
                    case OpCodeREnum.Div_I8:
                    case OpCodeREnum.Div_Un_I8:
                    case OpCodeREnum.Rem_I8:
                    case OpCodeREnum.Rem_Un_I8:
                    case OpCodeREnum.And_I8:
                    case OpCodeREnum.Or_I8:
                    case OpCodeREnum.Xor_I8:
                    case OpCodeREnum.Shl_I8:
                    case OpCodeREnum.Shr_I8:
                    case OpCodeREnum.Shr_Un_I8:
                    case OpCodeREnum.Add_R4:
                    case OpCodeREnum.Sub_R4:
                    case OpCodeREnum.Mul_R4:
                    case OpCodeREnum.Div_R4:
                    case OpCodeREnum.Rem_R4:
                    case OpCodeREnum.Add_R8:
                    case OpCodeREnum.Sub_R8:
                    case OpCodeREnum.Mul_R8:
                    case OpCodeREnum.Div_R8:
                    case OpCodeREnum.Rem_R8:
                    case OpCodeREnum.Ceq_I8:
                    case OpCodeREnum.Cgt_I8:
                    case OpCodeREnum.Cgt_Un_I8:
                    case OpCodeREnum.Clt_I8:
                    case OpCodeREnum.Clt_Un_I8:
                    case OpCodeREnum.Ceq_R4:
                    case OpCodeREnum.Cgt_R4:
                    case OpCodeREnum.Cgt_Un_R4:
                    case OpCodeREnum.Clt_R4:
                    case OpCodeREnum.Clt_Un_R4:
                    case OpCodeREnum.Ceq_R8:
                    case OpCodeREnum.Cgt_R8:
                    case OpCodeREnum.Cgt_Un_R8:
                    case OpCodeREnum.Clt_R8:
                    case OpCodeREnum.Clt_Un_R8:
                        LowerR1R2R3(ref op, localInfos);
                        break;

                    case OpCodeREnum.Neg:
                    case OpCodeREnum.Not:
                    case OpCodeREnum.Neg_I8:
                    case OpCodeREnum.Not_I8:
                    case OpCodeREnum.Neg_R4:
                    case OpCodeREnum.Neg_R8:
                        LowerR1R2(ref op, localInfos);
                        break;
                    case OpCodeREnum.Move:
                        // Move emits Unsafe.CopyBlock at runtime, so it needs the
                        // copy size (in bytes) in Operand2. Use min(src,dst) so a
                        // wide stack register (8 bytes) copied into a narrow local
                        // (e.g. int = 4 bytes) doesn't clobber neighbouring slots.
                        {
                            int srcReg = op.Register2;
                            int dstReg = op.Register1;
                            bool isRefMove = op.Operand == 1;
                            int srcSz = (srcReg >= 0 && srcReg < localInfos.Length) ? localInfos[srcReg].Size : 0;
                            int dstSz = (dstReg >= 0 && dstReg < localInfos.Length) ? localInfos[dstReg].Size : 0;
                            int dstRef = (dstReg >= 0 && dstReg < localInfos.Length) ? localInfos[dstReg].RefOffset : 0;
                            int sz;
                            if (isRefMove)
                                sz = 4;
                            else if (srcSz > 0 && dstSz > 0)
                                sz = srcSz < dstSz ? srcSz : dstSz;
                            else
                                sz = srcSz > 0 ? srcSz : dstSz;
                            LowerR1R2(ref op, localInfos);
                            op.Operand = isRefMove ? 1 : 0;
                            op.Operand2 = sz;
                            op.Operand3 = dstRef;
                        }
                        break;
                    case OpCodeREnum.Move_Vt:
                        // Step 12b: lower a whole-value-type copy. LowerMove
                        // (in TypeSpecializeNeoOpcodes) already rewrote the Move
                        // to Move_Vt while Register1/2 were still register
                        // indices; here we stamp the standalone Operand fields
                        // from localInfos (the authoritative slot layout) and
                        // LowerR1R2 to set DstOffset/SrcOffset.
                        //
                        // OpCodeR is [StructLayout(LayoutKind.Explicit)]:
                        //   offset 4: Register1 / DstOffset (ALIASED)
                        //   offset 6: Register2 / SrcOffset (ALIASED)
                        //   offset 8: Register3 / OperandOffset / Operand (ALIASED)
                        //   offset 12: Operand2            (STANDALONE)
                        //   offset 16: Operand3            (STANDALONE)
                        //   offset 20: Operand4            (STANDALONE)
                        // LowerR1R2 only writes DstOffset/SrcOffset (offsets 4/6).
                        // We store primSize/dstRef/srcRef/refCount in the
                        // standalone Operand2/3/4 fields so they survive lowering
                        // intact. We additionally reuse `Operand` (offset 8,
                        // aliased with Register3) to carry the SRC ref-run base;
                        // this is SAFE because Move_Vt never uses Register3
                        // (only Register1/2 via LowerR1R2), so overwriting the
                        // aliased int at offset 8 cannot corrupt a needed
                        // register index. (Mirrors how plain Move stores its
                        // isRefMove flag in Operand.)
                        {
                            int srcReg = op.Register2;
                            int dstReg = op.Register1;
                            int srcSz = (srcReg >= 0 && srcReg < localInfos.Length) ? localInfos[srcReg].Size : 0;
                            int dstSz = (dstReg >= 0 && dstReg < localInfos.Length) ? localInfos[dstReg].Size : 0;
                            // min(src,dst): same rule as plain Move, to avoid
                            // clobbering neighbouring slots when widths differ.
                            int sz = (srcSz > 0 && dstSz > 0) ? (srcSz < dstSz ? srcSz : dstSz)
                                                              : (srcSz > 0 ? srcSz : dstSz);
                            int srcRef = (srcReg >= 0 && srcReg < localInfos.Length) ? localInfos[srcReg].RefOffset : 0;
                            int dstRef = (dstReg >= 0 && dstReg < localInfos.Length) ? localInfos[dstReg].RefOffset : 0;
                            int dstRefCount = (dstReg >= 0 && dstReg < localInfos.Length) ? localInfos[dstReg].RefCount : 0;
                            LowerR1R2(ref op, localInfos);
                            op.Operand2 = sz;        // primitive byte size to CopyBlock
                            op.Operand3 = dstRef;    // dst ref-run base (frame-ref offset)
                            op.Operand = srcRef;     // src ref-run base (reuses the former isRefMove flag field)
                            op.Operand4 = dstRefCount; // number of ref slots to copy
                        }
                        break;
                    case OpCodeREnum.Conv_I:
                    case OpCodeREnum.Conv_I1:
                    case OpCodeREnum.Conv_I2:
                    case OpCodeREnum.Conv_I4:
                    case OpCodeREnum.Conv_I8:
                    case OpCodeREnum.Conv_R4:
                    case OpCodeREnum.Conv_R8:
                    case OpCodeREnum.Conv_R_Un:
                    case OpCodeREnum.Conv_U:
                    case OpCodeREnum.Conv_U1:
                    case OpCodeREnum.Conv_U2:
                    case OpCodeREnum.Conv_U4:
                    case OpCodeREnum.Conv_U8:
                    case OpCodeREnum.Addi:
                    case OpCodeREnum.Subi:
                    case OpCodeREnum.Muli:
                    case OpCodeREnum.Divi:
                    case OpCodeREnum.Divi_Un:
                    case OpCodeREnum.Remi:
                    case OpCodeREnum.Remi_Un:
                    case OpCodeREnum.Andi:
                    case OpCodeREnum.Ori:
                    case OpCodeREnum.Xori:
                    case OpCodeREnum.Shli:
                    case OpCodeREnum.Shri:
                    case OpCodeREnum.Shri_Un:
                    case OpCodeREnum.Ceqi:
                    case OpCodeREnum.Cgti:
                    case OpCodeREnum.Cgti_Un:
                    case OpCodeREnum.Clti:
                    case OpCodeREnum.Clti_Un:
                    case OpCodeREnum.Addi_I8:
                    case OpCodeREnum.Subi_I8:
                    case OpCodeREnum.Muli_I8:
                    case OpCodeREnum.Divi_I8:
                    case OpCodeREnum.Divi_Un_I8:
                    case OpCodeREnum.Remi_I8:
                    case OpCodeREnum.Remi_Un_I8:
                    case OpCodeREnum.Andi_I8:
                    case OpCodeREnum.Ori_I8:
                    case OpCodeREnum.Xori_I8:
                    case OpCodeREnum.Shli_I8:
                    case OpCodeREnum.Shri_I8:
                    case OpCodeREnum.Shri_Un_I8:
                    case OpCodeREnum.Addi_R4:
                    case OpCodeREnum.Subi_R4:
                    case OpCodeREnum.Muli_R4:
                    case OpCodeREnum.Divi_R4:
                    case OpCodeREnum.Remi_R4:
                    case OpCodeREnum.Addi_R8:
                    case OpCodeREnum.Subi_R8:
                    case OpCodeREnum.Muli_R8:
                    case OpCodeREnum.Divi_R8:
                    case OpCodeREnum.Remi_R8:
                    case OpCodeREnum.Ceqi_I8:
                    case OpCodeREnum.Cgti_I8:
                    case OpCodeREnum.Cgti_Un_I8:
                    case OpCodeREnum.Clti_I8:
                    case OpCodeREnum.Clti_Un_I8:
                    case OpCodeREnum.Ceqi_R4:
                    case OpCodeREnum.Cgti_R4:
                    case OpCodeREnum.Cgti_Un_R4:
                    case OpCodeREnum.Clti_R4:
                    case OpCodeREnum.Clti_Un_R4:
                    case OpCodeREnum.Ceqi_R8:
                    case OpCodeREnum.Cgti_R8:
                    case OpCodeREnum.Cgti_Un_R8:
                    case OpCodeREnum.Clti_R8:
                    case OpCodeREnum.Clti_Un_R8:
                        LowerR1R2(ref op, localInfos);
                        break;

                    case OpCodeREnum.Beq:
                    case OpCodeREnum.Bne_Un:
                    case OpCodeREnum.Blt:
                    case OpCodeREnum.Blt_Un:
                    case OpCodeREnum.Bgt:
                    case OpCodeREnum.Bgt_Un:
                    case OpCodeREnum.Ble:
                    case OpCodeREnum.Ble_Un:
                    case OpCodeREnum.Bge:
                    case OpCodeREnum.Bge_Un:
                    case OpCodeREnum.Beq_I8:
                    case OpCodeREnum.Bne_Un_I8:
                    case OpCodeREnum.Blt_I8:
                    case OpCodeREnum.Blt_Un_I8:
                    case OpCodeREnum.Bgt_I8:
                    case OpCodeREnum.Bgt_Un_I8:
                    case OpCodeREnum.Ble_I8:
                    case OpCodeREnum.Ble_Un_I8:
                    case OpCodeREnum.Bge_I8:
                    case OpCodeREnum.Bge_Un_I8:
                    case OpCodeREnum.Beq_R4:
                    case OpCodeREnum.Bne_Un_R4:
                    case OpCodeREnum.Blt_R4:
                    case OpCodeREnum.Blt_Un_R4:
                    case OpCodeREnum.Bgt_R4:
                    case OpCodeREnum.Bgt_Un_R4:
                    case OpCodeREnum.Ble_R4:
                    case OpCodeREnum.Ble_Un_R4:
                    case OpCodeREnum.Bge_R4:
                    case OpCodeREnum.Bge_Un_R4:
                    case OpCodeREnum.Beq_R8:
                    case OpCodeREnum.Bne_Un_R8:
                    case OpCodeREnum.Blt_R8:
                    case OpCodeREnum.Blt_Un_R8:
                    case OpCodeREnum.Bgt_R8:
                    case OpCodeREnum.Bgt_Un_R8:
                    case OpCodeREnum.Ble_R8:
                    case OpCodeREnum.Ble_Un_R8:
                    case OpCodeREnum.Bge_R8:
                    case OpCodeREnum.Bge_Un_R8:
                        LowerR1R2(ref op, localInfos);
                        break;

                    case OpCodeREnum.Brtrue:
                    case OpCodeREnum.Brtrue_S:
                    case OpCodeREnum.Brfalse:
                    case OpCodeREnum.Brfalse_S:
                        {
                            short r1 = op.Register1;
                            int size = localInfos[r1].Size;
                            int off1 = localInfos[r1].Offset;
                            op.Operand2 = size;
                            op.DstOffset = (ushort)off1;
                        }
                        break;
                    case OpCodeREnum.Beqi:
                    case OpCodeREnum.Bnei_Un:
                    case OpCodeREnum.Blti:
                    case OpCodeREnum.Blti_Un:
                    case OpCodeREnum.Bgti:
                    case OpCodeREnum.Bgti_Un:
                    case OpCodeREnum.Blei:
                    case OpCodeREnum.Blei_Un:
                    case OpCodeREnum.Bgei:
                    case OpCodeREnum.Bgei_Un:
                    case OpCodeREnum.Beqi_I8:
                    case OpCodeREnum.Bnei_Un_I8:
                    case OpCodeREnum.Blti_I8:
                    case OpCodeREnum.Blti_Un_I8:
                    case OpCodeREnum.Bgti_I8:
                    case OpCodeREnum.Bgti_Un_I8:
                    case OpCodeREnum.Blei_I8:
                    case OpCodeREnum.Blei_Un_I8:
                    case OpCodeREnum.Bgei_I8:
                    case OpCodeREnum.Bgei_Un_I8:
                    case OpCodeREnum.Beqi_R4:
                    case OpCodeREnum.Bnei_Un_R4:
                    case OpCodeREnum.Blti_R4:
                    case OpCodeREnum.Blti_Un_R4:
                    case OpCodeREnum.Bgti_R4:
                    case OpCodeREnum.Bgti_Un_R4:
                    case OpCodeREnum.Blei_R4:
                    case OpCodeREnum.Blei_Un_R4:
                    case OpCodeREnum.Bgei_R4:
                    case OpCodeREnum.Bgei_Un_R4:
                    case OpCodeREnum.Beqi_R8:
                    case OpCodeREnum.Bnei_Un_R8:
                    case OpCodeREnum.Blti_R8:
                    case OpCodeREnum.Blti_Un_R8:
                    case OpCodeREnum.Bgti_R8:
                    case OpCodeREnum.Bgti_Un_R8:
                    case OpCodeREnum.Blei_R8:
                    case OpCodeREnum.Blei_Un_R8:
                    case OpCodeREnum.Bgei_R8:
                    case OpCodeREnum.Bgei_Un_R8:
                        {
                            // Branch-on-immediate / branch-on-register comparisons
                            // read Register1 as a VALUE (the compared operand),
                            // NOT as a managed address. They must therefore resolve
                            // straight to the operand's own frame slot -- never
                            // through the address-alias map. A reused eval-stack
                            // register can be an ldloca/ldflda alias dest in one
                            // live range AND hold a comparison value in a LATER
                            // live range (e.g. `p.x=7; ref x; if (p.x==7)` reuses
                            // one register for &p, &x, then the p.x load result);
                            // routing the branch operand through the alias map
                            // would redirect it to the alias base (the wrong local)
                            // -> the branch reads the wrong slot (B1).
                            //
                            // F-8 / [OPT-HARDEN-3]: the I8/R4/R8 immediate-branch
                            // forms carry their 8-byte/4-byte constant in
                            // OperandLong/OperandDouble (offset 12..19) /
                            // OperandFloat (offset 8..11). The legacy I4-only arm
                            // below also stamped `op.Operand3 = RefOffset`
                            // (offset 16) -- that field is NEVER read by ANY
                            // immediate-branch runtime arm, but for the I8/R4/R8
                            // forms it OVERLAPS the high bytes of the immediate
                            // constant (Operand3 @16 aliases the high half of
                            // OperandLong/OperandDouble), corrupting the constant
                            // -> silent wrong branch. The long-works / double-fails
                            // signature: copy-prop folds a `double` Ldc_R8 into the
                            // immediate form (Bnei_Un_R8) but leaves a `long`
                            // Ldc_I8 in a register (register-register Bne_Un_I8),
                            // so only the R8/R4 immediate form trips the corruption.
                            // Fix: resolve DstOffset (always needed) but SKIP the
                            // dead Operand3 write for the I8/R4/R8 forms (their
                            // immediate lives at offset 12..19). The I4 forms keep
                            // the Operand3 write byte-identical (their immediate
                            // Operand @8 does not collide; preserved for safety).
                            short r1 = op.Register1;
                            op.DstOffset = (ushort)localInfos[r1].Offset;
                            bool immLarge =
                                op.Code == OpCodeREnum.Bnei_Un_I8 || op.Code == OpCodeREnum.Beqi_I8 ||
                                op.Code == OpCodeREnum.Blti_I8 || op.Code == OpCodeREnum.Blti_Un_I8 ||
                                op.Code == OpCodeREnum.Bgti_I8 || op.Code == OpCodeREnum.Bgti_Un_I8 ||
                                op.Code == OpCodeREnum.Blei_I8 || op.Code == OpCodeREnum.Blei_Un_I8 ||
                                op.Code == OpCodeREnum.Bgei_I8 || op.Code == OpCodeREnum.Bgei_Un_I8 ||
                                op.Code == OpCodeREnum.Bnei_Un_R4 || op.Code == OpCodeREnum.Beqi_R4 ||
                                op.Code == OpCodeREnum.Blti_R4 || op.Code == OpCodeREnum.Blti_Un_R4 ||
                                op.Code == OpCodeREnum.Bgti_R4 || op.Code == OpCodeREnum.Bgti_Un_R4 ||
                                op.Code == OpCodeREnum.Blei_R4 || op.Code == OpCodeREnum.Blei_Un_R4 ||
                                op.Code == OpCodeREnum.Bgei_R4 || op.Code == OpCodeREnum.Bgei_Un_R4 ||
                                op.Code == OpCodeREnum.Bnei_Un_R8 || op.Code == OpCodeREnum.Beqi_R8 ||
                                op.Code == OpCodeREnum.Blti_R8 || op.Code == OpCodeREnum.Blti_Un_R8 ||
                                op.Code == OpCodeREnum.Bgti_R8 || op.Code == OpCodeREnum.Bgti_Un_R8 ||
                                op.Code == OpCodeREnum.Blei_R8 || op.Code == OpCodeREnum.Blei_Un_R8 ||
                                op.Code == OpCodeREnum.Bgei_R8 || op.Code == OpCodeREnum.Bgei_Un_R8;
                            if (!immLarge)
                                op.Operand3 = localInfos[r1].RefOffset;
                        }
                        break;
                    case OpCodeREnum.Initobj:
                        {
                            // Step 12: Initobj on an in-frame IL value type must
                            // also null the slot's reference-field mStack slots.
                            // Stamp the target slot's RefOffset into Operand3
                            // (spare: Initobj only uses Operand=type token and
                            // DstOffset=slot byte offset) so the ExecuteNeo arm
                            // can locate the ref region without a runtime lookup.
                            // The target is typically an ldloca dest (the C#
                            // compiler emits `ldloca V; initobj` for
                            // default(struct)), so resolve through the alias map.
                            short r1 = ResolveLiveAlias(op.Register1).Reg;
                            op.Operand3 = localInfos[r1].RefOffset;
                            op.DstOffset = (ushort)localInfos[r1].Offset;
                        }
                        break;
                    case OpCodeREnum.Ldnull:
                    case OpCodeREnum.Ldstr:
                    case OpCodeREnum.Ldc_I4_M1:
                    case OpCodeREnum.Ldc_I4_0:
                    case OpCodeREnum.Ldc_I4_1:
                    case OpCodeREnum.Ldc_I4_2:
                    case OpCodeREnum.Ldc_I4_3:
                    case OpCodeREnum.Ldc_I4_4:
                    case OpCodeREnum.Ldc_I4_5:
                    case OpCodeREnum.Ldc_I4_6:
                    case OpCodeREnum.Ldc_I4_7:
                    case OpCodeREnum.Ldc_I4_8:
                    case OpCodeREnum.Ldc_I4:
                    case OpCodeREnum.Ldc_I4_S:
                    case OpCodeREnum.Ldc_I8:
                    case OpCodeREnum.Ldc_R4:
                    case OpCodeREnum.Ldc_R8:
                        if (op.Code == OpCodeREnum.Ldstr)
                            op.Operand = localInfos[op.Register1].RefOffset;
                        LowerR1(ref op, localInfos);
                        break;
                    // Step 19: ldftn / ldvirtftn produce an IMethod (a managed CLR
                    // heap object) -> the dest is a Neo ref slot (mStack index in
                    // the byte region, like Ldstr). Stamp the dest ref offset into
                    // Operand and lower R1 (DstOffset for the index write). For
                    // ldvirtftn also lower R2 (SrcOffset for the `this` source).
                    case OpCodeREnum.Ldftn:
                    case OpCodeREnum.Ldvirtftn:
                        op.Operand = localInfos[op.Register1].RefOffset;
                        LowerR1(ref op, localInfos);
                        if (op.Code == OpCodeREnum.Ldvirtftn)
                            op.SrcOffset = (ushort)localInfos[op.Register2].Offset;
                        break;
                    case OpCodeREnum.Ret:
                        if (op.Register1 >= 0)
                        {
                            // neo-ret-vt-with-ref-fields: stamp the return
                            // register's RefOffset into the spare Operand3
                            // (@16) so the ExecuteNeo Ret arm can locate the
                            // return value's callee-side ref region for a
                            // value-type return WITH reference fields. Ret uses
                            // only Register1/DstOffset (the return value's
                            // primitive byte offset); Operand/Operand2/Operand3/
                            // Operand4 are all spare. Operand3 mirrors the
                            // convention used by Initobj (its target RefOffset)
                            // and Move_Vt/Box (their ref-run bases). The return
                            // register is the eval-stack top (never an ldloca
                            // alias dest), but ResolveLiveAlias is harmless +
                            // symmetric with Initobj.
                            short retR1 = ResolveLiveAlias(op.Register1).Reg;
                            // A VOID method's `ret` (JIT Code.Ret leaves
                            // Register1 at its default 0 when hasReturn==false)
                            // reaches here with retR1 pointing past the (possibly
                            // empty) localInfos -- a phantom register that holds
                            // no value, so there is NO return ref region to stamp.
                            // ExecuteNeo only reads Operand3 when there IS a
                            // return value (a value-type WITH ref fields), so an
                            // out-of-range index resolves to RefOffset 0 (the
                            // Operand3 default; harmless). Mirrors the defensive
                            // `reg >= 0 && reg < localInfos.Length` guard already
                            // in LowerR1 (which runs on the NEXT line for the
                            // DstOffset stamp) -- without this guard the operand3
                            // read threw IndexOutOfRangeException on the empty
                            // static .cctor (NeoStep24CliProbe..cctor), a
                            // regression from the ret-vt-with-ref-fields change.
                            op.Operand3 = (retR1 >= 0 && retR1 < localInfos.Length)
                                ? localInfos[retR1].RefOffset : 0;
                            LowerR1(ref op, localInfos);
                        }
                        break;
                    case OpCodeREnum.Box:
                    case OpCodeREnum.Unbox:
                    case OpCodeREnum.Unbox_Any:
                    case OpCodeREnum.Isinst:        // Step 15: type-check reads the src ref slot, writes the dst ref slot in place (R1==R2).
                    case OpCodeREnum.Castclass:     // Step 15
                        {
                            short r1 = op.Register1;
                            short r2 = op.Register2;
                            int off1 = localInfos[r1].Offset;
                            int off2 = localInfos[r2].Offset;
                            int ref1 = localInfos[r1].RefOffset;
                            int ref2 = localInfos[r2].RefOffset;
                            op.DstOffset = (ushort)off1;
                            op.SrcOffset = (ushort)off2;
                            op.Operand3 = ref1;
                            op.Operand4 = ref2;
                        }
                        break;
                    case OpCodeREnum.Ldfld_I1:
                    case OpCodeREnum.Ldfld_I2:
                    case OpCodeREnum.Ldfld_I4:
                    case OpCodeREnum.Ldfld_I8:
                    case OpCodeREnum.Ldfld_U1:
                    case OpCodeREnum.Ldfld_U2:
                    case OpCodeREnum.Ldfld_U4:
                    case OpCodeREnum.Ldfld_U8:
                    case OpCodeREnum.Ldfld_R4:
                    case OpCodeREnum.Ldfld_R8:
                    case OpCodeREnum.Ldfld_Ref:
                    case OpCodeREnum.Ldfld_Value:
                    case OpCodeREnum.Ldloca:
                    case OpCodeREnum.Ldloca_S:
                    case OpCodeREnum.Ldarga:
                    case OpCodeREnum.Ldarga_S:
                    case OpCodeREnum.Ldflda:
                        {
                            short r1 = op.Register1;
                            short r2 = op.Register2;
                            if (op.Code == OpCodeREnum.Ldfld_Ref)
                                op.Operand = localInfos[r1].RefOffset;
                            op.DstOffset = (ushort)localInfos[r1].Offset;
                            op.SrcOffset = (ushort)localInfos[r2].Offset;
                        }
                        break;
                    case OpCodeREnum.Stfld_I1:
                    case OpCodeREnum.Stfld_I2:
                    case OpCodeREnum.Stfld_I4:
                    case OpCodeREnum.Stfld_I8:
                    case OpCodeREnum.Stfld_U1:
                    case OpCodeREnum.Stfld_U2:
                    case OpCodeREnum.Stfld_U4:
                    case OpCodeREnum.Stfld_U8:
                    case OpCodeREnum.Stfld_R4:
                    case OpCodeREnum.Stfld_R8:
                    case OpCodeREnum.Stfld_Ref:
                    case OpCodeREnum.Stfld_Value:
                        {
                            short r1 = op.Register1;
                            short r2 = op.Register2;
                            op.DstOffset = (ushort)localInfos[r1].Offset;
                            op.SrcOffset = (ushort)localInfos[r2].Offset;
                        }
                        break;
                    // ---- Step 12: in-frame value-type inline field access ----
                    // Primitive inline variants: same DstOffset/SrcOffset shape
                    // as the heap family, but the offsets address the frame
                    // byte region directly (no mStack dereference).
                    case OpCodeREnum.Ldfld_I1_Inline:
                    case OpCodeREnum.Ldfld_I2_Inline:
                    case OpCodeREnum.Ldfld_I4_Inline:
                    case OpCodeREnum.Ldfld_I8_Inline:
                    case OpCodeREnum.Ldfld_U1_Inline:
                    case OpCodeREnum.Ldfld_U2_Inline:
                    case OpCodeREnum.Ldfld_U4_Inline:
                    case OpCodeREnum.Ldfld_U8_Inline:
                    case OpCodeREnum.Ldfld_R4_Inline:
                    case OpCodeREnum.Ldfld_R8_Inline:
                        {
                            short r1 = op.Register1; // dest temp
                            // owning in-frame VT slot (possibly an ldloca/ldflda alias)
                            NeoAddressAlias owner = ResolveLiveAlias(op.Register2);
                            op.DstOffset = (ushort)localInfos[r1].Offset;
                            op.SrcOffset = (ushort)(localInfos[owner.Reg].Offset + owner.Offset);
                            // Operand2 (field PrimitiveOffset) is left as-is.
                        }
                        break;
                    case OpCodeREnum.Stfld_I1_Inline:
                    case OpCodeREnum.Stfld_I2_Inline:
                    case OpCodeREnum.Stfld_I4_Inline:
                    case OpCodeREnum.Stfld_I8_Inline:
                    case OpCodeREnum.Stfld_U1_Inline:
                    case OpCodeREnum.Stfld_U2_Inline:
                    case OpCodeREnum.Stfld_U4_Inline:
                    case OpCodeREnum.Stfld_U8_Inline:
                    case OpCodeREnum.Stfld_R4_Inline:
                    case OpCodeREnum.Stfld_R8_Inline:
                        {
                            // owning in-frame VT slot (possibly an ldloca/ldflda alias)
                            NeoAddressAlias owner = ResolveLiveAlias(op.Register1);
                            short r2 = op.Register2; // value temp
                            op.DstOffset = (ushort)(localInfos[owner.Reg].Offset + owner.Offset);
                            op.SrcOffset = (ushort)localInfos[r2].Offset;
                        }
                        break;
                    // Ref inline variants: resolve the absolute frame-ref
                    // index = owningSlot.RefOffset + field.ReferenceOffset.
                    // Operand3 still holds the field's ReferenceOffset (set by
                    // the JIT); we fold in the owning slot's RefOffset here.
                    // NOTE: nested-VT ref-field accumulation (ldflda of a VT
                    // that itself has ref fields) is not folded here -- Step 12
                    // tests only nest primitive value types.
                    case OpCodeREnum.Ldfld_Ref_Inline:
                        {
                            short r1 = op.Register1; // dest temp
                            NeoAddressAlias owner = ResolveLiveAlias(op.Register2);
                            op.Operand = localInfos[owner.Reg].RefOffset + op.Operand3; // source field abs ref index
                            op.Operand4 = localInfos[r1].RefOffset;                     // dest temp ref offset
                            op.DstOffset = (ushort)localInfos[r1].Offset;
                            op.SrcOffset = (ushort)(localInfos[owner.Reg].Offset + owner.Offset);
                        }
                        break;
                    case OpCodeREnum.Stfld_Ref_Inline:
                        {
                            NeoAddressAlias owner = ResolveLiveAlias(op.Register1);
                            short r2 = op.Register2; // value temp
                            op.Operand = localInfos[owner.Reg].RefOffset + op.Operand3; // dest field abs ref index
                            op.DstOffset = (ushort)(localInfos[owner.Reg].Offset + owner.Offset);
                            op.SrcOffset = (ushort)localInfos[r2].Offset;
                        }
                        break;
                    // ---- Step 16: array element access lowering ----
                    // Encoding (spare-field choice): the JIT lays these out with up
                    // to 3 registers (R1/R2/R3). After lowering we carry:
                    //   DstOffset  = R1 byte offset
                    //   SrcOffset  = R2 byte offset
                    //   Operand4   = R3 byte offset (the "third" register -- index
                    //                for Ldelem, value for Stelem). Operand4 (offset
                    //                20) does NOT alias Operand (offset 8), which
                    //                holds the element-type token for Newarr, so it
                    //                is safe to reuse here.
                    //   Operand3   = the ref offset that the per-arm runtime needs
                    //                (dest array ref for Newarr; dest ref for
                    //                Ldelem Ref/Any/VT result; value ref for Stelem
                    //                Ref/Any/VT value). Mirrors the Box/Isinst arm
                    //                (@430-447) which proves Operand3/Operand4 are
                    //                reusable scratch post-lowering.
                    // IMPORTANT: do NOT touch Operand -- Newarr carries its
                    // element-type token there (set by JIT @2064). Ldelem_Any /
                    // Stelem_Any do NOT carry a token in Operand (the JIT only
                    // stamps Register1/2/3 for them), so the element type is
                    // recovered at runtime from the array's CLR type.
                    case OpCodeREnum.Newarr:
                        {
                            // R1 = dest array (ref temp), R2 = count (int).
                            short r1 = op.Register1;
                            short r2 = op.Register2;
                            op.DstOffset = (ushort)localInfos[r1].Offset;
                            op.SrcOffset = (ushort)localInfos[r2].Offset;
                            op.Operand3 = localInfos[r1].RefOffset; // dest array ref slot
                        }
                        break;
                    case OpCodeREnum.Ldlen:
                        {
                            // R1 = dest (int), R2 = array (ref).
                            short r1 = op.Register1;
                            short r2 = op.Register2;
                            op.DstOffset = (ushort)localInfos[r1].Offset;
                            op.SrcOffset = (ushort)localInfos[r2].Offset;
                        }
                        break;
                    case OpCodeREnum.Ldelem_I1:
                    case OpCodeREnum.Ldelem_U1:
                    case OpCodeREnum.Ldelem_I2:
                    case OpCodeREnum.Ldelem_U2:
                    case OpCodeREnum.Ldelem_I4:
                    case OpCodeREnum.Ldelem_U4:
                    case OpCodeREnum.Ldelem_I8:
                    case OpCodeREnum.Ldelem_I:
                    case OpCodeREnum.Ldelem_R4:
                    case OpCodeREnum.Ldelem_R8:
                    case OpCodeREnum.Ldelem_Ref:
                    case OpCodeREnum.Ldelem_Any:
                        {
                            // R1 = dest, R2 = array (ref), R3 = index (int).
                            short r1 = op.Register1;
                            short r2 = op.Register2;
                            short r3 = op.Register3;
                            op.DstOffset = (ushort)localInfos[r1].Offset;
                            op.SrcOffset = (ushort)localInfos[r2].Offset;
                            op.Operand4 = localInfos[r3].Offset; // index byte offset
                            op.Operand3 = localInfos[r1].RefOffset; // dest ref slot (Ref/Any/VT)
                        }
                        break;
                    case OpCodeREnum.Stelem_I:    // native-int store (rare; JIT lowers it
                                                // to the 3-register form). Lowered here so
                                                // the encoding is correct; the interpreter
                                                // arm stays a Step-tagged NIE (out of scope).
                    case OpCodeREnum.Stelem_I1:
                    case OpCodeREnum.Stelem_I2:
                    case OpCodeREnum.Stelem_I4:
                    case OpCodeREnum.Stelem_I8:
                    case OpCodeREnum.Stelem_R4:
                    case OpCodeREnum.Stelem_R8:
                    case OpCodeREnum.Stelem_Ref:
                    case OpCodeREnum.Stelem_Any:
                        {
                            // R1 = array (ref), R2 = index (int), R3 = value.
                            short r1 = op.Register1;
                            short r2 = op.Register2;
                            short r3 = op.Register3;
                            op.DstOffset = (ushort)localInfos[r1].Offset; // array byte offset
                            op.SrcOffset = (ushort)localInfos[r2].Offset; // index byte offset
                            op.Operand4 = localInfos[r3].Offset; // value byte offset
                            op.Operand3 = localInfos[r3].RefOffset; // value ref slot (Ref/Any/VT)
                        }
                        break;
                    // ---- Step 17: byref store/load-indirect + array-address ----
                    // Stind_* / Stobj: R1 = address (Ref Slot temp), R2 = value.
                    //   -> DstOffset = address temp byte offset;
                    //      SrcOffset = value temp byte offset;
                    //      (Stobj keeps its type token in Operand, untouched.)
                    case OpCodeREnum.Stind_I:
                    case OpCodeREnum.Stind_I1:
                    case OpCodeREnum.Stind_I2:
                    case OpCodeREnum.Stind_I4:
                    case OpCodeREnum.Stind_I8:
                    case OpCodeREnum.Stind_R4:
                    case OpCodeREnum.Stind_R8:
                    case OpCodeREnum.Stind_Ref:
                    case OpCodeREnum.Stobj:
                        {
                            short r1 = op.Register1;
                            short r2 = op.Register2;
                            op.DstOffset = (ushort)localInfos[r1].Offset; // address
                            op.SrcOffset = (ushort)localInfos[r2].Offset; // value
                            // Stobj/Stind_Ref may need the value temp's ref slot
                            // (a ref-typed store through the pointer). Stamp it
                            // into Operand3 for the runtime arm.
                            op.Operand3 = localInfos[r2].RefOffset;
                        }
                        break;
                    // Ldind_* / Ldobj: R1 = dest, R2 = address (Ref Slot temp).
                    //   -> DstOffset = dest byte offset;
                    //      SrcOffset = address temp byte offset;
                    //      (Ldobj keeps its type token in Operand, untouched.)
                    //      Operand3 = dest ref slot (for Ref/VT results).
                    case OpCodeREnum.Ldind_I:
                    case OpCodeREnum.Ldind_I1:
                    case OpCodeREnum.Ldind_I2:
                    case OpCodeREnum.Ldind_I4:
                    case OpCodeREnum.Ldind_I8:
                    case OpCodeREnum.Ldind_R4:
                    case OpCodeREnum.Ldind_R8:
                    case OpCodeREnum.Ldind_U1:
                    case OpCodeREnum.Ldind_U2:
                    case OpCodeREnum.Ldind_U4:
                    case OpCodeREnum.Ldind_Ref:
                    case OpCodeREnum.Ldobj:
                        {
                            short r1 = op.Register1;
                            short r2 = op.Register2;
                            op.DstOffset = (ushort)localInfos[r1].Offset; // dest
                            op.SrcOffset = (ushort)localInfos[r2].Offset; // address
                            op.Operand3 = localInfos[r1].RefOffset; // dest ref slot (Ref/VT)
                        }
                        break;
                    // Ldelema: R1 = dest (== array eval slot, reused), R2 = array
                    // (same slot), R3 = index. Mirrors Ldelem's third-register
                    // encoding: Operand4 = index byte offset. Produces a Ref Slot
                    // (arrayMStackIdx, elementByteOffset) at runtime.
                    case OpCodeREnum.Ldelema:
                        {
                            short r1 = op.Register1;
                            short r2 = op.Register2;
                            short r3 = op.Register3;
                            op.DstOffset = (ushort)localInfos[r1].Offset;
                            op.SrcOffset = (ushort)localInfos[r2].Offset; // array mStack idx
                            op.Operand4 = localInfos[r3].Offset; // index byte offset
                        }
                        break;
                    case OpCodeREnum.Br:
                    case OpCodeREnum.Br_S:
                    case OpCodeREnum.Nop:
                        break;
                    case OpCodeREnum.Call:
                    case OpCodeREnum.Callvirt:
                    case OpCodeREnum.Callvirt_IL:
                    case OpCodeREnum.Callvirt_CLR:
                    case OpCodeREnum.Callvirt_Interface:
                    case OpCodeREnum.Call_Redirect:
                    case OpCodeREnum.Newobj:
                        {
                            var targetMethod = domain.GetMethod(op.Operand2);
                            if (targetMethod == null)
                                break;
                            
                            int pCnt = targetMethod.ParameterCount;
                            if (targetMethod.HasThis && op.Code != OpCodeREnum.Newobj) pCnt++;
                            
                            bool hasConstrained = op.Code != OpCodeREnum.Callvirt_IL &&
                                op.Code != OpCodeREnum.Callvirt_CLR &&
                                op.Code != OpCodeREnum.Callvirt_Interface &&
                                op.Operand4 == 1;
                            int pushCnt = hasConstrained ? pCnt : Math.Max(pCnt - 3, 0);
                            int regCnt = pCnt - pushCnt;
                            
                            short[] srcRegs = new short[pCnt];
                            if (regCnt > 0) srcRegs[pCnt - regCnt] = op.Register2;
                            if (regCnt > 1) srcRegs[pCnt - regCnt + 1] = op.Register3;
                            if (regCnt > 2) srcRegs[pCnt - regCnt + 2] = op.Register4;
                            
                            int foundPushes = 0;
                            int scanIdx = i - 1;
                            while (scanIdx >= 0 && foundPushes < pushCnt)
                            {
                                if (body[scanIdx].Code == OpCodeREnum.Push)
                                {
                                    srcRegs[pushCnt - 1 - foundPushes] = body[scanIdx].Register1;
                                    // 彻底删除该指令，避免解释器 Nop 带来的 Dispatch 开销
                                    for (int j = scanIdx; j < body.Length - 1; j++)
                                    {
                                        body[j] = body[j + 1];
                                    }
                                    Array.Resize(ref body, body.Length - 1);
                                    FixBranchTargetsAfterRemove(body, scanIdx, frame.SwitchTargets, frame.Symbols);
                                    // 因为当前指令(Call)的位置前移了，我们需要更新外层循环的 i 和当前 op
                                    i--;
                                    op = body[i];
                                    foundPushes++;
                                    // 不减少 scanIdx，因为后面的指令已经补上来了，当前 scanIdx 就是前一条指令
                                    continue;
                                }
                                scanIdx--;
                            }
                            
                            if (foundPushes != pushCnt)
                                throw new Exception("Neo lowering could not find expected Push instructions for Call/Newobj.");
                            
                             StackSlotInfo[] paramInfos = null;
                            if (targetMethod is ILRuntime.CLR.Method.ILMethod ilm)
                            {
                                paramInfos = ilm.CompiledFrame.ParamInfos;
                                // Step 11: an interface (abstract) declared method has no
                                // compiled parameter layout, so synthesize a contiguous
                                // callee param layout from the declared signature. The
                                // concrete impl shares this signature, so the argument
                                // copy is valid.
                                if (paramInfos == null)
                                    paramInfos = AllocNeoParamInfosFromSignature(targetMethod, op.Code == OpCodeREnum.Newobj, domain);
                            }
                            else if (targetMethod is ILRuntime.CLR.Method.CLRMethod clrMethod)
                            {
                                // Generate contiguous paramInfos for CLRMethod
                                int totalParams = pCnt + (op.Code == OpCodeREnum.Newobj ? 1 : 0);
                                paramInfos = new StackSlotInfo[totalParams];
                                int curPrim = 0, curRef = 0;
                                if (op.Code == OpCodeREnum.Newobj)
                                {
                                    paramInfos[0] = new StackSlotInfo { Offset = curPrim, Size = 4, RefOffset = curRef, RefCount = 1 };
                                    curPrim += 4;
                                    curRef += 1;
                                }
                                for (int p = 0; p < pCnt; p++)
                                {
                                    int dstIndex = (op.Code == OpCodeREnum.Newobj) ? p + 1 : p;
                                    CLR.TypeSystem.IType paramType;
                                    if (targetMethod.HasThis && op.Code != OpCodeREnum.Newobj && p == 0)
                                        paramType = targetMethod.DeclearingType;
                                    else
                                        paramType = clrMethod.Parameters[p - ((targetMethod.HasThis && op.Code != OpCodeREnum.Newobj) ? 1 : 0)];

                                    // Step 13b (D1): ALL parameter types -- including CLR value
                                    // types -- flow through the unified AllocateNeoCallParamSlot
                                    // callee-layout helper. The previous caller-temp-slot fallback
                                    // (which overrode the callee layout with the caller source
                                    // register's shape for CLR structs) is REMOVED: it miscopied a
                                    // boxed-ref CLR struct local's 4-byte mStack index into the
                                    // param region (K2). The callee layout already sizes a CLR struct
                                    // via GetPrimitiveSize (AllocateNeoCallParamSlot's IsValueType
                                    // branch), so primitives/enums/IL-VTs/refs are byte-identical and
                                    // only the CLR-struct branch changes.
                                    //
                                    // Step 13 Area 4c: a CLR-method byref param (ref/out/in) arrives
                                    // as an 8-byte Ref Slot in the source register. The readers
                                    // (CLRMethod.Invoke / the autogen wrapper) only see the callee
                                    // param region (targetBase), NOT the caller frameBase, so they
                                    // CANNOT dereference the byref (the area4b finding). Instead size
                                    // the dest slot by the ELEMENT type (de-byref) and flag the slot
                                    // so CopyNeoCallArguments derefs the byref at the copy site (the
                                    // 4b PrimitiveByRefSrc pattern, extended to params + the mStack-
                                    // object sub-case). The reader then reads flat bytes exactly
                                    // like a by-value param of the element type. A by-value param is
                                    // unaffected (its type is not IsByRef -> element == itself).
                                    if (paramType != null && paramType.IsByRef)
                                    {
                                        var elemType = paramType.ElementType;
                                        paramInfos[dstIndex] = AllocateNeoCallParamSlot(elemType, ref curPrim, ref curRef, domain);
                                    }
                                    else
                                        paramInfos[dstIndex] = AllocateNeoCallParamSlot(paramType, ref curPrim, ref curRef, domain);
                                }
                            }

                            if (paramInfos != null)
                            {
                                List<ushort> primSrc = new List<ushort>();
                                List<ushort> primDst = new List<ushort>();
                                List<ushort> primSize = new List<ushort>();
                                List<ushort> refSrc = new List<ushort>();
                                List<ushort> refDst = new List<ushort>();
                                List<bool> primByRef = new List<bool>();
                                List<bool> primByRefWriteBack = new List<bool>();
                                List<System.Type> primByRefElemType = new List<System.Type>();
                                // Step 13 Area 4c: the CLR ParameterInfo[] for the
                                // IsIn/IsOut write-back gate. Null for an IL callee
                                // (the byref-param flag only fires for CLR callees;
                                // an IL callee's byref params keep the 8-byte Ref Slot
                                // in the callee region, read by ExecuteNeo as a byref
                                // local -- the Step 17 path, byte-identical).
                                System.Reflection.ParameterInfo[] clrParams = (targetMethod is ILRuntime.CLR.Method.CLRMethod) ? ((ILRuntime.CLR.Method.CLRMethod)targetMethod).ParametersCLR : null;

                                for (int p = 0; p < pCnt; p++)
                                {
                                    var srcInfo = localInfos[srcRegs[p]];
                                    // For Newobj, the ILMethod paramInfos[0] is 'this', so we need to offset the dstInfo by 1
                                    int dstIndex = (op.Code == OpCodeREnum.Newobj) ? p + 1 : p;
                                    var dstInfo = paramInfos[dstIndex];

                                    // Step 13 Area 4b: a CLR value-type instance `this`
                                    // arrives as a frame-native byref (the C# compiler
                                    // lowers `local.VTMethod()` and `new VT(args)` to
                                    // `ldloca; call`, so srcRegs[0] holds the byref
                                    // temp). The dest slot is sized as the struct's flat
                                    // bytes (AllocateNeoCallParamSlot's IsValueType
                                    // branch), so the copy must DEREFERENCE the byref
                                    // and copy the struct bytes -- flag the slot so
                                    // CopyNeoCallArguments derefs instead of copying
                                    // the byref verbatim. (A by-value VT PARAM also
                                    // has a flat-bytes dest but its source is the
                                    // struct local directly, not a byref -- no flag.)
                                    bool dstIsVtThisSlot = (targetMethod.HasThis && op.Code != OpCodeREnum.Newobj && p == 0)
                                        && targetMethod.DeclearingType != null
                                        && targetMethod.DeclearingType.IsValueType
                                        && !(targetMethod.DeclearingType.IsPrimitive
                                             || (targetMethod.DeclearingType.TypeForCLR != null
                                                 && targetMethod.DeclearingType.TypeForCLR.IsEnum));

                                    // Step 13 Area 4c: a CLR-method byref PARAM. The
                                    // param's source register holds an 8-byte Ref Slot
                                    // (frame-native or mStack-object field). Flag it so
                                    // CopyNeoCallArguments derefs at the copy site (the
                                    // dest is sized by the element type per the sizing
                                    // loop above). `dstIsVtThisSlot` (4b) is the
                                    // frame-native-only subset; the param case adds the
                                    // mStack-object sub-case (handled at runtime by the
                                    // objIdx discriminator in CopyNeoCallArguments).
                                    int paramLogical = p - ((targetMethod.HasThis && op.Code != OpCodeREnum.Newobj) ? 1 : 0);
                                    bool dstIsByRefParam = false;
                                    bool byRefWriteBack = false;
                                    System.Type byRefElemType = null;
                                    if (!dstIsVtThisSlot && clrParams != null && paramLogical >= 0 && paramLogical < clrParams.Length)
                                    {
                                        var pinfo = clrParams[paramLogical];
                                        if (pinfo.ParameterType.IsByRef)
                                        {
                                            dstIsByRefParam = true;
                                            // D5: write back for ref/out, NOT for in-only.
                                            // ref = neither IsIn-only nor IsOut; out = IsOut;
                                            // in = IsIn && !IsOut.
                                            byRefWriteBack = !pinfo.IsIn || pinfo.IsOut;
                                            // The element CLR Type, for the mStack-object
                                            // field deref (a `ref obj.field` shape). Null
                                            // when the byref is frame-native (the byte
                                            // width alone suffices there). Source it from
                                            // the CLR ParameterInfo (de-byref'd element).
                                            var elemClr = pinfo.ParameterType.GetElementType();
                                            byRefElemType = elemClr;
                                        }
                                    }
                                    bool dstByRef = dstIsVtThisSlot || dstIsByRefParam;

                                    if (dstInfo.Size > 0)
                                    {
                                        primSrc.Add((ushort)srcInfo.Offset);
                                        primDst.Add((ushort)dstInfo.Offset);
                                        primSize.Add((ushort)dstInfo.Size);
                                        primByRef.Add(dstByRef);
                                        // 4b VT `this` always writes back (a ctor / mutating
                                        // instance method); 4c keys on the ref/out gate.
                                        primByRefWriteBack.Add(dstIsVtThisSlot || byRefWriteBack);
                                        primByRefElemType.Add(byRefElemType);
                                    }
                                    for (int r = 0; r < dstInfo.RefCount; r++)
                                    {
                                        refSrc.Add((ushort)(srcInfo.RefOffset + r));
                                        refDst.Add((ushort)(dstInfo.RefOffset + r));
                                    }
                                }

                                NeoCallParamMap map = new NeoCallParamMap();
                                if (primSrc.Count > 0)
                                {
                                    map.PrimitiveSrc = primSrc.ToArray();
                                    map.PrimitiveDst = primDst.ToArray();
                                    map.PrimitiveSize = primSize.ToArray();
                                    map.PrimitiveByRefSrc = primByRef.ToArray();
                                    map.PrimitiveByRefWriteBack = primByRefWriteBack.ToArray();
                                    map.PrimitiveByRefElemType = primByRefElemType.ToArray();
                                }
                                if (refSrc.Count > 0)
                                {
                                    map.RefSrc = refSrc.ToArray();
                                    map.RefDst = refDst.ToArray();
                                }
                                
                                op.Operand = callParams.Count;
                                callParams.Add(map);
                            }
                            
                            if (op.Code == OpCodeREnum.Newobj)
                            {
                                short r1 = op.Register1; // Destination
                                int off1 = localInfos[r1].Offset;
                                int ref1 = localInfos[r1].RefOffset;
                                op.DstOffset = (ushort)off1;
                                op.Operand3 = ref1;
                            }
                            else
                            {
                                if (op.Register1 >= 0)
                                {
                                    short r1 = op.Register1;
                                    op.Operand3 = localInfos[r1].RefOffset;
                                    LowerR1(ref op, localInfos);
                                }
                            }
                        }
                        break;
                    default:
                        handled = false;
                        break;
                }
                WarnUnhandledNeoLoweringOpcode(op.Code, handled);

                // B1: maintain the live-range-aware alias snapshot for the next
                // iteration's _Inline / Initobj consumers. An address producer
                // establishes its dest's alias (mirroring the alias-build pass);
                // any other opcode that redefines a register drops that
                // register's alias (its prior address live range is over). This
                // is what makes a REUSED register fold each consumer to the base
                // of its OWN live range instead of the static map's last write.
                if (liveAliasMap != null)
                {
                    if (op.Code == OpCodeREnum.Ldloca || op.Code == OpCodeREnum.Ldloca_S
                        || op.Code == OpCodeREnum.Ldarga || op.Code == OpCodeREnum.Ldarga_S)
                    {
                        short aDest = preOp.Register1;
                        short aSrc = preOp.Register2;
                        // Only alias value-type sources; a reference local
                        // produces a genuine pointer (ref/fixed/byref use)
                        // consumed by the heap path, not the in-frame-VT inline
                        // path (same guard as the alias-build pass).
                        bool aSrcIsRef = localIsRef != null && aSrc >= 0
                            && aSrc < localIsRef.Length && localIsRef[aSrc];
                        if (aSrc >= 0 && aSrc < localInfos.Length && !aSrcIsRef)
                        {
                            liveAliasMap[aDest] = liveAliasMap.TryGetValue(aSrc, out NeoAddressAlias inh)
                                ? new NeoAddressAlias { Reg = inh.Reg, Offset = inh.Offset }
                                : new NeoAddressAlias { Reg = aSrc, Offset = 0 };
                        }
                        else
                            liveAliasMap.Remove(aDest);
                    }
                    else if (op.Code == OpCodeREnum.Ldflda)
                    {
                        short aDest = preOp.Register1;
                        short aSrc = preOp.Register2;
                        if (liveAliasMap.TryGetValue(aSrc, out NeoAddressAlias inh))
                            liveAliasMap[aDest] = new NeoAddressAlias { Reg = inh.Reg, Offset = inh.Offset + op.Operand2 };
                        else
                            liveAliasMap.Remove(aDest);
                    }
                    else if (GetOpcodeDestRegister(ref preOp, out short killed))
                    {
                        liveAliasMap.Remove(killed);
                    }
                }

                body[i] = op;
            }
            frame.NeoExecuteBody = body;
            if (callParams.Count > 0)
            {
                frame.NeoCallParams = callParams.ToArray();
            }
        }

        static void LowerR1(ref OpCodeR op, StackSlotInfo[] localInfos)
        {
            short r1 = op.Register1;
            // A void method's `ret` (JIT Code.Ret leaves Register1 at its default
            // 0 when hasReturn==false) reaches here with r1 pointing past the
            // (possibly empty) localInfos -- a phantom register that holds no
            // value. ExecuteNeo only reads DstOffset when there IS a return value,
            // so an out-of-range index resolves to offset 0 (harmless). Mirrors the
            // defensive `reg >= 0 && reg < localInfos.Length` pattern already used
            // by the comparison/lower sites below. Fixes the empty static .cctor
            // IndexOutOfRangeException (NeoStep24CliProbe..cctor).
            int off1 = (r1 >= 0 && r1 < localInfos.Length) ? localInfos[r1].Offset : 0;
            op.DstOffset = (ushort)off1;
        }

        static StackSlotInfo AllocateNeoCallParamSlot(CLR.TypeSystem.IType type, ref int offset, ref int refOffset, Enviorment.AppDomain domain)
        {
            StackSlotInfo slot = default;
            // NOTE (Step 12): this builds the callee-side param layout for CLR
            // method calls and for interface-signature synthesis. The autogen
            // CLR binding redirects read params SEQUENTIALLY via ReadNeo*
            // (curPrim advanced by each param's exact size, no alignment), so
            // this layout MUST stay contiguous (offset += size) to match them.
            // Natural alignment is applied only to the IL frame's own
            // locals/temps/params (AllocateLocalStackSpaces in JITCompiler),
            // not here -- applying it here regresses CLR small-primitive calls.
            slot.Offset = offset;
            slot.RefOffset = refOffset;

            // Step 17: a byref param is an 8-byte Ref Slot (contiguous, no
            // per-param alignment, to keep the autogen ReadNeo* reader layout
            // matched per this helper's NOTE). TypeForCLR strips the byref
            // modifier, so detect IsByRef FIRST.
            if (type != null && type.IsByRef)
            {
                slot.Size = 8;
                slot.RefCount = 0;
                offset += 8;
                return slot;
            }

            if (type.IsPrimitive || (type.TypeForCLR != null && type.TypeForCLR.IsEnum))
            {
                slot.Size = domain.GetPrimitiveSize(type);
                offset += slot.Size;
            }
            else if (type is CLR.TypeSystem.ILType il && type.IsValueType)
            {
                slot.Size = il.TotalPrimitiveSize;
                slot.RefCount = il.TotalReferenceCount;
                offset += slot.Size;
                refOffset += slot.RefCount;
            }
            else if (type.IsValueType)
            {
                // Step 13b (D1): a CLR struct param/return slot is flat bytes sized
                // by the managed value-type byte size (Unsafe.SizeOf<T>). This NEVER
                // throws (unlike GetPrimitiveSize, which only knew the primitive
                // singletons), so an unsupported CLR struct param (e.g. an async
                // state-machine builder) no longer crashes JIT prewarm. The
                // reader/writer (CLRMethod.Invoke / AppendArgumentCodeNeo /
                // ReadNeoValueType / WriteNeoValueType) MUST use the SAME managed
                // size so the reader layout and this callee layout stay
                // byte-consistent -- see GetNeoValueTypeManagedSize. For a struct
                // WITH a registered ValueTypeBinder the binder maps the ref fields
                // (its managed count), so RefCount reflects the binder; the
                // primitive bytes are still the flat managed size.
                slot.Size = GetNeoValueTypeManagedSize(type.TypeForCLR);
                if (type is CLR.TypeSystem.CLRType crt && crt.ValueTypeBinder != null)
                {
                    crt.GetValueTypeSize(out _, out int managedCount);
                    slot.RefCount = managedCount;
                    refOffset += managedCount;
                }
                offset += slot.Size;
            }
            else
            {
                slot.Size = 4;
                slot.RefCount = 1;
                offset += 4;
                refOffset++;
            }

            return slot;
        }

        // Step 13b (D1/D4): the managed byte size of a CLR value type, used to size
        // its flat-bytes param/return slot in the callee param region. Computed via
        // the generic Unsafe.SizeOf<T>() (the GC-reference-aware managed size -- NOT
        // the unmanaged Marshal size), cached per Type. Never throws: works for any
        // struct (blittable, with ref fields, async builders, TaskAwaiter, ...), so
        // JIT prewarm of a method that takes an unsupported CLR struct param does not
        // crash. The reader/writer helpers (ReadNeoValueType / WriteNeoValueType and
        // the autogen AppendArgumentCodeNeo path) MUST use the same size for a given
        // Type so the reader layout and this callee layout stay byte-consistent.
        static readonly ConcurrentDictionary<Type, int> s_neoVtSizeCache = new ConcurrentDictionary<Type, int>();
        static MethodInfo s_unsafeSizeOfGeneric;
        // public: the autogen CLR binding code (compiled into the HOST assembly)
        // calls this to size a CLR struct param/return slot so it stays
        // byte-consistent with the optimizer's callee layout (single size source).
        public static int GetNeoValueTypeManagedSize(Type t)
        {
            if (t == null)
                return 0;
            return s_neoVtSizeCache.GetOrAdd(t, type =>
            {
                if (type.IsEnum)
                    type = Enum.GetUnderlyingType(type);
                if (s_unsafeSizeOfGeneric == null)
                {
                    // Unsafe.SizeOf<T>() -> a generic method; instantiate per Type.
                    s_unsafeSizeOfGeneric = typeof(Unsafe).GetMethod(
                        "SizeOf", BindingFlags.Public | BindingFlags.Static);
                }
                if (s_unsafeSizeOfGeneric != null)
                {
                    var inst = s_unsafeSizeOfGeneric.MakeGenericMethod(type);
                    return (int)inst.Invoke(null, null);
                }
                // Fallback: unmanaged marshalled size (blittable structs only).
                return System.Runtime.InteropServices.Marshal.SizeOf(type);
            });
        }


        // Step 12: resolve a register that may be the dest of an ldloca/ldloca.s
        // or ldflda addressing an in-frame value type back to the underlying
        // local register plus the accumulated nested-field byte offset. Returns
        // { reg, Offset = 0 } when the register is not an alias (a direct
        // local/parameter/temp).
        struct NeoAddressAlias
        {
            public short Reg;
            public int Offset;
        }

        static NeoAddressAlias ResolveAddressAlias(Dictionary<short, NeoAddressAlias> addrAlias, short reg)
        {
            if (addrAlias != null && addrAlias.TryGetValue(reg, out NeoAddressAlias resolved))
                return resolved;
            return new NeoAddressAlias { Reg = reg, Offset = 0 };
        }

        // Step 11: build a contiguous callee param-info layout purely from a
        // method's declared signature (used when the target has no compiled
        // frame, e.g. an interface abstract method). Mirrors the CLRMethod
        // branch: slot 0 is `this` for HasThis callvirt, then each parameter.
        static StackSlotInfo[] AllocNeoParamInfosFromSignature(CLR.Method.IMethod targetMethod, bool isNewobj, Enviorment.AppDomain domain)
        {
            int pCnt = targetMethod.ParameterCount;
            bool hasThis = targetMethod.HasThis && !isNewobj;
            int total = pCnt + ((hasThis || isNewobj) ? 1 : 0);
            StackSlotInfo[] paramInfos = new StackSlotInfo[total];
            int curPrim = 0, curRef = 0;
            if (isNewobj)
            {
                paramInfos[0] = new StackSlotInfo { Offset = curPrim, Size = 4, RefOffset = curRef, RefCount = 1 };
                curPrim += 4;
                curRef += 1;
            }
            if (hasThis)
            {
                paramInfos[0] = AllocateNeoCallParamSlot(targetMethod.DeclearingType, ref curPrim, ref curRef, domain);
            }
            for (int p = 0; p < pCnt; p++)
            {
                int dstIndex = (hasThis || isNewobj) ? p + 1 : p;
                var paramType = targetMethod.Parameters[p];
                paramInfos[dstIndex] = AllocateNeoCallParamSlot(paramType, ref curPrim, ref curRef, domain);
            }
            return paramInfos;
        }

        static void FixBranchTargetsAfterRemove(OpCodeR[] body, int removedIndex, Dictionary<int, int[]> jumpTables, Dictionary<int, RegisterVMSymbol> symbols)
        {
            for (int i = 0; i < body.Length; i++)
            {
                var op = body[i];
                if (IsBranching(op.Code))
                {
                    if (op.Operand > removedIndex)
                    {
                        op.Operand--;
                        body[i] = op;
                    }
                }
                else if (IsIntermediateBranching(op.Code))
                {
                    if (op.Operand4 > removedIndex)
                    {
                        op.Operand4--;
                        body[i] = op;
                    }
                }
                else if (op.Code == OpCodeREnum.Switch && jumpTables != null && jumpTables.TryGetValue(op.Operand, out var targets))
                {
                    for (int j = 0; j < targets.Length; j++)
                    {
                        if (targets[j] > removedIndex)
                            targets[j]--;
                    }
                }
            }

            if (symbols != null && symbols.Count > 0)
            {
                var oldSymbols = new List<KeyValuePair<int, RegisterVMSymbol>>(symbols);
                symbols.Clear();
                foreach (var item in oldSymbols)
                {
                    if (item.Key < removedIndex)
                    {
                        symbols[item.Key] = item.Value;
                    }
                    else if (item.Key > removedIndex)
                    {
                        symbols[item.Key - 1] = item.Value;
                    }
                }
            }
        }

        static void LowerR1R2(ref OpCodeR op, StackSlotInfo[] localInfos)
        {
            short r1 = op.Register1;
            short r2 = op.Register2;
            int off1 = localInfos[r1].Offset;
            int off2 = localInfos[r2].Offset;
            op.DstOffset = (ushort)off1;
            op.SrcOffset = (ushort)off2;
        }

        static void LowerR1R2R3(ref OpCodeR op, StackSlotInfo[] localInfos)
        {
            short r1 = op.Register1;
            short r2 = op.Register2;
            short r3 = op.Register3;
            int off1 = localInfos[r1].Offset;
            int off2 = localInfos[r2].Offset;
            int off3 = localInfos[r3].Offset;
            op.DstOffset = (ushort)off1;
            op.SrcOffset = (ushort)off2;
            op.OperandOffset = (ushort)off3;
        }

        static void WarnUnhandledNeoLoweringOpcode(OpCodeREnum code, bool handled)
        {
            // Do not throw during incremental steps because Prewarm compiles the whole assembly
        }
    }
}
#endif
