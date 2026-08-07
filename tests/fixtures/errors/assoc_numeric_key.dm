// A numeric associative-list key is an ERROR as of 516.1686, and was accepted by
// 516.1666. This is a genuine breaking language change rather than a new warning:
// code that built clean stops building.
//
// Found by moving the goldens to 1686 and re-running diagdiff on the corpus -
// madridspy goes from 0 errors to 2 on the same source, both of them this. The
// escape hatch is in the message: alist() still takes numeric keys, which is what
// 516 added alist for.
//
// One expectation per compilation unit, so the string-key and alist() controls
// that pin this to NUMBERS live in ok/parsing.dm rather than beside it here -
// dm.exe stops at the first error and they would never be reached.

/proc/numeric_key()
	var/list/L = list(1 = "a")
	return L
