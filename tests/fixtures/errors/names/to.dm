// `to` is not a legal variable name. It fails on the USE, not the declaration,
// which is why a probe that only declares it and reads it back compiles.
//
// Its own .dme: a syntax error stops dm.exe before anything else in the same
// compilation unit is checked, so two of these in one file mask each other.

/proc/e_var_named_to()
	var/to = 40
	return to
