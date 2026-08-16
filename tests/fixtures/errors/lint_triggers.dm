// dm.exe's two default-OFF lints, and the trigger set the reference gets wrong:
// `init_proc` (a var whose initialiser is not a compile-time constant) and
// `frequent_call` (New() or Del() overridden) fire on /datum, /atom and /turf
// EXACTLY plus the whole /turf subtree - the union of two rules, and neither
// "three exact types" nor "/turf and below" predicts it. /obj, /mob, /area,
// /datum/sub and /atom/sub are silent, which is where a real codebase's cost
// lives. Turned on here by pragma; the total is what pins every silence.
// Re-probed 2026-08-16 on 516.1687.
#pragma warn init_proc
#pragma warn frequent_call

/datum
	var/on_datum = list()             // warns
	New()                              // warns
		..()
/datum/sub
	var/on_datum_sub = list()         // silent
	New()                              // silent
		..()
/atom
	var/on_atom = list()              // warns
/atom/sub
	var/on_atom_sub = list()          // silent
/turf
	var/on_turf = list()              // warns
	var/a_literal = 5                 // silent: constant
	var/arithmetic = 1 + 2            // silent: folded
	var/a_path = /obj                 // silent: a path literal is a constant
	var/a_new = new /obj              // warns
	var/newlist_call = newlist(/obj)  // warns
	Del()                              // warns
		..()
/turf/sub
	var/on_turf_sub = list()          // warns: the whole /turf subtree
/turf/a/b/c
	var/deep_turf = list()            // warns
/obj
	var/on_obj = list()               // silent
	New()                              // silent
		..()
/mob
	var/on_mob = list()               // silent
/area
	var/on_area = list()              // silent
