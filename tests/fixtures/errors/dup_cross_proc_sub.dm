// Both re-declarations draw their own "duplicate definition"; the ancestor's
// "previous definition" appears once, not per pair (probed 2026-08-13).

/datum/crossdup/one
	proc/f()
		return 2

/datum/crossdup/two
	proc/f()
		return 3
