// `throw` is a legal type-path SEGMENT (ok/parsing.dm declares a type named
// throw) but not a variable name. The error lands on the declaration's `=`,
// unlike `in` which fails on the use.

/proc/e_var_named_throw()
	var/throw = 1
	return 0
