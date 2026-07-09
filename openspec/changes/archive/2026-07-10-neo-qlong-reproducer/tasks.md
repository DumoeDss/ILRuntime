# Tasks: neo-qlong-reproducer (portfolio child 19)

- [x] Read lead-6 Q-LONG context + conv.i8 handling (ILIntepreter.Neo.cs:2330,
      JITCompiler.cs:2505, InferPrimTag at :944).
- [x] Construct adversarial reproducer guards (3) in `TestCases/NeoStepQReproTest.cs`.
- [x] Build CLI (Debug_Neo) + TestCases (Debug).
- [x] Run `NeoStep` smoke; confirm the 3 guards PASS on HEAD (non-repro).
- [x] Confirmed-closed (doc-only); guards stay as permanent regression guards.
