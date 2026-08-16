// The compile-only sections of docs/dm-language-notes.md that rested on their
// original 516.1666 probing with no fixture behind them, pinned 2026-08-16 so
// a BYOND release that changes any of them fails a build. Each behaviour is
// asserted by VALUE where a value exists; the rejections live in errors/
// (leading_dot, colon_path, indent_spaces, if_*, usr_type, lint_triggers).

// ---- the leading-`.` search: path ancestry, backtracking, nearest wins ------
/x/sword
	var/far_only = 1
/x/sword/deep
/x/magic/sword
	var/near_only = 1
/x/magic/thing
	// The nearer /x/magic/sword has no `deep`, so the search abandons it and
	// climbs to /x/sword/deep: the whole path must resolve, not the first segment.
	var/deep_path = .sword/deep
	// Among ancestors where the whole path resolves, the nearest wins.
	var/near_path = .sword
	parent_type = .sword
	proc/reads_near()
		return near_only
/rootlevel
/a/b/c
	// The walk includes root, from any depth.
	var/root_path = .rootlevel
/proc/global_reach()
	// A global proc anchors at root and reaches root's own children.
	return .rootlevel

// ---- braces and indentation nest freely, in either order --------------------
/obj/one {
	var
		a = 1
		b = 2
}
/obj/two {
	proc/f()
		return 1
}
/obj/three {
	sub
		var/c = 1
}

// ---- indentation is measured in columns, a tab and a space each one -----------
// So one space is the same level as one tab. " \t" and "\t " are one level
// DEEPER (errors/indent_deeper) and "    " is "inconsistent indentation"
// (errors/indent_spaces) - the language notes' original table had the first
// two as the same level, and writing this file is what disproved it. Each
// top-level declaration sets its own unit from its first indented line, so a
// four-space proc beside a tab-indented one compiles.
/datum/indents
	var/by_tab = 1
 var/one_space = 2
/datum/indents/four_space_unit
    var/a = 3
    proc/nested()
        return a

// ---- names may carry backslash escapes; they present, they are not read ------
/mob/escaped
	verb/\~escaped_name()
		set category = "Test"
		return 1

// ---- the two #pragma syntax grammars, by value --------------------------------
#pragma push
#pragma syntax C switch
/proc/c_switch(n)
	var/out = ""
	switch(n)
		case 1:
			out += "one "
		case 2:
			out += "two "
			break
		case 3:
			out += "three "
	return out
#pragma pop

#pragma push
#pragma syntax C for
/proc/c_for()
	var/i
	var/j
	var/k = 0
	for(i = 0, j = 100; i < 3; i++, j += 10)
		k += i
	return "[i]:[j]:[k]"
#pragma pop

// ---- 1#INF and 1#IND are number literals --------------------------------------
/proc/infinities()
	var/inf = 1#INF
	return "[inf > 1e30]:[-1#INF < -1e30]"

// ---- a directive line carries no indentation of its own -----------------------
#define NOTES_GUARD
/proc/guarded_body()
#ifdef NOTES_GUARD
	var/seen = "col0"
#endif
			#ifdef NOTES_GUARD
	seen += "+deep"
			#endif
	return seen

// ---- the #if grammar it does accept -------------------------------------------
#define NOTES_FIVE 5
#if NOTES_FIVE + 2 * 3 == 11 && defined(NOTES_FIVE) && !defined(NOTES_ABSENT) && (-1 < 0.5)
/proc/if_grammar()
	return "taken"
#else
/proc/if_grammar()
	return "not taken"
#endif

// ---- both root-var spellings declare a global -------------------------------
// `var/x` and `/var/x` at file scope both compile and both are ordinary globals.
// The difference is entirely in the ORACLE: `dm.exe -o` lists the first and
// OMITS the second, initialised or not, so tgstation's `/var/__rust_g` and the
// two dreamluau globals read as ours-only in a tree diff. The -o harness flags
// a root-var extra rather than filtering it, because our side cannot see which
// spelling was written — this case is what says the language does not care.
var/root_plain = 11
/var/root_slashed = 22

/proc/run_notes()
	var/x/magic/thing/T = new
	CHECK("leading . backtracks to the ancestor that resolves the whole path", "[T.deep_path]", "/x/sword/deep")
	CHECK("leading . takes the nearest complete match", "[T.near_path]", "/x/magic/sword")
	CHECK("parent_type = .sword binds to the nearer sword", T.reads_near(), 1)
	var/a/b/c/C = new
	CHECK("leading . reaches root from any depth", "[C.root_path]", "/rootlevel")
	CHECK("a global proc anchors its . at root", "[global_reach()]", "/rootlevel")

	var/obj/one/O1 = new
	var/obj/two/O2 = new
	var/obj/three/sub/O3 = new
	CHECK("indented var block inside a brace block", "[O1.a]:[O1.b]", "1:2")
	CHECK("indented proc body inside a brace block", O2.f(), 1)
	CHECK("indented subtype inside a brace block", O3.c, 1)

	var/datum/indents/four_space_unit/I = new
	CHECK("one tab and one space are the same level", "[I.by_tab]:[I.one_space]", "1:2")
	CHECK("a declaration sets its own unit; four spaces nest at eight", I.nested(), 3)

	var/mob/escaped/M = new
	CHECK("an escaped verb name declares one verb", length(M.verbs), 1)

	CHECK("C switch falls through to the break", c_switch(1), "one two ")
	CHECK("C switch stops at the break", c_switch(2), "two ")
	CHECK("C for chains clauses with commas", c_for(), "3:130:3")

	CHECK("1#INF is a number literal", infinities(), "1:1")
	CHECK("a directive at column 0 or three tabs opens nothing", guarded_body(), "col0+deep")
	CHECK("#if arithmetic, comparison, && and defined()", if_grammar(), "taken")

	// Written as well as read: a global the compiler folded away rather than
	// declared would take the read and refuse the write.
	root_plain += 1
	root_slashed += 1
	CHECK("both root var spellings declare a writable global", "[root_plain]:[root_slashed]", "12:23")
