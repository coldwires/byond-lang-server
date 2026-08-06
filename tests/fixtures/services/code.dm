// The acceptance target, end to end: `t.` for a typed local offers the
// declared type's members plus inherited builtins, `.` and `:` stay distinct,
// and definition, hover and signature help agree on the same resolution.

/proc/fight()
	var/mob/test/t = new
	t.hp = 50
	//? complete 7:4 => hp, weapon, heal, loc, Move, !on_subtype, !reload
	//? definition 7:4 => types.dm:8
	//? hover 7:4 => /mob/test/hp
	t:on_subtype = 1
	//? complete 11:4 => on_subtype, hp
	t.heal(10, 1)
	//? signature 13:13 => heal @ 1
	//? definition 13:4 => types.dm:11
	var/gun_count = AMMO_MAX
	//? complete 16:18 => AMMO_MAX, t, fight
	return gun_count

// A call result has no written type, so dm.exe compiles the `.` unchecked and
// the honest completion list after it is EMPTY - showing everything would be
// noise, and showing the declared-type list would claim inference dm does not
// do for diagnostics.
/proc/scavenge()
	var/scraps = mk().hp
	//? complete 25:20 => (empty)
	return scraps

/proc/mk()
	return new /mob/test

// The reference index, through the same positions: every USE of hp, with its
// kind - the bare `hp += amount` inside heal is a write resolved against the
// enclosing chain, `t.hp = 50` a write through a typed receiver. The
// declaration itself is not a use. And heal's uses are its one call site.
//? references 7:4 => types.dm:12 write, code.dm:7 write
//? references 13:4 => code.dm:13 call
