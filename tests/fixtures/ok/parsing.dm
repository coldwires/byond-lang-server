// Constructs we once rejected on code dm.exe compiles clean. Every one came out
// of diffing our diagnostics against the compiler's on a real project - none was
// imagined. Each is checked at runtime as well as parsed, so "we accept it now"
// is not mistaken for "we read it correctly".

/datum/parsing
	var/list/blacklist = list("k" = 7)
	var/junction = 0
	var/list/seen = list()

	// A label followed by a BRACE BLOCK. A `\`-continued macro body has no lines
	// to put a label on, so /tg/station writes this shape and breaks out by name.
	proc/labelled_block()
		do {
			outer: {
				if(junction)
					break outer
				junction = 1
			}
		} while(FALSE)
		return junction

	// A label on its own line, the form that always worked - kept as the control.
	proc/label_own_line()
		var/hits = 0
		outer:
			for(var/i in 1 to 3)
				hits++
				if(i == 2)
					break outer
		return hits

	// `?[` inside an interpolation hole. It is ONE token and still opens a
	// bracket; counting only a bare `[` ended the hole at its closing `]`.
	//
	// The branches are numbers on purpose: a STRING literal in this position -
	// `? "hit" : "miss"`, nested inside a hole that already contains the string
	// "[k]" - is rejected by dm.exe with "unterminated text expression". One
	// level of nesting is fine, two is not, and the fixture must not claim
	// otherwise.
	proc/null_index_in_a_hole(k)
		return "[( blacklist?["[k]"] ? 1 : 0 )] tail"

	// `x in lo to hi` - a range membership test in ordinary expression position,
	// not the switch-case form.
	proc/range_membership(v)
		if(v in 12 to 20)
			return "mid"
		return "out"

	// A for header over a range whose loop variable is already declared, with a
	// negative step. The header's `in` belongs to the loop, not to an expression.
	proc/for_in_range(n)
		var/total = 0
		for(n in n - 1 to 1 step -1)
			total += n
		return total

	// `step` is a legal variable name - it lexes as a keyword only because
	// step() is a builtin proc. It is the ONLY contextual keyword that is;
	// `in`, `as`, `set` and `to` are covered in ../errors.
	proc/step_is_a_name(limit)
		var/step = 0
		for(var/other in 1 to limit)
			step += other
		return step

	// A trailing dot belongs to the number. `0. SECONDS` expands to `0. *10`.
	proc/trailing_dot_number()
		return 0. *10

	// A doubled path separator collapses, so this is /datum/parsing/proc/helper.
	proc/helper()
		return "helped"

	proc/doubled_separator()
		return nameof(/datum/parsing/.proc/helper)

	// Weighted pick(): `weight;value`, semicolon-separated inside one argument.
	// Every weight here picks the same value, so the result is deterministic.
	proc/weighted_pick()
		return pick(20;"only", 5;"only", 1;"only")

	// A run of `;` and blank lines may sit between a body and its continuation
	// keyword - else, do's while, catch. A `\`-continued macro body forces the
	// idiom: with no lines to end statements, every braced branch ends `};`, so
	// /tg/station writes `if(a) { b; }; else { c; };` throughout. Worth 44
	// invented diagnostics there. Both branches are checked, because binding the
	// else to the wrong thing would still run one of them.
	proc/else_after_brace_semicolon(a)
		var/r = 0
		if(a) { r = 1; }; else { r = 2; };
		return r

	proc/else_after_inline_semicolon(a)
		var/r = 0
		if(a) r = 1; else r = 2;
		return r

	// The `;` may even sit on a line of its own between the two indented bodies.
	proc/else_after_semicolon_line(a)
		var/r = 0
		if(a)
			r = 1
		;
		else
			r = 2
		return r

	// The while after `};` closes the do - it is not a fresh loop over what
	// follows, which is what an unaware parser reads it as.
	proc/while_after_semicolon(a)
		var/r = 0
		do { r += 1; }; while(r < a)
		return r

	proc/while_after_inline_body(a)
		var/r = 0
		do r += 1; while(r < a)
		return r

	// A switch's arm list may be a brace block - the form a `\`-continued
	// macro body has to write, as tgstation's CONVERT_PH_TO_COLOR does. All
	// three answers differ, so a wrong dispatch shows as a wrong value.
	proc/brace_switch(pH)
		var/color = "none"
		switch(pH) { if(7 to 10) { color = "high" } if(2 to 7) { color = "mid" } else { color = "other" } }
		return color

	// A modifier word is a modifier only when a separator follows it. Without
	// one it is a variable NAMED final - /tg/station writes `var/final = ""` -
	// and the control declares x WITH the modifier in the same proc, so both
	// readings are exercised side by side.
	proc/modifier_word_as_name(a)
		var/final = a * 2
		var/static/x = 7
		return final + x

	// `locate(X) in container` is one grammatical unit, legal inside a ternary
	// branch where a bare `in` is rejected with "expected ':'". tgstation
	// writes `cond ? locate(X) in L : null` three times. The value is the
	// found object, which is what the istype proves.
	proc/locate_in_ternary(c)
		var/list/L = list(src)
		var/r = c ? locate(/datum/parsing) in L : "none"
		return istype(r, /datum/parsing) ? "found" : r

	// The catch must bind AND run: a negative argument indexes a null list, so
	// the -1 can only come from the caught exception. `e` is read to keep
	// unused_var quiet.
	proc/catch_after_semicolon(a)
		var/r = 0
		var/list/L = null
		try { r = a < 0 ? L[1] : a; }; catch(var/exception/e) { r = isnull(e) ? -2 : -1; };
		return r

