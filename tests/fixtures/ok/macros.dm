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

// A macro whose BODY is a directive. `int MACRO_MADE 2` expands to
// `#define MACRO_MADE 2` and dm.exe RE-PROCESSES the expansion, so the macro
// exists afterwards - madridspy builds its whole status-flag vocabulary this
// way (`#define int #define`, then `int DEAD 2` and eight more). We re-process
// too since 2026-08-13, the same day the -code_tree oracle surfaced the gap: a
// line-starting object-like macro whose body begins with `#` splits the run
// and its line becomes a directive. Only the HEAD expands - a directive's
// arguments are raw, or `#undef FOO` would undefine FOO's VALUE. The #undef
// keeps `int` from leaking into the rest of the suite.
#define int #define
int MACRO_MADE 2
#undef int

// A skipped region whose content is indented. The newline after its #endif
// sits at the SKIPPED depth until the next code line dedents, and levelling it
// invented a block with nothing in it. The declaration after the region must
// exist and the one inside it must not.
//
// FALSE is deliberately the BUILT-IN macro (515+), not a local define: this is
// tgstation's own `#define MERGERS_DEBUG FALSE` + `#if MERGERS_DEBUG` shape,
// which reported "'FALSE' is not defined" until the built-ins were seeded.
#define MACROS_FIXTURE_OFF FALSE
#if MACROS_FIXTURE_OFF
/datum/macros_never
	var/x = 1
#endif
/datum/macros_after
	var/y = 2

// An #include inside an open bracket splices a file into the expression - the
// tgs module's ApiVersion() shape. The value proves the splice landed where it
// was written, not merely that something compiled.
/datum/spliced_ver
	var/raw

/datum/spliced_ver/New(raw_parameter)
	raw = raw_parameter

/proc/spliced_version()
	return new /datum/spliced_ver(
		#include "version_num.dm"
	)

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

	CHECK("a macro-made macro exists", MACRO_MADE, 2)
	CHECK("stringify", STRINGIFY(abc), "abc")
	CHECK("token paste", GLUE(2, 5), 25)
	CHECK("repeat operator", TWICE(1), 11)   // 2###t repeats t twice; the 2 is consumed

	CHECK("skipped region declares nothing", text2path("/datum/macros_never"), null)
	var/datum/macros_after/A = new
	CHECK("declaration after a skipped region", A.y, 2)

	var/datum/spliced_ver/V = spliced_version()
	CHECK("expression-position include", V.raw, "5.11.0")
