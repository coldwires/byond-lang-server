// The acceptance-target types. Positions in code.dm's //? annotations are
// 1-based line:column, the CLI's convention, so any of them can be reproduced
// with `dmc complete|definition|hover|signature <dme> code.dm <line> <col>`.

#define AMMO_MAX 30

/mob/test
	var/hp = 100
	var/obj/gun/weapon
	/// Heals the mob.
	proc/heal(amount as num, silent = 0)
		hp += amount

/mob/test/special
	var/on_subtype = 1

/obj/gun
	var/ammo = AMMO_MAX
	proc/reload()
		ammo = AMMO_MAX
