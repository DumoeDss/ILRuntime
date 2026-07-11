# neo-il-static-ref-field-readback

Reading a freshly-written IL-static REFERENCE field's value (stsfld->ldsfld->use) returns ILTypeInstance instead of unwrapping the CLR object (TestStaticFieldInstance InvalidCastException; child-12 TC1 read 3 not 123). Fix the static-ref-field read-back.
