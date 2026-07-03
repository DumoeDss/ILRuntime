using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ILRuntime.Mono.Cecil;
using ILRuntime.Mono.Cecil.Cil;

using ILRuntime.CLR.TypeSystem;
using ILRuntime.CLR.Method;
using ILRuntime.Runtime.Intepreter.OpCodes;

namespace ILRuntime.Runtime.Intepreter.RegisterVM
{
    partial class Optimizer
    {
        public static int CleanupRegister(List<OpCodeR> ins, int locRegStart, bool hasReturn, short protectedReg, out short protectedRegFinalIndex)
        {
            protectedRegFinalIndex = protectedReg;
            short maxRegNum = (short)locRegStart;
            HashSet<short> usedRegisters = new HashSet<short>();
            //arguments can not be cleaned
            for (short i = 0; i < locRegStart; i++)
                usedRegisters.Add(i);
            // Neo exception handling (Step 14): the catch handler's exception
            // variable occupies temp register 0 (baseRegStart = locRegStart +
            // varCnt). It is never explicitly referenced by any opcode (the
            // caught object is placed there by the runtime, not by the IR), so
            // without protection CleanupRegister would compact it away and the
            // Neo catch-entry write would have nowhere to store the exception.
            // Protect it so a StackSlotInfo is reserved for it. Legacy's flat
            // register array always has this slot, so Legacy passes -1.
            if (protectedReg >= 0)
            {
                usedRegisters.Add(protectedReg);
                if (protectedReg > maxRegNum)
                    maxRegNum = protectedReg;
            }
            for (int i = 0; i < ins.Count; i++)
            {
                var X = ins[i];
                short xSrc, xSrc2, xSrc3, xDst;
                if(GetOpcodeSourceRegister(ref X, hasReturn, out xSrc, out xSrc2, out xSrc3))
                {
                    if (xSrc >= 0)
                    {
                        if (xSrc > maxRegNum)
                            maxRegNum = xSrc;
                        usedRegisters.Add(xSrc);
                    }
                    if (xSrc2 >= 0)
                    {
                        if (xSrc2 > maxRegNum)
                            maxRegNum = xSrc2;
                        usedRegisters.Add(xSrc2);
                    }
                    if (xSrc3 >= 0)
                    {
                        if (xSrc3 > maxRegNum)
                            maxRegNum = xSrc3;
                        usedRegisters.Add(xSrc3);
                    }
                }

                if(GetOpcodeDestRegister(ref X, out xDst))
                {
                    if (xDst >= 0)
                    {
                        if (xDst > maxRegNum)
                            maxRegNum = xDst;
                        usedRegisters.Add(xDst);
                    }
                }
            }

            List<short> unusedRegisters = new List<short>();
            for(short i = 0; i <= maxRegNum; i++)
            {
                if (!usedRegisters.Contains(i))
                    unusedRegisters.Add(i);
            }

            for(short i = 0; i < unusedRegisters.Count; i++)
            {
                short r = (short)(unusedRegisters[i] - i);
                for (int j = 0; j < ins.Count; j++)
                {
                    var X = ins[j];
                    short xSrc, xSrc2, xSrc3, xDst;
                    bool replaced = false;

                    if (GetOpcodeSourceRegister(ref X, hasReturn, out xSrc, out xSrc2, out xSrc3))
                    {
                        if (xSrc > r)
                        {
                            ReplaceOpcodeSource(ref X, 0, (short)(xSrc - 1));
                            replaced = true;
                        }
                        if (xSrc2 > r)
                        {
                            ReplaceOpcodeSource(ref X, 1, (short)(xSrc2 - 1));
                            replaced = true;
                        }
                        if (xSrc3 > r)
                        {
                            ReplaceOpcodeSource(ref X, 2, (short)(xSrc3 - 1));
                            replaced = true;
                        }
                    }

                    if (GetOpcodeDestRegister(ref X, out xDst))
                    {
                        if (xDst > r)
                        {
                            ReplaceOpcodeDest(ref X, (short)(xDst - 1));
                            replaced = true;
                        }
                    }

                    if (replaced)
                        ins[j] = X;
                }
            }

            // The protected register (Neo catch exception var) is itself never
            // removed, but it shifts down by the number of unused registers
            // originally below it. Compute its final (post-compaction) index so
            // AllocateLocalStackSpaces can reserve its StackSlotInfo at the
            // exact index the catch-handler body references it by.
            if (protectedReg >= 0)
            {
                int shift = 0;
                for (int i = 0; i < unusedRegisters.Count; i++)
                {
                    if (unusedRegisters[i] < protectedReg)
                        shift++;
                }
                protectedRegFinalIndex = (short)(protectedReg - shift);
            }

            return maxRegNum - unusedRegisters.Count + 1;
        }
    }
}
