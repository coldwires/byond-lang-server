// An `else` with no preceding `if` is an error even after a valid statement
// and a `;`. The separator tolerance hands the else back to an enclosing if;
// it must not conjure one.

/proc/e_orphan_else(a)
	var/r = 0
	r = 1; else r = 2
	return r
