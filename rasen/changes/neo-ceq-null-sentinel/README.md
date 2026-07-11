# neo-ceq-null-sentinel

CORRECTNESS: the ceq form of the null-comparison gap (sibling of child-11 brtrue). Neo ceq compares raw mStack index N vs ldnull -1 as integers -> never equal for an IL-static null -> lazy-init skipped -> NRE. TestStaticFieldInstance/RegisterVMTest04. Apply the null-deref treatment to ceq/beq.
