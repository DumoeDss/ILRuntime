namespace NeoClrProbe
{
    /// <summary>
    /// Step 25 CLR-adaptor probe (the FOCUSED input for the
    /// <c>NeoStep25ClrAdaptor</c> host-side self-check). Two IL types exercise
    /// BOTH branches of the standalone-CLI graceful-skip contract:
    /// <list type="bullet">
    /// <item><c>ExceptionProbe : System.Exception</c> -- a CLR base that resolves
    /// via a BUILT-IN adaptor (<c>Adapters.ExceptionAdaptor</c>, registered by
    /// the AppDomain ctor at <c>AppDomain.cs:244</c>, so present in the generic
    /// CLI's bare <c>new AppDomain()</c>). The pre-filter's eager
    /// <c>FirstCLRBaseType</c> access finds the adaptor and SUCCEEDS -> this type
    /// is COMPILED and EMITTED (NOT skipped). Proves the built-in-adaptor path is
    /// preserved (Fix A is a no-op: the ctor already registers it).</item>
    /// <item><c>AdaptorProbe : ILRuntimeTest.TestFramework.TestClass2</c> -- a CLR
    /// base whose adaptor (<c>TestClass2Adapter</c>) is registered ONLY by the
    /// test harness's <c>ILRuntimeHelper.Init</c> (helper.cs:24), NEVER in the
    /// generic CLI's bare AppDomain. The lazy base-type resolution throws
    /// <c>TypeLoadException("Cannot find Adaptor for:...TestClass2")</c> at
    /// <c>ILType.cs:1593</c> -> the pre-filter SKIPS the WHOLE type (recorded in
    /// <c>NeoCompilerResult.Skipped</c> with a <c>(type)</c> marker; its methods
    /// are omitted; the <c>.neo</c> is still written for the survivors). Proves
    /// the harness-specific-adaptor path is a graceful skip, not a fatal.</item>
    /// </list>
    /// The generic CLI MUST NOT couple <c>TestClass2Adapter</c> (it lives in the
    /// <c>ILRuntimeTestBase</c> test-framework assembly); robustness comes from
    /// the SKIP, not from coupling.
    /// </summary>
    public class ExceptionProbe : System.Exception
    {
        // A trivial method so the type has real compilable content -- proving it
        // is EMITTED to the .neo (its TypeDef present), not merely present.
        public int Tag() => 42;
    }

    // Declared abstract so it need not override TestClass2's abstract members
    // (AbMethod1 / AbMethod2) -- irrelevant to the base-type adaptor lookup,
    // which fires in InitializeBaseType regardless of the type's abstractness.
    public abstract class AdaptorProbe : ILRuntimeTest.TestFramework.TestClass2
    {
    }
}
