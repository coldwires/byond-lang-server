// Code dm.exe rejects on SEMANTIC grounds. This is the half a corpus can never
// supply: correct code contains none of it, so no amount of real DM tells us
// whether we report it.
//
// Split from the syntax cases deliberately. dm.exe stops before semantic
// analysis when a syntax error is present, so mixing the two masks every
// diagnostic in this file - the first version of this fixture reported one
// error out of eight for exactly that reason.
//
// One case per proc, so a line number identifies it exactly.

/obj/item
	var/hp = 1

/obj/item/sword
	var/sharpness = 5

/datum/unrelated
	var/elsewhere = 9

/obj/item/proc/use()
	return 1

// -- undefined members -------------------------------------------------------

/proc/e_undefined_var()
	var/obj/item/I = new
	return I.nowhere_at_all

/proc/e_undefined_proc()
	var/obj/item/I = new
	return I.nosuchproc()

// `.` checks the declared type ONLY, so a subtype's member is an error even
// though it plainly exists.
/proc/e_subtype_member_through_dot()
	var/obj/item/I = new
	return I.sharpness

// DM does no local inference: x has no type, so every member of it fails -
// including the right one.
/proc/e_untyped_receiver()
	var/x = new /obj/item
	return x.hp

// -- undefined type on a declared var ----------------------------------------

// The declaration compiles; the error lands on the USE line.
/mob/carrier
	var/clothing/slot

/mob/carrier/proc/e_undefined_declared_type()
	slot = null
	return 1
