# neo-f13-nested-run-executeneo

TRUE COMPLETION: F-13 nested appdomain.Invoke inside an in-flight ExecuteNeo corrupts the outer method's instruction pointer. Isolate the root cause (suspected process-static shared across per-interpreter frames) + fix.
