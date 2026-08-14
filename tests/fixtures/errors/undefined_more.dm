// The rest of the undefined-var/proc family, each rule probed before the
// code that matches it:
// - `usr` does not exist in a type-level initializer (probed in the datum
//   var, global var and bare override spellings alike)
// - an unknown `set` name is "undefined var" on the set line; the accepted
//   vocabulary is ten names, identical in verbs and procs
// - a bare call that no proc anywhere satisfies is "undefined proc", and a
//   var does not satisfy a call: the called local below draws the error AND
//   dm.exe's own unused_var, since a call is not a read

/datum/more
	var/at_type_level = usr

/proc/set_probe()
	set bogus_setting = 1
	return 1

/proc/call_probe()
	var/x = 5
	x()
	return 1

/proc/global_probe()
	return no_such_global_xyz()
