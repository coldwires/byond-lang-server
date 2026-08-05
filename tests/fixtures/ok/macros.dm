// Macro shapes real codebases use, which is where the preprocessor's edges are.
// These are /tg/station's own definitions, reduced to the token shapes that
// broke us - a macro argument containing `?[` lost everything after it, silently,
// so the parse failed on a stream that was simply missing its tail.

#define PROC_REF(X) (nameof(.proc/##X))
#define TYPE_PROC_REF(TYPE, X) (nameof(##TYPE.proc/##X))

#define WRAP(a, b) ("[(a)] #[(b)]")
#define OUTER(rt, off) (WRAP(rt, blacklist?["[rt]"] ? 0 : off))
#define TARGET "*W"

// Stringify, paste, and the repeat operator.
#define STRINGIFY(x) #x
#define GLUE(a, b) a##b
#define TWICE(t) 2###t

#define SECONDS *10

/datum/macros
	var/list/blacklist = list()

	proc/target()
		return "target"

	proc/proc_ref_is_a_path()
		return PROC_REF(target)

	proc/type_proc_ref_with_a_trailing_separator()
		return TYPE_PROC_REF(/datum/macros/, target)

	// The argument here contains `?[`, a string with a hole, and a ternary.
	proc/argument_survives_a_null_index(off)
		return OUTER(TARGET, off)

	proc/cooldown_shape()
		return 0. SECONDS

/proc/run_macros()
	var/datum/macros/M = new

	CHECK("PROC_REF resolves to the name", M.proc_ref_is_a_path(), "target")
	CHECK("TYPE_PROC_REF with trailing sep", M.type_proc_ref_with_a_trailing_separator(), "target")
	CHECK("macro arg keeps its tail", M.argument_survives_a_null_index(3), "*W #3")
	CHECK("macro expanding to an operator", M.cooldown_shape(), 0)

	CHECK("stringify", STRINGIFY(abc), "abc")
	CHECK("token paste", GLUE(2, 5), 25)
	CHECK("repeat operator", TWICE(1), 11)   // 2###t repeats t twice; the 2 is consumed
