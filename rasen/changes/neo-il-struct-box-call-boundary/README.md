# neo-il-struct-box-call-boundary

wave2 2-for-1: IL-struct not boxed to ILTypeInstance at Callvirt_CLR ref-typed param boundary (StructTest11 + TestStructDictionary). 3-site fix: NeoCallParamMap flag + optimizer map-build + CopyNeoCallArguments boxing. re-audit + fix + full-smoke 15->lower
