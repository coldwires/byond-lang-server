// The var half pairs the same way across files: re-declaring an ancestor's
// var/ draws "duplicate definition" on the descendant and "previous
// definition" on the ancestor's line. Probed 2026-08-13 - the var-over-
// ancestor pair had never been recorded, only the descendant's half.

/datum/crossvar
	var/x
