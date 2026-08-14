// A bare identifier that resolves nowhere - not a local, a parameter, a
// member of the enclosing chain, or a root global - is dm.exe's plain
// "undefined var". Value position is VARS-ONLY: a proc name does not satisfy
// it, which the mined probes pin with &f and initial(p) both erroring.

var/global_ok = 2

/datum/bare
	var/member_ok = 1
	proc/f()
		var/local_ok = member_ok + global_ok
		return local_ok + missing_name
	proc/g()
		return f
