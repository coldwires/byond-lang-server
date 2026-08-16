// The /path::NAME static form folds when written from a SIBLING (errors/const_fold,
// ok/constants.dm) and is rejected when written from an ANCESTOR of the path -
// the descendant inherits the very const being asked for, and dm.exe reads
// that as a cycle. Found by writing the ok/ fixture the obvious way first;
// the ancestor form is what a reader would try. Probed 2026-08-16 on 516.1687.
/datum/holder
	var/const/OWN_MAX = 40
	var/static_form = /datum/holder/child::OWN_MAX + 1

/datum/holder/child
