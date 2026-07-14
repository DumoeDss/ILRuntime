using System;

namespace TestCases
{
    // Regression probe for the Neo `unbox.any T` reference-type cluster (fresh-60
    // smoke cluster B). Roslyn lowers `(T)o` on a generic type param T that is
    // interface-constrained (so T may be a value type) to `unbox.any T`. For a
    // reference-type T this is a reference copy (null passes through). On HEAD the
    // Neo Unbox/Unbox_Any arm throws InvalidCastException (IL-class/interface T) /
    // NullReferenceException (null source); after the fix these pass.
    public interface INeoUnboxAnyRef
    {
        int Id { get; set; }
    }

    public class NeoUnboxAnyRefImpl : INeoUnboxAnyRef
    {
        public int Id { get; set; }
    }

    public static class NeoStepUnboxAnyRefTypeTest
    {
        // Interface-constrained generic T => `(T)o` emits `unbox.any T` (the CIL
        // path that faulted in the fresh-60 smoke, e.g. GenericMethodTest9's
        // `return (T)result;`).
        static T CastToT<T>(object o) where T : INeoUnboxAnyRef
        {
            return (T)o;
        }

        public static void NeoStepUnboxAnyRef_TC1()
        {
            // `(T)o` for a reference-type T must preserve identity (ref copy).
            var obj = new NeoUnboxAnyRefImpl { Id = 4242 };
            var res = CastToT<NeoUnboxAnyRefImpl>(obj);
            if (res == null || ((INeoUnboxAnyRef)res).Id != 4242)
                throw new Exception("TC1: unbox.any ref-copy lost identity/Id");
        }

        public static void NeoStepUnboxAnyRef_TC2()
        {
            // null source for a reference-type T: unbox.any must yield null, not
            // throw NullReferenceException (the RefOutNull2 / GenericStaticMethodTest19
            // shape).
            NeoUnboxAnyRefImpl res = CastToT<NeoUnboxAnyRefImpl>(null);
            if (res != null)
                throw new Exception("TC2: expected null from unbox.any of null source");
        }

        public static void NeoStepUnboxAnyRef_TC3()
        {
            // `(T)o` where T is the interface type itself (also a reference type):
            // unbox.any must return the object typed as the interface.
            var obj = new NeoUnboxAnyRefImpl { Id = 17 };
            INeoUnboxAnyRef res = CastToT<INeoUnboxAnyRef>(obj);
            if (res == null || res.Id != 17)
                throw new Exception("TC3: unbox.any to interface lost value");
        }
    }
}
