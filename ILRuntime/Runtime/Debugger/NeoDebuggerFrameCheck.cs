#if ENABLE_NEO_MODE && DEBUG
using System;
using System.Collections.Generic;

using ILRuntime.CLR.Method;
using ILRuntime.CLR.TypeSystem;
using ILRuntime.Runtime.Intepreter;

namespace ILRuntime.Runtime.Debugger
{
    /// <summary>
    /// neo-debugger-neo-frame capstone (host-side, DEBUG+Neo only) -- the
    /// correctness gate for Neo debugger variable inspection.
    ///
    /// The exercise point is the UNHANDLED-EXCEPTION path: an IL method running
    /// under ExecuteNeo throws an exception no handler catches -> ExecuteNeo's
    /// unwind constructs an ILRuntimeException (ILIntepreter.Neo.cs:4543) whose
    /// ctor calls DebugService.GetThisInfo / GetLocalVariableInfo
    /// (ILRuntimeException.cs:36-40), stashing .ThisInfo / .LocalInfo. This check
    /// drives that path with a probe that has primitive + reference locals and
    /// asserts the stashed strings carry the CORRECT live values (NOT "not
    /// supported yet"). An ADVERSARIAL probe MUTATES a local before the throw to
    /// prove the inspection reads the live frame, not a stale/default value.
    ///
    /// Mirrors the NeoStep25LoadExecCheck host-side-self-check shape (a static
    /// Run(AppDomain) returning a Pass/Fail tally, invoked via a CLI special
    /// mode "NeoDebuggerFrame"). Binding evidence the GetThisInfo /
    /// GetLocalVariableInfo Neo arms are load-bearing: on HEAD (guards return
    /// "not supported yet") the value assertions MISS -> FAIL; with the Neo arms
    /// -> PASS.
    /// </summary>
    public static class NeoDebuggerFrameCheck
    {
        public class Result
        {
            public int TotalCells;
            public int Passed;
            public int Failed;
            public List<string> Failures = new List<string>();
        }

        const string ProbeFullName = "TestCases.NeoDebuggerFrameProbe";

        public static Result Run(ILRuntime.Runtime.Enviorment.AppDomain appdomain)
        {
            var res = new Result();

            // ---- locate the probe type ----
            ILType probeType = null;
            if (!appdomain.LoadedTypes.TryGetValue(ProbeFullName, out var probeIType) || !(probeIType is ILType pt))
            {
                res.Failures.Add(ProbeFullName + " not loaded / not an ILType");
                res.TotalCells = 1; res.Failed = 1;
                return res;
            }
            probeType = pt;

            // ===== Cell 1: ProbeThrow -- primitive + reference locals inspect =====
            // The probe assigns prim=12345, msg="local-string-A", then throws
            // unhandled -> ILRuntimeException.LocalInfo MUST contain both values.
            RunValueCell(res, appdomain, probeType, "ProbeThrow",
                new Expected[] {
                    new Expected("prim local", "12345"),
                    new Expected("reference local msg", "local-string-A"),
                },
                /*thisExpect*/ new Expected("FieldInt", "7777"));

            // ===== Cell 2: ADVERSARIAL ProbeMutateThenThrow -- the inspection
            // reads the LIVE frame. prim is assigned 11111 then MUTATED to 99999;
            // LocalInfo MUST contain the MUTATED value (99999), NOT 11111. A
            // stale/default read (or a no-op guard) would surface 11111 or empty. =====
            RunValueCell(res, appdomain, probeType, "ProbeMutateThenThrow",
                new Expected[] {
                    new Expected("MUTATED prim local (live read)", "99999"),
                    new Expected("reference local msg", "mutated-string-B"),
                },
                /*thisExpect*/ new Expected("FieldInt", "7777"));

            // ===== Cell 3: ProbeMixedWidths -- a 8-byte (long) primitive local +
            // a reference local, asserting the wide primitive read path. The long
            // value 0x123456789ABCDEF0 renders as 1311768467463790320 in decimal. =====
            RunValueCell(res, appdomain, probeType, "ProbeMixedWidths",
                new Expected[] {
                    new Expected("long (8-byte prim) local", "1311768467463790320"),
                    new Expected("reference local tag", "wide-probe-C"),
                },
                /*thisExpect*/ new Expected("FieldInt", "7777"));

            // ===== Cell 4 (neo-debugger-ilvt-local): IL-value-type LOCAL
            // reconstruction. ProbeVtLocal has a `VtLocal vt` in-frame struct
            // (int X + string S). The reconstructed LocalInfo MUST render the
            // struct's fields from the split primitive+reference sub-regions:
            // X = VT_X (4242, the primitive sub-region) and S = VT_S ("vt-field-A",
            // the reference sub-region). On HEAD (placeholder) neither value
            // appears (the local renders "<IL value-type local: reconstruction
            // deferred>") -- so both assertions MISS -> FAIL; with the fix -> PASS.
            // This is the load-bearing stash-toggle gate for this child. =====
            RunValueCell(res, appdomain, probeType, "ProbeVtLocal",
                new Expected[] {
                    new Expected("IL-VT local primitive field X", "4242"),
                    new Expected("IL-VT local reference field S", "vt-field-A"),
                },
                /*thisExpect*/ new Expected("FieldInt", "7777"));

            // ===== Cell 5 (neo-debugger-ilvt-local): ADVERSARIAL IL-VT local.
            // ProbeVtLocalMutate assigns vt.X = 1111 then MUTATES it to 8888. The
            // reconstruction MUST read the LIVE frame bytes (8888), NOT the stale
            // 1111 -- proving the field-walk reads the live primitive sub-region,
            // not a default/cached value. =====
            RunValueCell(res, appdomain, probeType, "ProbeVtLocalMutate",
                new Expected[] {
                    new Expected("MUTATED IL-VT local primitive field X (live read)", "8888"),
                    new Expected("IL-VT local reference field S", "vt-field-B"),
                },
                /*thisExpect*/ new Expected("FieldInt", "7777"));

            // ===== Cell 6: load-bearing gate -- assert LocalInfo is NOT the
            // HEAD refusal string ("not supported yet"). A green value cell above
            // already implies this (the expected values are absent from the refusal
            // string), but this cell makes the guard-vs-arm distinction explicit
            // (a future regression that re-introduces the guard trips here even if
            // the value cells are skipped). =====
            res.TotalCells++;
            {
                string diff = null;
                try
                {
                    var inst = appdomain.Instantiate(ProbeFullName);
                    var m = probeType.GetMethod("ProbeThrow", 0);
                    object thrown = null;
                    try { appdomain.Invoke(m, inst); }
                    catch (ILRuntimeException ex) { thrown = ex; }
                    catch (Exception ex) { thrown = ex; }
                    if (thrown is ILRuntimeException ilre)
                    {
                        if (ilre.LocalInfo == null || ilre.LocalInfo.Contains("not supported"))
                            diff = "LocalInfo is the HEAD refusal string (Neo arm not active): " + (ilre.LocalInfo ?? "<null>");
                    }
                    else
                    {
                        diff = "expected ILRuntimeException, got " + (thrown == null ? "<null>" : thrown.GetType().Name);
                    }
                }
                catch (Exception ex) { diff = "cell threw " + ex.GetType().Name + ": " + ex.Message; }
                RecordCell(res, "LocalInfo is NOT the HEAD refusal string (Neo arm active)", diff);
            }

            return res;
        }

