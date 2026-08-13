// A bare type name is not a receiver. `mob.name` is "undefined var" - a bare
// `mob` is neither a variable nor a path, since the path reading needs a
// leading separator (PLAN 4a context 3) - and no edit makes the expression
// legal: `var/mob/x` fixes `x.`, nothing fixes `mob.`.
//
// Our completion offered /mob's members here anyway until ABI 0.26, marked
// typeFrom "bareTypeName"; the fallback is gone and the honest list is empty.
// The compiling shapes that look like this one - a typed root global as the
// receiver, and a root global NAMED `mob` shadowing the type - are pinned in
// services/, since dm.exe stops at the first error and a control placed here
// would never be reached.

/mob/proc/t()
	var/x = mob.name
	return x
