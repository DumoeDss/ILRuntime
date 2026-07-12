using System;

namespace TestCases
{
    // neo-activator-createinstance-neo-redirect probe (child 22 of neo-overhaul).
    // Under Neo, Activator.CreateInstance on an IL type threw MissingMethodException
    // because the hand-written CLRRedirections.CreateInstance/2/3 redirects were
    // registered on Legacy's RedirectMap ONLY -- not on RedirectMapNeo -- so the
    // call fell through to the autogen System_Activator_Binding.CreateInstance_*_Neo
    // stub, which calls host Activator.CreateInstance<ILTypeInstance>() (ILType-
    // Instance has no public parameterless ctor). A Neo redirect
    // (CreateInstanceNeo / CreateInstance2Neo / CreateInstance3Neo) now intercepts
    // the call and routes IL types through ILType.Instantiate() / Instantiate(args).
    //
    // Assertion discipline (child-1/child-2/child-6): NO try/catch. On HEAD the
    // uncaught MissingMethodException propagates and the test FAILs. With the fix
    // the instance is created and the value assertions run; a wrong value surfaces
    // as the deliberate 1/0 (DivideByZero) fault -- so this is a real correctness
    // check, not a ran-without-throwing check. Tests are `public static void`
    // parameterless; names embed "NeoStep" so they run in the NeoStep smoke.

    public class NeoStepActivatorCreateInstanceData
    {
        public int IntValue { get; set; }
        public string StringValue { get; set; }

        public NeoStepActivatorCreateInstanceData() { }
        public NeoStepActivatorCreateInstanceData(int intValue, string stringValue)
        {
            IntValue = intValue;
            StringValue = stringValue;
        }
    }

    public class NeoStepActivatorCreateInstanceTest
    {
        // TC1: generic Activator.CreateInstance<T>() on an IL reference type.
        // On HEAD: MissingMethodException (uncaught) -> FAIL.
        public static void NeoStepActivatorCreateInstance_TC1_Generic()
        {
            var inst = Activator.CreateInstance<NeoStepActivatorCreateInstanceData>();
            if (inst == null) { int z = 1; int d = 0; int _ = z / d; }
            if (inst.IntValue != 0) { int z = 1; int d = 0; int _ = z / d; }
            if (inst.StringValue != null) { int z = 1; int d = 0; int _ = z / d; }
        }

        // TC2: Activator.CreateInstance(Type) on an IL type.
        // On HEAD: MissingMethodException (uncaught) -> FAIL.
        public static void NeoStepActivatorCreateInstance_TC2_Type()
        {
            var inst = (NeoStepActivatorCreateInstanceData)Activator.CreateInstance(typeof(NeoStepActivatorCreateInstanceData));
            if (inst == null) { int z = 1; int d = 0; int _ = z / d; }
            if (inst.IntValue != 0) { int z = 1; int d = 0; int _ = z / d; }
            if (inst.StringValue != null) { int z = 1; int d = 0; int _ = z / d; }
        }

        // TC3: Activator.CreateInstance(Type, object[]) on an IL type -- the ctor
        // args must round-trip through ILType.Instantiate(object[]). On HEAD:
        // MissingMethodException (uncaught) -> FAIL.
        public static void NeoStepActivatorCreateInstance_TC3_TypeWithArgs()
        {
            var inst = (NeoStepActivatorCreateInstanceData)Activator.CreateInstance(
                typeof(NeoStepActivatorCreateInstanceData), 777, "activator-arg");
            if (inst == null) { int z = 1; int d = 0; int _ = z / d; }
            if (inst.IntValue != 777) { int z = 1; int d = 0; int _ = z / d; }
            if (inst.StringValue != "activator-arg") { int z = 1; int d = 0; int _ = z / d; }
        }
    }
}
