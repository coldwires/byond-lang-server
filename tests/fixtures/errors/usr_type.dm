// `usr` is a /mob and stays one whatever world.mob says: it is a runtime default
// for what a connecting client gets, not a static retype. Compile-only; probed
// 2026-08-16 on 516.1687. The two controls compile: usr.key is a /mob var and
// usr.density an /atom one, while the /mob/player var is unreachable through it.
/mob/player
	var/player_only = 1
world/mob = /mob/player
/obj/thing
	proc/f()
		var/k = usr.key
		var/d = usr.density
		var/p = usr.player_only
		return k + d + p
