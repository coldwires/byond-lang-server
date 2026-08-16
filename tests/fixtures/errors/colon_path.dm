// The DM Reference documents a leading `:` as a DOWNWARD path search - `:player`
// for /mob/player - and 516 rejects every spelling of it. Re-probed 2026-08-16 on
// 516.1687 in the same unit as an absolute-path control that compiles.
/mob/player
/proc/f()
	var/mob/m = /mob/player
	var/mob/n = :player
	return m || n
