# neo-async-movenext-frame-stacking

wave2 PREREQUISITE: DriveMoveNextCore reuses stack.StackBase for resumed MoveNext byte-frame -> overwrites live outer driver frame. Sub-int slot-sizing fix blocked by this. Fix = stack MoveNext frame above current top. HIGH-RISK (Step-19/20 resume path). re-audit + fix + full-smoke 9->lower (unblocks sub-int slot fix -> 8)
