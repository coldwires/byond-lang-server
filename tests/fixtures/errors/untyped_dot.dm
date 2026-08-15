// `.` through an UNTYPED var rejects every member, the RIGHT one included -
// dm.exe does no inference, so x.hp errors while hp sits on the type written
// two tokens away. Probed 2026-08-14 across every spelling: local, parameter,
// `as`-clause parameter, a member reached by bare name, and a global, with
// the member existing elsewhere or nowhere - and the invoked form is the proc
// twin. The unused_var lines are dm's own: a member access counts as a USE of
// its receiver only when the access compiles.

/mob/holder
	var/hp = 1
	proc/f()
		return 1

/proc/local_probe()
	var/x = new /mob/holder
	return x.hp

/proc/call_probe()
	var/y = new /mob/holder
	return y.f()

/proc/param_probe(a, m as mob)
	return a.hp + m.hp

/mob/test
	var/thing
	proc/member_probe()
		return thing.hp

var/gv
/proc/global_probe()
	return gv.hp
