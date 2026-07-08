# neo-async-movenext-fix

Step 20 Phase-2 TRUE COMPLETION: fix the MoveNext control-flow hang (state machine hangs after get_IsCompleted=false, before reaching AwaitUnsafeOnCompleted/GetResult). Truly-async await must WORK.
