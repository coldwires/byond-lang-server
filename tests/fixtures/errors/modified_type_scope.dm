// A modified-type initializer's values cannot see proc locals.
//
// `new /obj/gun{ammo = ...}` is legal anywhere a type value is (language notes
// §5), and the braces are mandatory there. What is NOT documented anywhere is
// what the values may refer to: a literal works, and so does a MACRO, because
// the preprocessor substitutes before the parser looks - but a local, or a
// member reached through one, is "undefined var".
//
// The second half of the compiler's answer is the part that settles it: it
// also reports unused_var on the local, which is dm.exe saying it never read
// the name at all rather than resolving it to something else. That is the same
// rule the untyped-receiver work pinned on 2026-08-14 - a use counts as a use
// only when the access compiles.
//
// Probed one case per compilation unit on 516.1687, since dm.exe stops at the
// first error: `= 5` and `= AMMO_MAX` compile, `= n` and `= g.ammo` do not.
// The compiling controls live in ok/parsing.dm, where they are asserted BY
// VALUE rather than by compiling - a construct compiling proves only that the
// parser allowed it.
//
// The consequence for us is coverage rather than correctness: the binder binds
// ModifiedTypeExpressionSyntax (one of the six expression holes closed on
// 2026-08-12), and no code dm.exe accepts can put a project symbol in that
// position, so the reference index can never be asserted through it. The
// tier-2 mark in services/code.dm records that where someone would look for it.

#define AMMO_MAX 30

/obj/gun
	var/ammo = AMMO_MAX

/proc/f()
	var/n = 5
	var/obj/gun/m = new /obj/gun{ammo = n}
	return m.ammo
