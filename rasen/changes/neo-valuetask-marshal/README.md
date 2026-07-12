# neo-valuetask-marshal

ValueTask<T> VT1/VT2/VT6 async failures. Re-routed real bug (clrstruct blocked.md): AsyncValueTaskMethodBuilder_T_SetResult_Neo curPrim+=8 skips the builder byref-this but the ValueTask builder byref-this is 16 bytes (8 F-10 byref + 8 struct flat-bytes). Compare vs AsyncTaskMethodBuilder<int> (TC8 works). Fix the skip + matching redirects.
