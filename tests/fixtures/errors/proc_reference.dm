// A proc REFERENCED without parentheses, in value position, through a receiver whose
// type is WRITTEN DOWN.
//
// §8 records the search as kind-sensitive for `:` — `x:only_a_proc` read as a var is
// "undefined var" though the proc exists. This pins the same for `.` with a written
// type, which is the case that decides a code action: the "declare the type" quick fix
// must NOT be offered here, because writing the type down does not make this compile.
// Probed 2026-08-16 on 516.1687 while building that action; the test that expected a
// fix was the thing that was wrong.

/obj/item
	var/hp = 1
	proc/use()
		return 1

/proc/reads_a_proc_as_a_value()
	var/obj/item/x = new /obj/item
	return x.use
