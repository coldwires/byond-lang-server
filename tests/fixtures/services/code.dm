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

// A macro resolves at its use site to the #define that produced it — the
// preprocessor replaces the token before the parser sees it, so the macro
// reading wins over every other. 16:18 is the AMMO_MAX in gun_count's
// initializer; the definition is types.dm line 5.
//? definition 16:18 => types.dm:5
//? hover 16:18 => #define AMMO_MAX

// A TWO-LEVEL CHAIN. `t.weapon` is an /obj/gun by its written type, so the next
// `.` offers that type's members. PLAN §4a context 3 is the rule: a dotted run
// with no leading separator is MEMBER ACCESS, not the type path `/t/weapon`.
// Folding it into a path is what made every two-level chain answer nothing -
// `src.client.`, `usr.client.` and this one alike, whether the vars were
// builtin or written in the project. A single receiver worked throughout, which
// is why it read as a builtins problem rather than a chaining one.
/proc/chain()
	var/mob/test/t = new
	t.weapon = new /obj/gun
	t.weapon.ammo = 5
	//? complete 56:11 => ammo, reload, !hp, !weapon
	//? definition 56:11 => types.dm:18
	return t.weapon.ammo

// `usr` is always a /mob and, unlike `src`, does NOT take the enclosing type -
// compiler-verified by reaching a /mob-only var from a proc on /obj, with a
// nonexistent member as the control that proves dm.exe checks it at all. `hp`
// belongs to /mob/test and must be absent, or this would pass against a
// receiver resolved to the wrong type.
/proc/who()
	var/k = usr.key
	//? complete 67:14 => key, client, loc, Move, !hp
	return k
