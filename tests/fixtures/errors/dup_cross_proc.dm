// The ancestor's half of a cross-file duplicate pair: dm.exe reports
// "previous definition" HERE, in this file, when a descendant declared in
// ANOTHER file re-declares the proc - and once, however many descendants do.

/datum/crossdup
	proc/f()
		return 1
