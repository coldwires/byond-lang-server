// The VAR half of DM0403. The proc half shipped 2026-08-05; this one waited
// because VarSymbol kept a single site and had nowhere to record the others.
//
// THE PAIR'S LINES ARE INVERTED against the proc half, which is why it was
// probed rather than assumed: for two declarations on ONE type, dm.exe puts
// "duplicate definition" on the FIRST and "previous definition" on the second.
// The ancestor case below is the normal way round.
//
// A BARE OVERRIDE IS NOT A DECLARATION - `settable = 2` with no `var/` is
// ordinary DM and stays silent. That control matters more than the failures: a
// check that read it as a redeclaration would fire on most of a real game.

// a. the same var twice on one type - a pair, first line called the duplicate
/datum/a
	var/twice = 1
	var/twice = 2

// b. redeclaring what the parent declares - a pair, child called the duplicate
/datum/b
	var/inherited = 1
/datum/b/child
	var/inherited = 2

// c. CONTROL: a bare override of an inherited var, no `var/` - legal
/datum/c
	var/settable = 1
/datum/c/child
	settable = 2

// d. colliding with a BUILTIN - one line, its own message, no pair
/mob/d
	var/name = "x"

// e. CONTROL: the same name on unrelated types - legal
/datum/e1
	var/shared = 1
/datum/e2
	var/shared = 2
