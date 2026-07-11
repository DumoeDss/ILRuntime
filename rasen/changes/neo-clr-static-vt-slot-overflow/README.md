# neo-clr-static-vt-slot-overflow

CLR static VT field TestVector3.One NIEs (14 hits) post-child-8 -- the NeoClrVtStaticFieldIsUnsafe guard (child-8 kept ref-field + slot-overflow) fires. Investigate: is it a real slot-overflow or a guard bug? Fix.
