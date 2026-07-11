# neo-addi-on-float

CORRECTNESS (child-15 surfaced, pre-existing): addi integer-adds float bit-patterns (a.X += 100 -> addi r,r,0x42C80000). float += const is broken pervasively. Fix the JIT/optimizer lowering for float += const.
