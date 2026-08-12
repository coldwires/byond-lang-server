// unused_label, and what counts as USING a label. One case per proc, pinned
// against 516.1686: this file compiles with 0 errors and 3 warnings.
//
// Labels sit on their OWN LINE. `looped: for(...)` on one line is a syntax
// error - the first draft of this probe wrote it that way and dm.exe answered
// "var/i: undefined var", which is worth knowing before modelling one.

// a. nothing refers to it - WARNS
/proc/a_bare()
	var/r = 0
	lonely: {
		r = 1
	}
	return r

// b. `break <name>` is a use
/proc/b_break()
	var/r = 0
	for(var/i in 1 to 2)
		used: {
			r = 1
			break used
		}
	return r

// c. `continue <name>` is a use
/proc/c_continue()
	var/r = 0
	looped:
		for(var/i in 1 to 3)
			r++
			continue looped
	return r

// d. `goto <name>` is a use
/proc/d_goto()
	var/r = 0
	if(r)
		goto target
	target
	return r

// e. a label before a loop that nothing names - WARNS, exactly like any other
/proc/e_loop_label_unused()
	var/r = 0
	spare:
		for(var/i in 1 to 2)
			r++
	return r

// f. A BARE `break` DOES NOT USE THE LABEL - WARNS. This is the case an
// implementation is most likely to get wrong, since the break is right there
// inside the labelled block and looks like it belongs to it.
/proc/f_bare_break()
	var/r = 0
	for(var/i in 1 to 2)
		outer: {
			r = 1
			break
		}
	return r
