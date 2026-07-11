# neo-f7-delegate-byref

TRUE COMPLETION: F-7 delegate ref/out byref marshal -- a frame-to-frame delegate-invoke fast path that preserves the byref Ref Slot + propagates the ref/out write-back (the byref is destroyed in 2 sites: ReadNeoDelegateInvokeArgs object[] funnel + NeoInvokeSub separate pooled interpreter).
