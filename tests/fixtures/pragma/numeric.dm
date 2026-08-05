// `#pragma warn|ignore|error` takes NUMERIC warning ids as well as names, which
// the DM Reference documents nowhere. From the compiler's own warning-id table
// (PLAN.md §8a); verified here by compiling.
//
// The asymmetry is the interesting half: an unknown NAME is a hard error, while
// an unknown NUMBER is silently accepted. A project can carry
// `#pragma ignore 9999` forever and never learn it does nothing.

// 3006 is unused_var. Suppressed by number, so this file must compile with no
// warnings at all despite the unused local below.
#pragma ignore 3006

/proc/has_an_unused_local()
	var/unused_local = 1
	return 2

// 9999 is not a warning id. Accepted in silence - no diagnostic about the
// pragma itself, which is why this file still compiles clean.
#pragma ignore 9999

/proc/still_clean()
	return 3
