using System;

namespace TestCases
{
    // ===== neo-debugger-cli-protocol (child 13) probe =====
    //
    // A SMALL dedicated probe for the DAP adapter self-check
    // (ILRuntime/Runtime/Debugger/NeoDebuggerDapCheck.cs). The DAP adapter
    // sets a LINE breakpoint in RunToBreakpoint, runs it on a worker thread,
    // hits the breakpoint, then inspects the locals (scopes/variables via the
    // Neo GetLocalVariableInfo arm). The KNOWN local values (PRIM_DAP / REF_DAP)
    // are set BEFORE the breakpoint line + are still LIVE at the stop (the
    // breakpoint fires at a statement boundary after both assigns), so the
    // adapter's variables() MUST surface them -- the load-bearing gate.
    //
    // NON-GENERIC + top-level so the DAP line->method bind (FindMethodByLine,
    // a LoadedTypes scan) resolves + the StartLine/EndLine window is stable.
    //
    // RunToBreakpoint calls a second method (Callee) so a step-over lands on a
    // distinct sequence point (exercises `next`), then returns a sentinel so the
    // check can confirm `continue` ran to completion.

    public class NeoDebuggerDapProbe
    {
        public int FieldInt = 3333;

        // A line breakpoint is set here (the DAP adapter binds on the method's
        // StartLine/EndLine window). The locals are assigned BEFORE the stop line
        // so they are live in the frame at the breakpoint.
        public int RunToBreakpoint()
        {
            int prim = 4242;                 // PRIM_DAP -- known primitive local
            string msg = "dap-local-value";  // REF_DAP  -- known reference local
            int after = Callee();            // step-over target (a distinct line)
            return prim + after;             // sentinel: continue runs to here
        }

        public int Callee()
        {
            int inner = 100;
            return inner + 1;
        }
    }
}
