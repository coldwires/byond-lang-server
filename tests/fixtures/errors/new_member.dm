// COMPILES CLEAN and raises at runtime, which is why the .expected beside this is empty and the
// diagdiff gate still passes: DM0302 is a deliberate divergence.
//
// dm.exe holds no type for a `new` expression - its own error text for a member that exists
// nowhere calls the receiver `<expression>` - so it accepts any member that exists on ANY type in
// the program. Runtime-verified on 516.1686 that the accepted form then fails:
//
//   new /mob/test(1).elsewhere  ->  runtime error, undefined variable /mob/test/var/elsewhere
//
// We know the constructed type exactly, because it is written two tokens away, so this is the
// rare case where the language server can be certain where the compiler declines to look.

/mob/test
	var/hp = 7

/datum/other
	var/elsewhere = 99

/proc/reaches_a_real_member()
	// The control: `hp` IS on /mob/test, so no warning belongs here.
	return new /mob/test(1).hp

/proc/reaches_an_unrelated_member()
	// `elsewhere` is on /datum/other. The object is a /mob/test. DM0302.
	return new /mob/test(1).elsewhere
