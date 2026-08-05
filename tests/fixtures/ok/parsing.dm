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
