# neo-stfld-value-inframe-owner

wave2: Stfld_Value in-frame (non-heap) owner NRE (StructTest3/14). JIT emits plain Stfld_Value for IL-struct local/this, no _Inline form. re-audit + fix + full-smoke 73->lower
