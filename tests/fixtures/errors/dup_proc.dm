// `proc/` means declare-new, and declaring the same name twice on one type is
// a duplicate definition - TWO diagnostics, "duplicate" on the later line and
// "previous" on the first. An override (no `proc/` segment) is the legal way
// to replace a body; see ok/parsing.dm for the shapes that stay clean.

/datum/dup
	proc/f()
		return 1
	proc/f()
		return 2