        // Drive one probe method, catch the resulting ILRuntimeException, assert
        // .LocalInfo contains each expected local value AND .ThisInfo contains the
        // expected field value. localExpects: the values the frame locals hold at
        // the throw point; thisExpect: an IL instance field value GetThisInfo
        // renders via the F-4 indexer.
        static void RunValueCell(Result res, ILRuntime.Runtime.Enviorment.AppDomain appdomain,
            ILType probeType, string methodName, Expected[] localExpects, Expected thisExpect)
        {
            res.TotalCells++;
            string diff = null;
            try
            {
                var inst = appdomain.Instantiate(ProbeFullName);
                var m = probeType.GetMethod(methodName, 0);
                if (m == null) { diff = "method " + methodName + " not found"; RecordCell(res, methodName + " frame inspection", diff); return; }
                ILRuntimeException caught = null;
                try { appdomain.Invoke(m, inst); }
                catch (ILRuntimeException ex) { caught = ex; }
                catch (Exception ex)
                {
                    // If a non-ILRuntimeException surfaces, the unwind path did not
                    // wrap it (e.g. debugger attached). Surface it for diagnosis.
                    diff = methodName + ": expected ILRuntimeException, got " + ex.GetType().Name + ": " + ex.Message;
                    RecordCell(res, methodName + " frame inspection", diff);
                    return;
                }
                if (caught == null) { diff = methodName + ": probe did not throw"; RecordCell(res, methodName + " frame inspection", diff); return; }

                var sb = new System.Text.StringBuilder();
                string li = caught.LocalInfo ?? "";
                string ti = caught.ThisInfo ?? "";

                // LocalInfo must NOT be the refusal string.
                if (li.Contains("not supported"))
                    sb.Append("LocalInfo=refusal; ");

                foreach (var e in localExpects)
                {
                    if (!li.Contains(e.Value))
                        sb.Append("LocalInfo missing ").Append(e.Label).Append("='").Append(e.Value).Append("' (li=[").Append(li).Append("]); ");
                }

                // ThisInfo must contain the field value (exercises the F-4 indexer
                // reuse on the IL instance fields).
                if (!ti.Contains(thisExpect.Value))
                    sb.Append("ThisInfo missing ").Append(thisExpect.Label).Append("='").Append(thisExpect.Value).Append("' (ti=[").Append(ti).Append("]); ");

                diff = sb.Length == 0 ? null : sb.ToString();
            }
            catch (Exception ex)
            {
                diff = methodName + " cell threw " + ex.GetType().Name + ": " + ex.Message;
            }
            RecordCell(res, methodName + " frame inspection (locals + this)", diff);
        }

        static void RecordCell(Result res, string name, string diff)
        {
            if (diff == null)
            {
                res.Passed++;
                Console.WriteLine("[NeoDebuggerFrame] " + name + ": PASS");
            }
            else
            {
                res.Failed++; res.Failures.Add(name + ": " + diff);
                Console.WriteLine("  [FAIL] " + name + ": " + diff);
            }
        }

        struct Expected
        {
            public string Label;
            public string Value;
            public Expected(string label, string value) { Label = label; Value = value; }
        }
    }
}
#endif
