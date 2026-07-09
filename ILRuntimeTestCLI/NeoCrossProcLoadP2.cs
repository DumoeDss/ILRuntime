#if ENABLE_NEO_MODE
namespace ILRuntimeTestCLI
{
    /// <summary>
    /// Step 25 cross-process P2 thin wrapper (neo-aot-crossprocess, child 9).
    /// Delegates to NeoStep25CrossProcessCheck.RunP2 in the ILRuntime assembly
    /// (which has internal access to NeoAssemblyReader / LoadNeoAssembly; the CLI
    /// assembly does not). The real P2 logic + its documentation live there.
    /// </summary>
    internal static class NeoCrossProcLoadP2
    {
        internal static int Run(string neoPath, string expectedStr)
        {
            return ILRuntime.Runtime.Intepreter.RegisterVM.NeoStep25CrossProcessCheck.RunP2(neoPath, expectedStr);
        }
    }
}
#endif
