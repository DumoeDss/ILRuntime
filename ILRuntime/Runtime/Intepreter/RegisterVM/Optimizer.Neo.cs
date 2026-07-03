#if ENABLE_NEO_MODE
using ILRuntime.Runtime.Intepreter.OpCodes;
using System;
using System.Collections.Generic;
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

            for (int i = 0; i < body.Length; i++)
            {
                OpCodeR op = body[i];
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
                            short r1 = ResolveAddressAlias(addrAlias, op.Register1).Reg;
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
                    case OpCodeREnum.Ret:
                        if (op.Register1 >= 0)
                            LowerR1(ref op, localInfos);
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
                            NeoAddressAlias owner = ResolveAddressAlias(addrAlias, op.Register2);
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
                            NeoAddressAlias owner = ResolveAddressAlias(addrAlias, op.Register1);
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
                            NeoAddressAlias owner = ResolveAddressAlias(addrAlias, op.Register2);
                            op.Operand = localInfos[owner.Reg].RefOffset + op.Operand3; // source field abs ref index
                            op.Operand4 = localInfos[r1].RefOffset;                     // dest temp ref offset
                            op.DstOffset = (ushort)localInfos[r1].Offset;
                            op.SrcOffset = (ushort)(localInfos[owner.Reg].Offset + owner.Offset);
                        }
                        break;
                    case OpCodeREnum.Stfld_Ref_Inline:
                        {
                            NeoAddressAlias owner = ResolveAddressAlias(addrAlias, op.Register1);
                            short r2 = op.Register2; // value temp
                            op.Operand = localInfos[owner.Reg].RefOffset + op.Operand3; // dest field abs ref index
                            op.DstOffset = (ushort)(localInfos[owner.Reg].Offset + owner.Offset);
                            op.SrcOffset = (ushort)localInfos[r2].Offset;
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

                                    if (paramType.IsValueType && !paramType.IsPrimitive && !(paramType is CLR.TypeSystem.ILType) && !(paramType.TypeForCLR != null && paramType.TypeForCLR.IsEnum))
                                    {
                                        // TODO Step 13: replace this CLR struct fallback with a real CLR value-type ABI.
                                        // For now we keep the caller temp slot shape so unsupported CLR structs (for example TaskAwaiter)
                                        // do not fail during JIT prewarm. Reference/primitive/IL value-type parameters use exact callee layout below.
                                        var srcInfo = localInfos[srcRegs[p]];
                                        paramInfos[dstIndex] = new StackSlotInfo { Offset = curPrim, Size = srcInfo.Size, RefOffset = curRef, RefCount = srcInfo.RefCount };
                                        curPrim += srcInfo.Size;
                                        curRef += srcInfo.RefCount;
                                    }
                                    else
                                    {
                                        paramInfos[dstIndex] = AllocateNeoCallParamSlot(paramType, ref curPrim, ref curRef, domain);
                                    }
                                }
                            }

                            if (paramInfos != null)
                            {
                                List<ushort> primSrc = new List<ushort>();
                                List<ushort> primDst = new List<ushort>();
                                List<ushort> primSize = new List<ushort>();
                                List<ushort> refSrc = new List<ushort>();
                                List<ushort> refDst = new List<ushort>();
                                
                                for (int p = 0; p < pCnt; p++)
                                {
                                    var srcInfo = localInfos[srcRegs[p]];
                                    // For Newobj, the ILMethod paramInfos[0] is 'this', so we need to offset the dstInfo by 1
                                    int dstIndex = (op.Code == OpCodeREnum.Newobj) ? p + 1 : p;
                                    var dstInfo = paramInfos[dstIndex];
                                    
                                    if (dstInfo.Size > 0)
                                    {
                                        primSrc.Add((ushort)srcInfo.Offset);
                                        primDst.Add((ushort)dstInfo.Offset);
                                        primSize.Add((ushort)dstInfo.Size);
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
            int off1 = localInfos[r1].Offset;
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
                slot.Size = domain.GetPrimitiveSize(type);
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
