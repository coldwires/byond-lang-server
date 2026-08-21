// Language behaviours that are surprising, undocumented, or documented wrongly.
// Each is compiler-verified in PLAN.md §8 and written up in
// docs/dm-language-notes.md; here they are checked at RUNTIME, so a claim about
// what a construct *means* is tested rather than a claim that it parses.

/obj/item
	var/hp = 1

/obj/item/sword
	var/sharpness = 5

/datum/base
	proc/greet(name)
		return "base saw '[name]'"

/datum/child
	parent_type = /datum/base
	greet(name)
		return "child -> " + ..()

/obj/thing
	var/hp = 1
	var/label = "none"

/obj/marker

/proc/run_semantics()
	// `in` binds looser than assignment: this is (has = 2) in L, not has = (2 in L).
	var/list/L = list(1, 2, 3)
	var/has
	var/whole = (has = 2 in L)
	CHECK("in-precedence: has", has, 2)
	CHECK("in-precedence: whole", whole, 1)

	// ..() with empty parens forwards the CURRENT arguments, not none.
	var/datum/child/C = new
	CHECK("parent-call forwards args", C.greet("hello"), "child -> base saw 'hello'")

	// % truncates both operands to integers; %% is the fractional one.
	CHECK("integer modulo", 7.5 % 2, 1)
	CHECK("fractional modulo", 7.5 %% 2, 1.5)

	// Pointers (515+): *p is a valid assignment target.
	var/x = 5
	var/p = &x
	*p = 99
	CHECK("pointer write", x, 99)

	// A modified-type initialiser constructs with the vars set. Braces mandatory.
	var/obj/thing/T = new /obj/thing{hp = 42; label = "set"}
	CHECK("modified-type hp", T.hp, 42)
	CHECK("modified-type label", T.label, "set")

	// A bare `for` iterates world CONTENTS: anything in nullspace is invisible.
	new /obj/marker(locate(1, 1, 1))
	new /obj/marker(locate(2, 2, 1))
	new /obj/marker
	var/found = 0
	for(var/obj/marker/M)
		found++
	CHECK("bare for skips nullspace", found, 2)

	// `//` inside a path starts a comment, so the rest of the line is discarded.
	var/path = /obj//item
	CHECK("path comment wins", "[path]", "/obj")

	// Mid-path, `.` and `/` are the same token.
	CHECK_TRUE("mid-path dot equals slash", /obj/item/sword == /obj.item.sword)

	// Raw strings take any single character as the delimiter, escapes off.
	var/rx = @/(\d+)/
	CHECK("raw string", rx, "(\\d+)")

	// A backslash continues a string across lines; the break and indent vanish.
	var/joined = "one \
two"
	CHECK("string continuation", joined, "one two")

	// ** binds LEFT, and unary minus binds tighter than it.
	CHECK("exponent associativity", 2 ** 3 ** 2, 64)
	CHECK("unary minus before exponent", -2 ** 2, 4)

	// ?[] guards a NULL LIST, not an out-of-range index.
	var/list/N = null
	CHECK_TRUE("null-list index is null", isnull(N?[1]))
	var/list/A = list("a" = 1, "b" = 2)
	CHECK("assoc hit", A?["a"], 1)
	CHECK_TRUE("assoc miss is null", isnull(A?["zzz"]))

	// A `proc` block inside a `var` block declares NOTHING, silently.
	var/datum/swallowed/S = new
	CHECK("sibling var survives", S.kept, "kept")
	CHECK("discarded proc is absent", ("vanished" in S.vars) ? 1 : 0, 0)

	// The three parent_type forms a check for DM0406 must NOT report. Each is asserted by
	// inheriting a value rather than by compiling: a link the compiler accepted and did not
	// actually make would compile exactly as quietly.
	var/obj/pt_relative/rel = new
	CHECK("a relative parent_type inherits", rel.pt_marker, 7)
	CHECK("and its procs too", rel.pt_value(), 11)

	var/obj/pt_forward/fwd = new
	CHECK("a parent declared LATER in the file still links", fwd.pt_later_marker, 13)

	var/obj/pt_builtin/bi = new
	CHECK_TRUE("a builtin parent_type is a real link", istype(bi, /mob))

	// A bare `verb` BLOCK header declares VERBS, exactly as `verb/name()` does, and a `proc`
	// block declares procs. `dm.exe -o` prints a <verb> element for both verb forms, and at
	// runtime the difference is observable: a verb lands in `verbs` and a proc does not.
	// Checked by VALUE because our own tree recorded block-declared verbs as procs until
	// 2026-08-18, and nothing said so — the kind reaches the outline, completion, tree queries
	// and the `verb/` symbol filter.
	var/obj/kindcheck/kc = new
	CHECK("a verb block declares verbs, a proc block does not", kc.verbs.len, 2)

	var/block_verb_listed = 0
	for(var/V in kc.verbs)
		if(findtext("[V]", "block_verb"))
			block_verb_listed = 1

	CHECK("the block-declared one is among them", block_verb_listed, 1)

/datum/swallowed
	var
		kept = "kept"
		proc
			vanished()

// parent_type's legal forms, for the checks above. `.pt_base` is the upward search from this
// type's own path (notes §8), and `/obj/pt_later` is declared after the type naming it.
/obj/pt_base
	var/pt_marker = 7
	proc/pt_value()
		return 11

/obj/pt_relative
	parent_type = .pt_base

/obj/pt_forward
	parent_type = /obj/pt_later

/obj/pt_later
	var/pt_later_marker = 13

/obj/pt_builtin
	parent_type = /mob

// Both bare-block forms, for the kind check above. The segment form is there so the count
// distinguishes "verbs are collected" from "the block form is collected".
/obj/kindcheck
	verb
		block_verb()
			return 1

	proc
		block_proc()
			return 1

	verb/segment_verb()
		return 1
