// unused_var, and the three shapes that are EXEMPT.
//
// The exemptions are the point of this file. This check was written and backed
// out twice for inventing on projects that compile clean, and both causes are
// here: a `for` loop variable, and a `var` block header. Neither is a variable
// dm.exe will warn about, and both look exactly like one to a naive collector.

/obj/small/clothing

/proc/a_unused()
	var/never = 1
	return 0

/proc/b_write_only()
	var/wo
	wo = 1
	return 0

/proc/c_no_initializer()
	var/bare
	return 0

// Silent: read at all, in any position.
/proc/d_read()
	var/used = 1
	return used

/proc/e_read_through_not()
	var/passed = 1
	if(!passed)
		return 1
	return 0

/proc/f_read_in_interpolation()
	var/name = "x"
	return "[name]"

/proc/g_compound_counts_as_a_read()
	var/c = 1
	c += 1
	return 0

// Silent: a PARAMETER is never reported, however unused.
/proc/h_unused_parameter(p)
	return 0

// Silent: a LOOP VARIABLE is never reported. This is the cause that invented 14
// on mlaas and was recorded as never diagnosed.
/proc/i_loop_variable_unused()
	for(var/i in 1 to 3)
		return 1
	return 0

// Silent: a `var` block HEADER is a type, not a variable - both the written-out
// form and the bare form, and mlaas ships both.
/proc/j_var_block_header()
	var/obj/small/clothing
		this_C
		that_C
	var/list/stuff = list()
	for(this_C in stuff)
		return this_C
	return that_C

/proc/k_bare_var_block_header()
	var/mob
		who
		M
	for(M in world)
		who = M
	return who
