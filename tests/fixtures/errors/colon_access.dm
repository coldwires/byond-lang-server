// `:` is not "unchecked" - it is a WIDER check, and the width differs by operator.
//
// Probed one case per compilation unit against 516.1687, then collected here:
// these are semantic errors rather than syntax ones, so dm.exe reports all of
// them in one pass instead of stopping at the first.
//
//   `.`  / `?.`               the declared type and its ancestors
//   `:`  on a written type    the declared type, its ancestors, AND ITS SUBTYPES
//   `?:` on anything          the WIDEST question: is this a member of ANYTHING
//   either, untyped receiver  the same widest question
//
// The pair that separates `:` from `?:` is `M:elsewhere` erroring while
// `M?:elsewhere` compiles - same receiver, same member, one character apart.
// Both compiling forms live in ok/parsing.dm, asserted by value.
//
// SUBTYPE MEANS INHERITANCE, NOT PATH: a type carrying `parent_type = /mob/test`
// satisfies `M:` on a /mob/test receiver however its own path reads. Walking
// path children would miss every re-parented type, and re-parenting is ordinary
// DM - /mob itself descends from /atom/movable rather than from the root.
//
// The kind matters too: a name that is only a PROC does not satisfy a var
// access, so `x:only_a_proc` is "undefined var" even though the proc exists.

/mob/test
	var/hp = 1

/mob/test/special
	var/on_subtype = 5

/datum/other
	var/elsewhere = 9
	proc/only_a_proc()
		return 1

// A written receiver, and a member that lives on an UNRELATED type. The subtype
// walk must not reach it.
/proc/unrelated_member()
	var/mob/test/M = new
	return M:elsewhere

// A written receiver and a name on nothing at all.
/proc/nowhere_member()
	var/mob/test/M = new
	return M:nowhere_xyz

// An untyped receiver asks the widest question, and still answers no here.
/proc/untyped_nowhere()
	var/x
	x = 1
	return x:nowhere_xyz

// A proc name does not satisfy a VAR access, however real the proc is.
/proc/proc_name_as_var()
	var/x
	x = 1
	return x:only_a_proc

// The invoked twin reports "undefined proc" instead, on a name that exists only
// on an unrelated type.
/proc/unrelated_proc()
	var/mob/test/M = new
	return M:only_a_proc()
