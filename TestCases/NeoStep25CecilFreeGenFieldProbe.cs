using System;
using System.Collections.Generic;

namespace TestCases
{
    // ===== Step 25 (Cecil-free generic TYPE-instance fields): the follow-up probe =====
    //
    // A type with GENERIC-INSTANTIATION INSTANCE FIELD TYPES:
    //   - List<int>            (a CLR generic def List<> instantiated at a CLR arg int)
    //   - List<string>         (a CLR generic def instantiated at a CLR arg string)
    //   - List<NeoStep25GenFieldItem> (a CLR generic def instantiated at an IL arg)
    //   - Dictionary<int, string> (two CLR args)
    //
    // The child-8 Cecil-free machinery resolved a generic-METHOD instance; this
    // probe targets the generic-TYPE-instance FIELD TYPE surface. A field whose
    // TYPE is a generic instantiation serializes (HybridPatch TypeReferencePatchInfo)
    // as IsGenericInstance=true + an ElementType (the generic def, e.g. List`1) +
    // GenericArguments[] (the type args). On HEAD the Cecil-free ILType factory
    // (CreateFromNeoRecord) resolves each field type via ResolveNamedIType(info),
    // which reads ONLY info.Name -- and for a generic instance info.Name is NULL
    // (the serializer never sets it for a GenericInstanceType). So the field type
    // resolves to null -> ldfld/stfld/field-method-invocation fails. The follow-up
    // adds a Cecil-free generic-instance type constructor in the field-type
    // resolution (resolve the generic def + each type arg Cecil-free, then
    // MakeGenericInstance).
    //
    // PARAMETERLESS wrappers (the Run shim is no-arg). INSTANCE methods so the
    // Cecil-free ILType must be Instantiate'd first (exercises the Cecil-free
    // instance-ctor path too). The wrappers construct the field, Add elements, and
    // return the Count -- exercising the generic-instance field's CONSTRUCTION +
    // VIRTUAL dispatch on the constructed instance + field read/write.
    //
    // Top-level + NON-GENERIC type + the Item type top-level too: keeps every
    // TypeRef full name == the LoadedTypes key + within the S1/S2 reference
    // boundary.

    public class NeoStep25CecilFreeGenFieldProbe
    {
        // CLR-T-arg generic-instance field. List<int>.
        public List<int> intItems = new List<int>();

        // CLR-ref-T-arg generic-instance field. List<string>.
        public List<string> strItems = new List<string>();

        // IL-T-arg generic-instance field. List<NeoStep25GenFieldItem>.
        public List<NeoStep25GenFieldItem> ilItems = new List<NeoStep25GenFieldItem>();

        // Two CLR-arg generic-instance field. Dictionary<int, string>.
        public Dictionary<int, string> map = new Dictionary<int, string>();

        // Parameterless wrapper: Add to the List<int> field + return Count.
        // Expected: 3.
        public int WrapListIntCount()
        {
            intItems.Add(10);
            intItems.Add(20);
            intItems.Add(30);
            return intItems.Count;
        }

        // Parameterless wrapper: Add to List<string> + return Count.
        // Expected: 2.
        public int WrapListStrCount()
        {
            strItems.Add("a");
            strItems.Add("bb");
            return strItems.Count;
        }

        // Parameterless wrapper: Add an IL-T element to List<IL-T> + return Count.
        // Expected: 2.
        public int WrapListIlTCount()
        {
            var i0 = new NeoStep25GenFieldItem();
            i0.Value = 5;
            ilItems.Add(i0);
            var i1 = new NeoStep25GenFieldItem();
            i1.Value = 7;
            ilItems.Add(i1);
            return ilItems.Count;
        }

        // Parameterless wrapper: read-back the IL-T element's field via the indexer
        // (exercises the generic-instance field's indexer virtual dispatch on an
        // IL-T element). Expected: i1.Value = 7.
        public int WrapListIlTIndex()
        {
            var i1 = new NeoStep25GenFieldItem();
            i1.Value = 7;
            ilItems.Add(i1);
            return ilItems[0].Value;
        }

        // Parameterless wrapper: Add to Dictionary<int,string> + return Count.
        // Expected: 2.
        public int WrapDictCount()
        {
            map.Add(1, "one");
            map.Add(2, "two");
            return map.Count;
        }
    }

    // A top-level (NON-NESTED) reference type used as a generic-instance field's
    // IL type arg (List<NeoStep25GenFieldItem>). Top-level keeps its TypeRef full
    // name == the LoadedTypes key. One int field so the wrapper observes the
    // element via a property-style field read (no [VT-THIS-ADDR] needed -- it is a
    // reference type).
    public class NeoStep25GenFieldItem
    {
        public int Value;
    }
}
