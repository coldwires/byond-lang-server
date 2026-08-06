// Redeclaring `proc/f` on a SUBTYPE - at any depth - is also a duplicate
// definition, not an override: PLAN 4a's mob/proc/operator<< example. The
// override spells it without the `proc/` segment.

/datum/dup
	proc/f()
		return 1

/datum/dup/mid

/datum/dup/mid/deep
	proc/f()
		return 2
