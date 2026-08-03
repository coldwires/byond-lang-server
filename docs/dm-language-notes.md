# DM syntax edge cases, with runnable proofs

Behaviours of the DreamMaker language that are surprising, undocumented, or documented wrongly.
Every claim here was established by compiling and — where the behaviour is observable at runtime —
running a case built so that the two candidate answers produce *different* output.

Tested against **DM compiler 516.1666**. Results may differ on other versions; the file at the end
re-runs everything in about ten seconds.

Nothing here is taken from the DM Reference on trust. Where the reference and the compiler disagree,
that disagreement is called out.

---

## Running it

Save the file from [the appendix](#appendix-the-complete-test-file) as `edge_cases.dm`, put a
one-line `edge_cases.dme` beside it containing `#include "edge_cases.dm"`, then:

```
dm.exe edge_cases.dme
dreamdaemon.exe edge_cases.dmb -trusted -invisible -once -logself
```

Output lands in `edge_cases.log`.

---

## 1. `in` binds looser than assignment

The lowest-precedence operator in the language sits **below** `=`.

```dm
var/list/L = list(1,2,3)
var/has
var/whole = (has = 2 in L)
```

```
has=2  whole=1   (parses as (has = 2) in L)
```

`has` ends up holding `2`, not a boolean. The expression parsed as `(has = 2) in L`, and *that*
evaluated to 1 because the value 2 is in the list — so the bug produces a plausible-looking result.

The same trap catches negation: `if(!A in L)` is `if((!A) in L)`. Write `if(!(A in L))`.

## 2. `..()` with empty parens forwards the current arguments

```dm
/datum/base
	proc/greet(name)
		return "base saw '[name]'"

/datum/child
	parent_type = /datum/base
	greet(name)
		return "child -> " + ..()
```

```
child -> base saw 'hello'
```

The empty parentheses do not mean "no arguments". They mean "the arguments I was called with".
To genuinely pass nothing, you have to pass something explicit.

Note also that "parent" means the previous override in the chain, not strictly the parent type — if
a type overrides the same proc twice, `..()` reaches the earlier override first.

## 3. `%` truncates to integers; `%%` is the fractional modulo

```dm
7.5 % 2     // 1
7.5 %% 2    // 1.5
```

`%` truncates **both** operands to integers before dividing. `%%` (515+) is the one that behaves
like `fmod`. If a modulo has ever given a surprising answer on non-integers, this is why.

## 4. DM has pointers

```dm
var/x = 5
var/p = &x
*p = 99
// x is now 99
```

`&` takes a reference, `*` dereferences, and `*p` is a valid assignment target. Available on object
vars, proc locals (including `src`, `usr` and the `.` var), arguments, globals, and list items.

Watch the precedence: **unary** `*` and `&` bind tightly (level 4), while **binary** `*` and `&` are
at levels 6 and 11.

## 5. Modified-type initialisers

```dm
var/obj/thing/T = new /obj/thing{hp = 42; label = "set"}
```

```
hp=42 label=set
```

Legal anywhere a type value is. The braces are **mandatory** here, even though braces are optional
elsewhere in DM, and `;` separates entries written on one line.

## 6. A bare `for` iterates world *contents*, not all instances

```dm
new /obj/marker(locate(1,1,1))
new /obj/marker(locate(2,2,1))
new /obj/marker                  // loc = null

var/n = 0
for(var/obj/marker/M)
	n++
```

```
2 of 3 found; the loc=null one is invisible
```

`for(var/obj/M)` with no `in` clause is exactly `for(var/obj/M in world)`. Anything in nullspace —
pooled, dormant, or simply constructed without a location — is invisible to it. If you are using a
bare `for` to sweep every instance of a type, it is not doing that.

## 7. `//` inside a path starts a comment

```dm
var/p = /obj//item
```

```
/obj//item evaluates to /obj
```

The `//` is a comment, so the rest of the line is discarded and you get a perfectly valid path that
is not the one you wrote. Comment detection beats path separation.

## 8. Mid-path, `.` and `/` are the same token

```dm
/obj/item/sword == /obj.item.sword    // 1
```

Identical values, not merely equivalent. They can be mixed inside one path.

This holds **mid-path only**. A *leading* `.` is a different thing entirely — a relative path that
searches upward through the code tree from the current position, first hit wins. So a leading dot
carries search semantics a mid-path dot does not, and the two should not be conflated.

## 9. Raw strings take any delimiter

```dm
var/a = @/(\d+)/
var/b = @#has "quotes" inside#
```

```
@/../ -> (\d+)    @#..# -> has "quotes" inside
```

Three forms, all with escapes and `[...]` interpolation disabled:

| Form | Line breaks |
|---|---|
| `@X…X` — **any single character** as the delimiter | no |
| `@{"…"}` | yes |
| `@(XYZ)…XYZ` — arbitrary multi-character terminator | yes |

The single-character form matters most: `@/(\d+)/` is a *string*, not a division. Any tool that
assumes `@` is always followed by `"` will silently mis-parse a regex.

## 10. A backslash continues a string across lines

```dm
var/s = "one \
two"
```

```
one two
```

The line break and the next line's leading whitespace are both discarded. Common in long
description text.

One consequence worth knowing: this is a **string** continuation. Getting the byte handling wrong
around CRLF here produces "unterminated string" errors that only appear on Windows-authored files.

## 11. Names may contain backslash escapes

```dm
/mob/verb/\~escaped_name()
	set category = "Test"
	usr << "called"
```

Compiles. The escape works on verbs, procs **and** vars, leading or mid-name, and the escaped
character can be anything — `\the`, `\a`, even `\1`. These control how a name presents to players.

A bare `\~` in *expression* position is rejected, so this is a declaration-name feature only.

**The DM Reference never documents this.** The string `\~` does not appear anywhere in `info.html`.

## 12. `#pragma syntax C switch` changes semantics, not just spelling

```dm
#pragma push
#pragma syntax C switch

/proc/t_c_switch(n)
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
```

```
n=1 -> one two        (fell through case 1 into case 2, stopped at break)
n=2 -> two
```

Fall-through is real, and `break` is what stops it. In DM's default `switch`, each `if` block exits
on its own and fall-through is impossible — so turning this pragma on changes the behaviour of any
`switch` that relied on implicit exit.

Without the pragma, `case 1:` fails with *"expected var or proc name after `:` operator"* — the
compiler reads `case` as an identifier and `:` as member access. It is a genuinely different
grammar, not an alias.

`#pragma push` / `#pragma pop` scope it correctly: test 13 in the appendix uses DM `switch` syntax
after the pop and compiles.

## 13. `#pragma syntax C for` swaps what the comma means

This one is widely misunderstood, because semicolons work **without** the pragma.

| Header | Default | `#pragma syntax C for` |
|---|---|---|
| `for(i=0, i<3, i++)` — comma clauses | accepted | **rejected** — "malformed for statement" |
| `for(i=0; i<3; i++)` — semicolon clauses | accepted | accepted |
| `for(i=0,j=0; i<3; i++,j+=1)` — comma *chaining* | **rejected** — "too many args" | accepted |

The pragma does not enable semicolons; they already work. What it does is **swap the comma's
meaning** — removing comma-as-clause-separator and adding comma-as-statement-chainer. It is
subtractive as well as additive, so enabling it breaks every existing comma-separated `for` in that
file.

Runtime-confirmed that chained clauses both execute:

```dm
#pragma syntax C for
for(i=0, j=100; i<3; i++, j+=10)
// ends at i=3 j=130
```

## 14. `**` binds left, and unary minus binds tighter than it

```dm
2 ** 3 ** 2    // 64
-2 ** 2        // 4
```

```
2 ** 3 ** 2 = 64    -2 ** 2 = 4
```

Left-associative, so it is `(2**3)**2` and not the `2**(3**2)` that most languages with an exponent
operator give you. And the unary minus is applied first, so `-2 ** 2` is `(-2)**2` rather than
`-(2**2)`. Both follow from the reference's precedence table — unary sits one level tighter than
`**` — but both are the opposite of what C or Python instincts suggest.

## 15. A conditional's `:` needs whitespace before it

This is the only place in DM where **spacing changes a parse**.

Declare `c` on a datum so that `b:c` is a valid member access, then write a conditional whose false
branch is also named `c`:

```dm
/datum/holder
	var/c = "MEMBER"

var/datum/holder/b = new
var/c = "LOCAL"
var/r = 1 ? b : c
```

```
conditional (b), not member access
```

Now vary only the whitespace around that colon:

| Written | Result |
|---|---|
| `1 ? b : c` | conditional |
| `1 ? b :c` | conditional |
| `1 ? b:c` | **compile error** — "expected `:`" |
| `1 ? b: c` | **compile error** — "expected `:`" |

Only the space *before* the colon matters. Without one, `b:c` is taken as member access and the
conditional is left with no separator, which is what the error is complaining about — it is not
complaining about the branch.

But whitespace is only half of it. Vary what sits *before* the colon instead, keeping it tight:

| Written | Result |
|---|---|
| `1 ? b:c` — after a bare name | **compile error** |
| `1 ? "0":"1"` — after a string | conditional |
| `1 ? f():g()` — after `)` | conditional |
| `1 ? L[1]:z` — after `]` | conditional |
| `1 ? 1:2` — after a number | conditional |
| `1 ? (y):z` — after a parenthesised group | conditional |

So the rule has two halves: a tight colon is member access **only when it directly follows a bare
identifier**, which is the one position where `a:b` is a member access to begin with. Everywhere
else the colon closes the conditional however it is spaced.

The practical consequence: `x = cond ? a:b` does not mean what it looks like, and the error message
points at the wrong thing, while the visually identical `x = cond ? "a":"b"` is fine. Any tool that
lexes `:` uniformly will disagree with the compiler on one of those two.

## 16. A preprocessor directive carries no indentation of its own

```dm
/proc/guarded()
	#ifdef POOFING
	var
		seen = "the guarded block parsed and ran"
	#endif
	return seen
```

```
the guarded block parsed and ran
```

The directive sits between the proc header and its body without opening or closing anything. Inside
a one-tab body, `#ifdef` written at column 0, at one tab, and at three tabs all compile identically —
the line's indentation is simply not part of the block structure.

This matters more than it looks for anything that tracks indentation. A directive line emits no
indent, so a tool that expects the body to start on the very next line will miss the block entirely
and read the body as though it were top-level code.

A bare `;` at file scope is legal too, for much the same reason — it is an empty declaration and
carries no structure. Real code leaves them behind when the statement they terminated is commented
out.

## 17. `?[]` guards a null list, not an out-of-range index

The spelling is `L?[i]`, with the `?` **outside** the bracket. `L[?i]` does not compile on
516.1666 — it fails with *"i: missing comma ',' or right-paren ')'"*.

It is easy to find it described as shorthand for a bounds check:

```dm
x = L?.len >= i ? L[i] : null    // NOT what it does
```

That is wrong, and the difference shows up exactly where it matters. `?[]` reuses the `?.` logic,
so it is `isnull(L) ? null : L[i]` — a guard on the **list**, not on the index:

| `L = list("a","b","c")` | `L?[i]` | `L?.len >= i ? L[i] : null` |
|---|---|---|
| `i = 1` | `a` | `a` |
| `i = 3` | `c` | `c` |
| `i = 4` — past the end | **runtime error**, list index out of bounds | `null` |
| `i = 0` | runtime error | runtime error |
| `L` is null | `null` | `null` |

```
L?[4] -> runtime: list index out of bounds   null-list?[1] -> null   long form -> null
```

So it removes the *"cannot read from list"* error you get from indexing a null list, and nothing
else. An out-of-range numeric index still crashes, which is what makes the shorthand description
dangerous — it reads as a bounds check and is not one.

Where it does pay off is associative lists, because a missing key is already null there:

| `A = list("a" = 1, "b" = 2)` | result |
|---|---|
| `A?["a"]` | `1` |
| `A?["zzz"]` — missing key | `null` |
| `A?["a"]` where `A` is null | `null` |
| `A?[1]` — numeric index on an assoc list | `a`, the **key**, not the value |
| `A?[9]` | runtime error |

For an assoc lookup, then, `?[]` makes the read null-safe end to end: a missing key was already
null, and the `?` covers the null list. For a numeric index into a plain list it buys much less
than it appears to. BYOND's maintainer describes it the same way — it hijacks the `?.` operator's
logic, and there is no bounds checking on the index.

---

## Compile-only: `.` versus `:` — neither is unchecked

These cannot go in the runtime file because the failing cases do not compile. Each needs its own
file.

Declare a property on a **subtype** of the receiver's declared type, then try to reach it:

```dm
/mob/test
	var/hp = 1
/mob/test/special
	var/on_subtype = 5

/proc/f()
	var/mob/test/M = new
	return M.on_subtype     // or M:on_subtype
```

| Expression | Property lives on | Result |
|---|---|---|
| `M.prop` | the declared type | compiles |
| `M.prop` | a **subtype** of the declared type | **compile error** |
| `M:prop` | a **subtype** of the declared type | compiles |
| `M:prop` | an **unrelated** type | **compile error** |

So `.` checks the declared type only, and `:` widens the check to the declared type *and its
subtypes*. Describing `:` as "unchecked" is wrong — it is a wider check, not an absent one.

### `.` degrades to `:` when the type cannot be inferred

```dm
/mob/test
	var/hp = 1
/datum/other
	var/elsewhere = 5

/proc/f()
	var/list/L = list(new /mob/test)
	return L[1].elsewhere       // compiles
```

`L[1].elsewhere` and `make().elsewhere` both compile against a property that exists only on an
unrelated type, because a list lookup and a proc call have no known type to check against. The
compile-time guarantee silently disappears exactly where it would be most useful.

This is why procs can declare `as` return types.

---

## Where the DM Reference is wrong

The reference is authoritative for *inventories* — the operator list, the precedence table, the `as`
types, the `set` names. Those cannot be discovered by testing. It is much less reliable on
behaviour.

**Documents something that does not work.** `/operator/path/:` describes a leading `:` as a
*downward* path search: `mob = :player` as shorthand for `/mob/player`. Every form was rejected with
`:player: undefined type path` — in a proc local, a typed var initialiser, a type-level var, and
inside the `/mob` branch itself. An absolute-path control compiled in the same harness. Treat it as
removed.

**Two errors in the operator documentation.**
- The precedence table lists `-=` twice in the assignment row.
- The overload table maps `A -= B` to `A.operator--(B)`; it should be `operator-=`.

**Silent on several real behaviours.** None of the following appear anywhere in `info.html`:
backslash escapes in names (§11); `//` inside a block comment hiding both `/*` and `*/`; a backslash
continuing a `//` comment; the "inconsistent indentation" error, or any indentation specification at
all; hexadecimal and scientific number literal syntax; the whitespace rule on a conditional's `:`
(§15); that a directive line carries no indentation of its own (§16); what `?[]` actually
guards (§17); the infinity and
indeterminate literals `1#INF` and `1#IND`, which appear in shipped library code and which a lexer
splitting on `#` will read as a number, a directive, and a name.

The precedence table also cannot express §15, since that distinction is lexical rather than a matter
of binding strength. Reading the table alone will not tell you that `cond ? a:b` fails to compile.

**Nesting it does get right:** "Multi-line comments may be nested" is documented and true.

---

## Indentation, since nothing documents it

Measured against a sibling declared at one tab:

| Indentation | Result |
|---|---|
| `"\t"` | same level |
| `" \t"` — space then tab | same level |
| `"\t "` — tab then space | same level |
| `" "` — one space | same level |
| `"    "` — four spaces | **rejected**, "inconsistent indentation" |

Prefix comparison does not explain this, since neither `"\t"` nor `" \t"` is a prefix of the other.
The simplest model consistent with every accepted case: **depth is the leading tab count, falling
back to the space count when there are no tabs.**

The compiler has an "inconsistent indentation" diagnostic that the reference never mentions.

---

## Appendix: the complete test file

Save as `edge_cases.dm`, with `edge_cases.dme` containing `#include "edge_cases.dm"`.

```dm
world
	maxx = 3
	maxy = 3
	maxz = 1

// ---- 1. `in` binds looser than assignment -------------------------------
/proc/t_in_precedence()
	var/list/L = list(1,2,3)
	var/has
	var/whole = (has = 2 in L)
	return "has=[has]  whole=[whole]   (parses as (has = 2) in L)"

// ---- 2. ..() with empty parens forwards current arguments ---------------
/datum/base
	proc/greet(name)
		return "base saw '[name]'"
/datum/child
	parent_type = /datum/base
	greet(name)
		return "child -> " + ..()

// ---- 3. % truncates to integers, %% is fractional -----------------------
/proc/t_modulo()
	return "7.5 % 2 = [7.5 % 2]    7.5 %% 2 = [7.5 %% 2]"

// ---- 4. pointers (515+) -------------------------------------------------
/proc/t_pointers()
	var/x = 5
	var/p = &x
	*p = 99
	return "x = [x]  (mutated through a pointer)"

// ---- 5. modified-type initializer --------------------------------------
/obj/thing
	var/hp = 1
	var/label = "none"
/proc/t_modified_type()
	var/obj/thing/T = new /obj/thing{hp = 42; label = "set"}
	return "hp=[T.hp] label=[T.label]"

// ---- 6. a bare `for` iterates world CONTENTS ---------------------------
/obj/marker
/proc/t_bare_for()
	new /obj/marker(locate(1,1,1))
	new /obj/marker(locate(2,2,1))
	new /obj/marker                  // loc = null -> nullspace
	var/n = 0
	for(var/obj/marker/M)
		n++
	return "[n] of 3 found; the loc=null one is invisible"

// ---- 7. `//` inside a path starts a comment ----------------------------
/obj/item
/proc/t_path_comment()
	var/p = /obj//item
	return "/obj//item evaluates to [p]"

// ---- 8. mid-path `.` is the same as `/` --------------------------------
/obj/item/sword
/proc/t_dot_path()
	return "(/obj/item/sword == /obj.item.sword) -> [/obj/item/sword == /obj.item.sword]"

// ---- 9. raw strings: any single char is the delimiter ------------------
/proc/t_raw_strings()
	var/a = @/(\d+)/
	var/b = @#has "quotes" inside#
	return "@/../ -> [a]    @#..# -> [b]"

// ---- 10. a backslash continues a string across lines -------------------
/proc/t_string_continuation()
	var/s = "one \
two"
	return "[s]"

// ---- 11. names may contain \ escapes -----------------------------------
/mob/verb/\~escaped_name()
	set category = "Test"
	usr << "called"
/proc/t_escaped_name()
	return "a verb whose name begins with a backslash-tilde compiled"

// ---- 12. pragma-scoped C switch, with fall-through ---------------------
#pragma push
#pragma syntax C switch
/proc/t_c_switch(n)
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

// ---- 13. after the pop, DM switch syntax works again -------------------
/proc/t_dm_switch(n)
	switch(n)
		if(1)
			return "one"
		if(2,3)
			return "a few"
		else
			return "many"

// ---- 14. `**` binds left, and unary minus binds tighter ----------------
/proc/t_exponent()
	return "2 ** 3 ** 2 = [2 ** 3 ** 2]    -2 ** 2 = [-2 ** 2]"

// ---- 15. a conditional's `:` needs whitespace before it ----------------
/datum/holder
	var/c = "MEMBER"
/proc/t_conditional_colon()
	var/datum/holder/b = new
	var/c = "LOCAL"
	var/r = 1 ? b : c
	return istype(r, /datum/holder) ? "conditional (b), not member access" : "member access (got [r], local c is [c])"

// ---- 16. a directive carries no indentation of its own -----------------
#define POOFING
/proc/t_directive_indent()
	#ifdef POOFING
	var
		seen = "the guarded block parsed and ran"
	#endif
	return seen

// A bare `;` at file scope is legal. This line is the proof.
;

// ---- 17. `?[]` guards a null list, not an out-of-range index -----------
/proc/t_null_index()
	var/list/L = list("a","b","c")
	var/list/N = null
	var/oob = "?"
	try
		var/v = L?[4]
		oob = isnull(v) ? "null" : "[v]"
	catch(var/exception/e)
		oob = "runtime: [e.name]"
	var/guarded = N?[1]
	var/longform = N?.len >= 4 ? N[4] : null
	return "L?\[4\] -> [oob]   null-list?\[1\] -> [isnull(guarded) ? "null" : "value"]   long form -> [isnull(longform) ? "null" : "value"]"

world/New()
	var/datum/child/C = new
	world.log << " 1 in-precedence   : [t_in_precedence()]"
	world.log << " 2 ..() forwarding : [C.greet("hello")]"
	world.log << " 3 modulo          : [t_modulo()]"
	world.log << " 4 pointers        : [t_pointers()]"
	world.log << " 5 modified type   : [t_modified_type()]"
	world.log << " 6 bare for        : [t_bare_for()]"
	world.log << " 7 path comment    : [t_path_comment()]"
	world.log << " 8 dot path        : [t_dot_path()]"
	world.log << " 9 raw strings     : [t_raw_strings()]"
	world.log << "10 continuation    : [t_string_continuation()]"
	world.log << "11 escaped name    : [t_escaped_name()]"
	world.log << "12 C switch n=1    : [t_c_switch(1)]"
	world.log << "12 C switch n=2    : [t_c_switch(2)]"
	world.log << "13 DM switch n=3   : [t_dm_switch(3)]"
	world.log << "14 exponent        : [t_exponent()]"
	world.log << "15 conditional \:   : [t_conditional_colon()]"
	world.log << "16 directive indent: [t_directive_indent()]"
	world.log << "17 null index      : [t_null_index()]"
	del src
```

### Expected output

```
 1 in-precedence   : has=2  whole=1   (parses as (has = 2) in L)
 2 ..() forwarding : child -> base saw 'hello'
 3 modulo          : 7.5 % 2 = 1    7.5 %% 2 = 1.5
 4 pointers        : x = 99  (mutated through a pointer)
 5 modified type   : hp=42 label=set
 6 bare for        : 2 of 3 found; the loc=null one is invisible
 7 path comment    : /obj//item evaluates to /obj
 8 dot path        : (/obj/item/sword == /obj.item.sword) -> 1
 9 raw strings     : @/../ -> (\d+)    @#..# -> has "quotes" inside
10 continuation    : one two
11 escaped name    : a verb whose name begins with a backslash-tilde compiled
12 C switch n=1    : one two
12 C switch n=2    : two
13 DM switch n=3   : a few
14 exponent        : 2 ** 3 ** 2 = 64    -2 ** 2 = 4
15 conditional :   : conditional (b), not member access
16 directive indent: the guarded block parsed and ran
17 null index      : L?[4] -> runtime: list index out of bounds   null-list?[1] -> null   long form -> null
```

The file compiles with 0 errors and 0 warnings, and the run above is its actual output.

One caveat if you edit the file: do not put a `\~` inside a DM string literal. In string context a
backslash begins a text macro, and `\~escaped_name` fails with *"undefined text macro or escape
sequence"*. The escape is legal in a **name**, not in a string.
