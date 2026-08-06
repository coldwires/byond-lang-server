// A LOCAL var's initializer cannot end in a top-level relational `in` -
// "unexpected 'in' expression" whatever sits on the operator's left: a bare
// name, a parenthesised one, or a whole ternary. The same text is legal as an
// assignment statement, a global's initializer, or a type-level var's, and
// both `var/r = (y in L)` and `var/found = locate(X) in L` are legal here -
// those live in ok/parsing.dm with their runtime values.

/proc/e_local_in()
	var/list/L = list(1,2,3)
	var/y = 5
	var/r = y in L
	return r