// A statement keyword as a type-path segment. `throw` is the one real code
// uses - tgstation's /datum/manipulator_task/cargo/dropoff_base/throw - and
// the type must work end to end: declared, named in a local's type, and its
// member read back. The keyword is a SEGMENT only; ../errors/names covers
// `var/throw` being rejected.
/datum/parsing/throw
	var/marker = "thrown"

/proc/run_parsing()
	var/datum/parsing/P = new

	CHECK("labelled brace block", P.labelled_block(), 1)
	CHECK("label on its own line", P.label_own_line(), 2)
	CHECK("null-index in a hole, hit", P.null_index_in_a_hole("k"), "1 tail")
	CHECK("null-index in a hole, miss", P.null_index_in_a_hole("zz"), "0 tail")
	CHECK("in a to b, inside", P.range_membership(15), "mid")
	CHECK("in a to b, outside", P.range_membership(40), "out")
	CHECK("for in range with step -1", P.for_in_range(5), 10)
	CHECK("step as a variable name", P.step_is_a_name(4), 10)
	CHECK("trailing-dot number", P.trailing_dot_number(), 0)
	CHECK("doubled path separator", P.doubled_separator(), "helper")
	CHECK("weighted pick", P.weighted_pick(), "only")
	CHECK("}; else, then branch", P.else_after_brace_semicolon(1), 1)
	CHECK("}; else, else branch", P.else_after_brace_semicolon(0), 2)
	CHECK("x; else inline, then branch", P.else_after_inline_semicolon(1), 1)
	CHECK("x; else inline, else branch", P.else_after_inline_semicolon(0), 2)
	CHECK("; line before else", P.else_after_semicolon_line(0), 2)
	CHECK("}; while closes the do", P.while_after_semicolon(3), 3)
	CHECK("inline do body; while", P.while_after_inline_body(3), 3)
	CHECK("}; catch binds", P.catch_after_semicolon(5), 5)
	CHECK("}; catch runs", P.catch_after_semicolon(-3), -1)
	CHECK("brace switch, range arm", P.brace_switch(8), "high")
	CHECK("brace switch, second arm", P.brace_switch(3), "mid")
	CHECK("brace switch, else arm", P.brace_switch(0), "other")

	var/datum/parsing/throw/T = new
	CHECK("keyword as a type segment", T.marker, "thrown")
	CHECK("keyword type via istype", istype(T, /datum/parsing/throw), 1)
	CHECK("modifier word as a name", P.modifier_word_as_name(5), 17)
	CHECK("locate-in inside a ternary, hit", P.locate_in_ternary(1), "found")
	CHECK("locate-in inside a ternary, miss", P.locate_in_ternary(0), "none")
