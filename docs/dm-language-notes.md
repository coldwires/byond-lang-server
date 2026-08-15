# DM syntax edge cases, with runnable proofs

Behaviours of the DreamMaker language that are surprising, undocumented, or documented wrongly.
Every claim here was established by compiling and — where the behaviour is observable at runtime —
running a case built so that the two candidate answers produce *different* output.

Originally established against **DM compiler 516.1666**, and the appendix has been re-run whole on
**516.1687**, the current build — 0 errors, 0 warnings, and the output block below it is that run,
identical line for line to the 516.1686 run before it. Results may differ on other versions; the
file at the end re-runs everything in about ten seconds.

The compile-only sections are not in the appendix and so are not covered by that run. Several of
them are pinned against 1687 by `tests/fixtures` instead — the keyword type names, the local-var
`in` rule, undefined members through `.`, and duplicate definitions all have fixtures the CI job
compiles with it. The rest still rest on their original 1666 testing, which is called out here
rather than papered over: a version claim is only as good as the run behind it.

Nothing here is taken from the DM Reference on trust. Where the reference and the compiler disagree,
that disagreement is called out.

---

## Running it

Save the file from [the appendix](#appendix-the-complete-test-file) as `edge_cases.dm`, put a
one-line `edge_cases.dme` beside it containing `#include "edge_cases.dm"`, then:

```
dm.exe edge_cases.dme
dreamdaemon.exe edge_cases.dmb -safe -invisible -once -logself
```

Output lands in `edge_cases.log`. `-safe` is deliberate: nothing in the file needs trusted
mode — pointers and `call()` both run under `-safe` — and a `-trusted` world waits on a GUI
approval prompt when there is no interactive session to click it, which reads as a silent hang.

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
searches upward through the code tree from the current position, and the search is more particular
than "first hit wins": it validates the whole remaining path, backtracks when a nearer candidate
does not carry it, and ignores `parent_type`. The exact rule, with the cases that pin it down, is
[further below](#compile-only-what-a-leading--actually-searches). A leading dot carries search
semantics a mid-path dot does not, and the two should not be conflated.

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

## 18. A `proc` block inside a `var` block declares nothing

Indent a `proc` header one level too far — into a `var` block — and everything under it is
silently discarded:

```dm
/datum/swallowed
	var
		kept = "this one survives"
		proc
			vanished()
```

```
kept=this one survives  in vars=no  call -> runtime: undefined proc or verb /datum/swallowed/vanished().
```

It compiles with **0 errors and 0 warnings**. `vanished` is then not a proc, not a var, and absent
from `vars` — it does not exist in any form, and calling it is a runtime error at the moment that
line is first reached. The sibling var beside it is unaffected, so nothing looks wrong.

Give those procs bodies and the file stops compiling, which is the clue to what is happening:

```dm
		proc
			vanished()
				return 1        // error: return1: missing =
```

Everything under the misplaced header is being read as **var declarations**, so `return 1` looks
like a variable called `return` that forgot its initialiser. The only reason the bodyless form
compiles is that a bare `name()` is accepted there and then dropped.

This is worth checking for in real code. It was found in a shipped game where four mission procs
were declared this way and one of them is called from another file — a runtime error waiting on a
code path, with nothing in the build output to suggest it.

## 19. A `;` run before `else`, `while` or `catch` is skipped

```dm
if(a) { r = 1; }; else { r = 2; };
do { r += 1; }; while(r < a)
try { r = a; }; catch(var/exception/e) { r = -1; };
```

All three compile and mean what the keywords suggest: the `else` binds to the `if`, the
`while` closes the `do`, the `catch` belongs to the `try`. Any run of semicolons and blank
lines may sit between a body and its continuation keyword — `};;`, `};` before a line break,
even a bare `;` on its own line between two indented bodies.

This is not an obscure corner. A `\`-continued macro body has no line breaks, so `;` is its
only statement terminator, and ending every braced branch with `};` is the natural way to
write one — /tg/station does it throughout. A parser that ends the `if` at the `;` then finds
an orphaned `else` and errors on code the compiler accepts; it cost 44 invented diagnostics
there. In the inline `do` form the failure is worse than a diagnostic: `do r += 1; while(r < a)`
read without this rule becomes a *fresh* `while` loop over whatever follows, which is a
misparse with no error at all.

Two boundaries pin the rule down:

| Written | Result |
|---|---|
| `if(a) r = 1; else r = 2` | compiles; both branches reachable |
| `if(a) r = 1 else r = 2` — no separator | **compile error**, "else: expected end of statement" |
| `r = 1; else r = 2` — no `if` | **compile error**, "'else' clause without preceding 'if' statement" |

So the tolerance is for a *separator run*, not for `else` anywhere: the keyword still needs a
`;` or a line break in front of it, and it still needs its statement.

## 20. A `for(x in L)` loop nulls `x` when it finishes, but not when you `break`

Declare the loop variable before the loop and read it afterwards, and it is empty:

```dm
var/list/L = list("a", "b", "c")
var/x
for(x in L)
	// ...
// x is null here, NOT "c"
```

```
normal exit : NULL      break exit : b      empty list : NULL
```

Three exits, because a single case cannot tell "nulled on the way out" apart from "nulled always":

| Loop ends by | `x` afterwards |
|---|---|
| the list running out | **null** |
| `break` | the element it stopped on — `"b"` |
| the list being **empty**, body never entered | **null**, overwriting whatever `x` held |

The last row is the one that pins it. With an empty list the body never runs, and a variable that
went in holding `"preset"` still comes out null — so the nulling belongs to the loop's termination
rather than to having iterated. The fetch that finds no next element is what writes null, and it
happens whether or not there was ever a first one.

The practical consequence: reading a loop variable after its loop is only safe when the loop cannot
finish normally. `for(x in L)` then `if(x)` is a bug wherever the list can run out, which is
usually. Code that wants the last element has to save it inside the body.

This is also why the inline form is the better habit — `for(var/y in L)` scopes `y` to the loop, so
there is no after-the-loop read to get wrong.

## 21. A short colour duplicates its digits, and `rgb()` truncates

Two rules, both of which produce a *wrong colour* rather than an error, so nothing complains.

```dm
rgb2num("#f08")     // [255, 0, 136]  - not 128
rgb(1.4, 1.5, 1.6)  // "#010101"      - not #010202
```

```
short form blue = 136    fractional components = #010101
```

**A short form repeats each digit.** `#f08` is `#ff0088`, so the blue channel is `0x88` = 136. The
tempting implementation shifts the nibble left by four, which gives 128 — close enough to look
right in a picker and wrong in every three-digit colour in the codebase. Four digits is `#RGBA`,
and the alpha repeats with the rest: `rgb2num("#ff00")` is `[255,255,0,0]`, a fully transparent
yellow rather than a malformed `#RRGG`.

**`rgb()` truncates a fraction and clamps a range.** `1.5` becomes 1, not 2 — which is the one
value most likely to be written, and the one where truncating and rounding disagree.
`rgb(300,-20,0)` is `#ff0000` and `rgb(-1,-1,-1)` is `#000000`, so both ends clamp rather than
wrapping.

A named colour is real: `rgb2num("red")` is `[255,0,0]`, and `color = "red"` reads back
`#ff0000`. So is a colour space — `rgb(0,100,50,space=COLORSPACE_HSL)` is `#ff0000`, as is the
named-argument form `rgb(h=0,s=100,l=50,space=COLORSPACE_HSL)`, since only a component's first
letter matters. The `COLORSPACE_*` values are `#define`s in `stddef.dm`: RGB 0, HSV 1, HSL 2,
HCY 3.

The practical consequence for a tool: an `rgb()` carrying a `space` argument is **not** an RGB
triple, and reading its arguments as one draws a red swatch beside a colour that is not red.

Runtime-verified on 516.1686. These live in `tests/fixtures/ok/colors.dm` rather than in the
appendix below, so a BYOND release that changes any of them fails a build.

---

## Compile-only: braces and indentation nest freely

DM has both `{ ... }` blocks and significant indentation, and the obvious assumption is that a brace
block turns indentation off inside it — the way a C-style block would. It does not. Indentation
keeps its full meaning inside braces, and the two forms nest in either order.

Three shapes, each written once with a brace body and once with indentation alone:

```dm
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
```

`dm.exe -o` prints the braced and the indented versions **identically**: `a` and `b` are vars on
`/obj/one`, `f` is a proc on `/obj/two`, and `/obj/three/sub` is a subtype carrying `c`.

```xml
<obj file="braces.dm:8">one
	<var file="braces.dm:10">a <val>1</val></var>
	<var file="braces.dm:11">b <val>2</val></var>
</obj>
<obj file="braces.dm:15">two
	<proc file="braces.dm:16">f</proc>
</obj>
<obj file="braces.dm:21">three
	<obj file="braces.dm:22">sub
		<var file="braces.dm:23">c <val>1</val></var>
	</obj>
</obj>
```

This has to be read off `-o` rather than from whether the file compiles, because every candidate
behaviour compiles: a tool that ignored the indentation inside the braces would produce a type with
no members and no error to show for it.

The practical consequence is for anything that consumes both. A lexer emits `Indent` and `Dedent`
inside the braces exactly as it does outside, so a parser that treats a brace block as a flat list
of `;`-separated members silently drops everything written across indented lines.

One more trap in the same area, since macro-generated code writes `{ ... };` runs on one logical
line: the `}` ends the declaration in front of it. Skipping "to the end of the line" past a `}`
runs into whatever follows the block, and the declarations after it are then read as members of the
braced type. That produces no diagnostic — the paths still resolve, so only an outline shows it.

The same freedom extends to a `switch`'s arm list:

```dm
switch(pH) { if(7 to 10) { c = "high" } if(2 to 7) { c = "mid" } else { c = "other" } }
```

compiles, and runtime-checked by value each arm dispatches correctly — ranges, the `else`, braces
opening on the header line or the next one, and indented arms inside the braces all behave exactly
as the indented form does. tgstation's `CONVERT_PH_TO_COLOR` macro is this shape, since a
`\`-continued body has no lines to indent.

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

### There is no local type inference at all

The obvious next question is whether the compiler reads the type off an initialiser. It does not.

```dm
/obj/item
	var/hp = 1

/proc/f()
	var/x = new /obj/item
	world << x.hp          // error: x.hp: undefined var
```

`hp` is the correct member of the type named two characters earlier on the previous line, and it
still fails. Only a **written** type is ever checked — `var/obj/item/x` compiles, `var/x` does not,
whatever it was initialised with.

Every route you might expect to carry a type was tested, and none of them do:

| Written | `x.member` |
|---|---|
| `var/obj/item/x = new` — declared type | **checked against /obj/item** |
| `var/x = new /obj/item` | error on every member |
| `var/x = new /obj/item()` | error on every member |
| `var/x = new /obj/item{hp = 2}` | error on every member |
| `var/x` then `x = new /obj/item` | error on every member |
| `var/x = a`, where `a` is a typed local | error on every member |
| `f(M as mob)` — parameter with an `as` clause | error on every member |
| `var/M = input("x") as mob` | error on every member |

So `as` is an input filter, not a type annotation, and `new` tells the compiler nothing about the
variable receiving it.

### `.` and `:` on an untyped receiver are checked differently

Untyped is not the same as unchecked. With `var/x` and no type anywhere:

| Written | Result |
|---|---|
| `x.hp` — a member that exists on some type | **error** |
| `x.nowhere_at_all` — a name on no type at all | **error** |
| `x:hp` — a member that exists on some type | compiles |
| `x:nowhere_at_all` — a name on no type at all | **error** |

`.` on an untyped var rejects everything. `:` on an untyped var asks a different question — *does
this name exist as a member of anything in the program?* — and accepts it if so. That is the widest
form of the check `:` performs, and it is still a check.

This is why the two operators cannot share a completion list even when the receiver is unknown: the
correct list after `x.` is empty, and after `x:` it is every member name in the program.

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

Note how this sits against the previous section, because the two look like the same situation and
are not. The degradation applies to the **expression form** — the member access is written directly
on the call or the index. Store that same value in an untyped var first and `.` goes back to
rejecting everything:

```dm
mk().elsewhere              // compiles - unchecked
var/x = mk()
x.elsewhere                 // error: x.elsewhere: undefined var
```

So "the type cannot be known" produces opposite answers depending on whether a variable is in the
way. A tool that models untyped-var access and call-result access with one rule will disagree with
the compiler on one of them.

This is why procs can declare `as` return types.

---

## Compile-only: what a leading `.` actually searches

The reference says a leading `.` starts "a relative path with an *upward* search" through the code
tree. That sentence has at least three readings, and only one of them is what the compiler does.

Each case below compiles or fails, so no runtime harness is needed.

**The anchor is the path, not the inheritance chain.** `parent_type` has no effect on it:

```dm
/a/inh
/a/inh/target
/b/thing
	parent_type = /a/inh
	var/p = .target        // error: .target: undefined type path
```

`target` is a child of this type's *parent type* and is still not found, because the search climbs
`/b/thing` → `/b` → `/` and never consults `parent_type`.

**The whole path has to resolve, and the search backtracks until it does.**

```dm
/x/sword/deep
/x/magic/sword           // nearer, but has no `deep`
/x/magic/thing
	var/p = .sword/deep    // compiles - resolves to /x/sword/deep
```

The nearer `sword` matches the first segment and is abandoned anyway, because `deep` is not under
it. So this is not "first matching segment wins". Trailing segments are genuinely checked —
`.sword/nonexistent` fails with *"undefined type path"* — so the compiler is searching, not
shrugging.

**Among ancestors that fully resolve, the nearest wins.** With both `/x/sword` and
`/x/magic/sword` complete, `.sword` inside `/x/magic` binds to the nearer one. Shown by pointing
`parent_type` at it and then reaching a var that exists on only one:

```dm
/x/sword
	var/far_only = 1
/x/magic/sword
	var/near_only = 1
/x/magic/thing
	parent_type = .sword
	proc/f()
		return near_only   // compiles
		return far_only    // error: far_only: undefined var
```

**The walk includes root**, so a root-level type is reachable from any depth. A **global proc
anchors at root** and therefore reaches only root's own children — `/b/target` is invisible to a
`.target` written in `/proc/f()`.

The rule in one line: *walk the enclosing type's path ancestors nearest-first, including root, and
take the first one under which the entire relative path resolves.* It works in type-level var
initialisers, in proc bodies, and as a `parent_type` value.

---

## Compile-only: thirteen statement keywords are legal type names

```dm
/datum/throw
	var/marker = 1

/proc/f()
	var/datum/throw/x = new
	return x.marker      // compiles - the type, the local and the member all resolve
```

`throw`, `set`, `step`, `if`, `else`, `for`, `while`, `switch`, `catch`, `try`, `do`, `spawn`
and `null` all work exactly like this — probed one keyword per compilation unit, and checked by
*using* the type rather than only declaring it, since a clean compile of the declaration alone
proves nothing.

The rest of the keyword vocabulary fails, in three different ways worth telling apart:

| Keyword | What happens |
|---|---|
| `in`, `to` | *"missing expression"* at the declaration |
| `as` | the declaration passes; the typed local breaks at the use |
| `return`, `break`, `continue`, `del`, `new`, `goto` | *"instruction not allowed here"* |
| `var`, `list`, `tmp`, `global`, `static`, `const`, `proc`, `verb` | read as modifiers or group markers; **no type exists**, and only the use says so |

And a keyword is a type *segment* only, never a variable name: `var/datum/throw/x` compiles while
`var/throw = 1` is *"missing left-hand argument to ="*.

The **modifier words go the other way**: every one of `final`, `const`, `tmp`, `global` and
`static` is a legal variable *name*, with uses, at proc level and type level alike. The word is a
modifier only when a separator follows it, and a block header only when the line ends there:

```dm
var/final = ""       // a var NAMED final - /tg/station writes this
var/final/x = 1      // x, carrying 516's final modifier
var/const            // heads a block of constants (the stddef.dm shape)
	NORTH = 1
```

Neither of these is hypothetical: /tg/station declares
`/datum/manipulator_task/cargo/dropoff_base/throw`, writes typed locals of it, and declares
`var/final = ""` five times.

---

## Compile-only: `#include` works in expression position

```dm
/proc/apiver()
	return new /datum/ver(
		#include "ver_num.dm"
	)
```

where `ver_num.dm` contains one line, `"5.11.0"`. Compiles clean, and the constructed object
carries the string — the file is spliced into the argument list at the include point. The tgs
module ships this shape in every /tg/station checkout (`ApiVersion()` +
`__interop_version.dm`), so any tool that parses per file meets it in the wild.

The directive still ends at its own physical line even mid-expression, and the reference never
mentions that `#include` is legal anywhere but declaration position.

`TRUE` and `FALSE` are also worth knowing as **built-in macros** (515+): with no define anywhere,
`#if TRUE` is taken and `#if FALSE` is silently not — despite `#if` rejecting other undefined
names outright — and the runtime values are 1 and 0.

---

## `in` inside a ternary branch, the `locate` exception, and where an earlier claim was wrong

The relational `in` does not parse inside a ternary's **true** branch, in any position:

| Written | Result |
|---|---|
| `c ? 9 in L : "no"` | **compile error**, *"expected ':'"* — as an initializer and as a statement alike |
| `c ? locate(/obj) in L : "no"` | compiles, **and runs**: the true branch yields the found object |

So `locate(X) in container` is its own grammatical unit rather than the loosest-binding `in`
operator wearing a hat — it binds to the locate, and it is welcome where the bare operator is
rejected. tgstation writes `cond ? locate(X) in L : null` three times.

The statement-level consequence is silent: in `x = locate(y) in L`, reading the `in` as the
relational operator gives `(x = locate(y)) in L` — assign first, test afterwards, per `in` binding
below `=` (§1) — which puts the wrong value in `x` with no diagnostic anywhere. The idiom's value
is the found object.

An earlier revision of this section also listed `c ? y : 9 in L` — the **false** branch — as a
compile error. That was measured only in a var initializer, and the ternary had nothing to do with
the rejection; the context did. The next section has the real rule.

## Compile-only: a local var's initializer rejects the relational `in`

Write a membership test as a proc-local var's initializer and the compiler refuses it, whatever
sits on the operator's left:

| Written | Result |
|---|---|
| `var/r = y in L` | **compile error**, *"unexpected 'in' expression"* |
| `var/r = (y) in L` | **compile error** — parenthesising the left side changes nothing |
| `var/r = c ? y : 9 in L` | **compile error** — a ternary left side changes nothing |
| `var/r = (y in L)` | compiles: parenthesise the **whole** test |
| `var/r = 2 in g()`, `2 in typesof(/obj)`, `2 in (L)`, `2 in world` | **compile error** — the RHS does not rescue it either |
| `r = y in L` — plain assignment statement | compiles |
| `var/gv = 2 in list(1,2)` — a **global** | compiles |
| `/datum/d` + `var/x = 2 in list(1,2)` — **type-level** | compiles |
| `for(var/x = start in L)` | the header's own form, untouched |

Only the proc-local declaration refuses it. Three initializer forms are exempt, and each is a
different grammar rather than the operator:

- **`locate(X) in L`** — the locate unit above. `var/found = locate(/obj/item) in L` compiles.
- **`input(...) [as null|anything] in choices`** — the input idiom's choice list. mlaas writes it
  eight times as a local initializer, `as` clause and all, in a project that compiles clean.
- **`= x in list(...)`** — a literal `list(...)` on the right, and this one is a trap. It is the
  declaration's **value-restriction clause**, the same grammar as a verb argument's
  `as num in list(...)` — not the membership operator. Runtime-verified:
  `var/r = 2 in list(4,5)` compiles clean and leaves `r` holding **2**, the left value, member or
  not. A local written this way almost always meant the test; tgstation ships exactly one
  (`var/needs_turf = task_type in list(...)`), and its `needs_turf` holds the task type, not the
  answer. Write `(x in list(...))` for the test.

In statement position the loosest-binding rule from §1 takes over instead, and the results are
runtime-verified: `r = c ? y : 9 in L` parses as `(r = c ? y : 9) in L` — assign the branch
value, then test and discard — so with `c = 0` the var holds 9, which neither reading of the `in`
as an operator could produce. In an `if` condition there is no assignment in the way, and
`if(c ? y : 9 in L)` tests `(c ? y : 9) in L`, confirmed by the branch not being taken when the
selected value is absent from the list.

---

## Compile-only: a var's declared type is resolved at the use site, not the declaration

"It compiles" is not the same as "it means what you think". This one only shows up if you go on to
*use* the thing you declared.

```dm
mob
	var
		clothing/slot
```

Compiles with **0 errors and 0 warnings**, and `slot` genuinely exists — `dm.exe -o` lists it. But
`/clothing` was never declared, and the moment anything touches the var:

```dm
mob
	var
		clothing/slot
	proc/f()
		slot = null        // error, on THIS line: slot: undefined type: /clothing
```

The error lands on line 5, the use, not on line 3 where the type was written. So a var with a
misspelled or deleted type sits in a clean build until someone reads or writes it, at which point
the error points at the reader rather than at the declaration. Declare `/clothing` and both compile.

The same holds for any number of type segments: `var/a/b/c/slot` is silent until used, then reports
`undefined type: /a/b/c`.

This is the same shape as the discarded-proc trap in §18 — a declaration the compiler accepts and
then quietly cannot honour — and it is worth a warning for the same reason.

### And no, the separators do not create types

The obvious suspicion about `mob/var/clothing/feet` is that `/` overrides the block structure and
declares something called `clothing` or `/clothing/feet`. It does not. The object tree for the whole
file is:

```xml
<dm>
	<mob file="c.dm:1">mob
		<var file="c.dm:3">feet</var>
	</mob>
</dm>
```

One var, named `feet`, on `/mob`. Nothing named `clothing` appears anywhere in the tree, and all
three candidate types are rejected as paths:

| Probe | Result |
|---|---|
| `var/p = /clothing` | `/clothing: undefined type path` |
| `var/p = /clothing/feet` | `/clothing/feet: undefined type path` |
| `var/p = /mob/clothing` | `/mob/clothing: undefined type path` |

The one-line form `mob/var/clothing/feet` and the indented `var` block form produce byte-identical
trees, so the separator really is interchangeable with the indentation here.

### `-o` and `-code_tree` answer different questions

Worth knowing before trusting either as an oracle:

- **`-o`** is the **resolved object tree** — what exists after the compiler has merged everything.
  It lists types, vars and constant values, and it is the right check for "did this become a type or
  a var". It does **not** record a var's declared type, so it cannot confirm that `feet` is a
  `/clothing`; only that `feet` is a var on `/mob`.
- **`-code_tree`** is the **syntactic** tree, printed before resolution. It shows
  `mob / var / clothing / feet` as literal nested nodes, exactly as written. That makes it an oracle
  for a *parser* rather than for a type tree — it will happily show you nesting that resolution then
  discards.

A bare override shows up in `-o` as a second entry rather than replacing the first:

```xml
<obj>item
	<var>hp <val>1</val></var>
	<var>hp <val>3</val></var>
</obj>
```

---

## The two lint warnings, and what actually triggers them

There are at least three warning names, and they do not all behave the same way. **`unused_var` is
on by default** — `var/unused = 1` reports `warning (unused_var): unused: variable defined but not
used` with no flags at all. The two below ship **off**, which is what makes them easy to mistake for
the whole vocabulary.

The level also **flows through include order rather than resetting per file**. With
`#pragma ignore unused_var` in the first file a `.dme` includes, an offending var in the second file
is silent; swap the two `#include` lines and it warns. Pragma level is sequential state, like the
macro table.

`init_proc` and `frequent_call` ship **off**. `dm.exe -warn init_proc,frequent_call game.dme` turns
them on, and the name prints inline so you can read the vocabulary off a build log:

```
lint.dm:2:warning (init_proc): stuff: var will be initialized in a hidden init proc; ...
lint.dm:3:warning (frequent_call): New: this proc will be called very frequently
```

Both are narrower than their names suggest, and the restriction has an odd shape that two obvious
guesses both get wrong. The trigger set is identical for the two warnings:

| Type | Warns |
|---|---|
| `/datum`, `/atom`, `/turf` — the exact types | yes |
| `/turf/sub`, `/turf/a/b/c` — any turf subtype, any depth | **yes** |
| `/datum/sub`, `/atom/sub` | **no** |
| `/atom/movable`, `/obj`, `/obj/sub`, `/mob`, `/area`, `/client`, `/image` | no |

So it is **`/datum`, `/atom` and `/turf` exactly, plus the entire `/turf` subtree** — the union of
two rules, not either one alone. "Three exact types" fails to predict `/turf/sub`; "`/turf` and
below" fails to predict bare `/datum`. Inheritance is not it either: `/obj` descends from `/atom`
and stays silent.

That union matches the intent. A var or `New()` on `/datum` or `/atom` *exactly* is inherited by
every object in the game; and the map instantiates turfs per tile for every turf subtype, so the
whole branch is hot. Everything else is created on demand.

The practical consequence: **neither lint fires on `/obj/item` or `/mob/living`**, which is where a
large codebase's per-instance cost actually lives. As a codebase-wide audit they are far weaker than
the names suggest.

`init_proc` is about **whether the initialiser needs runtime evaluation**, not about lists:

| Initialiser on a `/turf` | Warns |
|---|---|
| `= list(1,2,3)`, `= list()`, `= new /obj`, `= newlist(/obj)` | yes |
| `= 5`, `= "text"`, `= 1+2`, `= /obj`, `= null`, no initialiser, anything `const` | no |

A constant is folded into the type; anything else needs a hidden per-instance init proc.

`frequent_call` covers `New()` and `Del()` only. A plain `proc/whatever()` and an override of
`Enter()` both stay silent.

### The pragma beats the command-line flag

Verified in both directions, which settles which one a tool should treat as authoritative:

| Written | Result |
|---|---|
| `-warn init_proc` + `#pragma ignore init_proc` | silent |
| `-ignore init_proc` + `#pragma warn init_proc` | warns |
| `#pragma warn init_proc` alone, no flag | warns |
| `-error init_proc`, or `#pragma error init_proc` | a real error, printed as `error (name):` |

So the flag sets the starting level and the pragma overrides it from that point in the file.
`#pragma push` / `#pragma pop` scope it — with `push`, `ignore`, one declaration, `pop`, that
declaration is silent and the next one warns.

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
guards (§17); that a `proc` block misplaced inside a `var` block is discarded without a warning
(§18); that indentation keeps its meaning inside a brace block, so the two nest freely; that a run
of semicolons and blank lines may separate a body from its `else`, `while` or `catch` (§19); the
infinity and indeterminate literals `1#INF` and `1#IND`, which appear in shipped library code and
which a lexer splitting on `#` will read as a number, a directive, and a name.

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

// ---- 18. a `proc` block inside a `var` block declares nothing ----------
/datum/swallowed
	var
		kept = "this one survives"
		proc
			vanished()

/proc/t_proc_in_var()
	var/datum/swallowed/S = new
	var/in_vars = ("vanished" in S.vars) ? "yes" : "no"
	var/called = "?"
	try
		call(S, "vanished")()
		called = "ran"
	catch(var/exception/e)
		called = "runtime: [e.name]"
	return "kept=[S.kept]  in vars=[in_vars]  call -> [called]"

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

// ---- 19. a `;` run before else / while / catch is skipped ----------------
/proc/t_separator_runs()
	var/r = 0
	if(r) { r = 10; }; else { r = 1; };
	do r += 1; while(r < 3)
	var/list/N = null
	var/caught = "no"
	try { r += N[1]; }; catch(var/exception/e) { caught = isnull(e) ? "null" : "yes"; };
	return "r=[r] caught=[caught]"

// ---- 20. for-in nulls its loop variable on normal termination -----------
/proc/t_for_exit_value()
	var/list/L = list("a", "b", "c")
	var/x
	for(x in L)
		continue
	var/after_normal = isnull(x) ? "NULL" : x

	var/y
	for(y in L)
		if(y == "b")
			break
	var/after_break = isnull(y) ? "NULL" : y

	var/list/E = list()
	var/z = "preset"
	for(z in E)
		continue
	var/after_empty = isnull(z) ? "NULL" : z

	return "normal=[after_normal] break=[after_break] empty=[after_empty]"

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
	world.log << "15 conditional :   : [t_conditional_colon()]"
	world.log << "16 directive indent: [t_directive_indent()]"
	world.log << "17 null index      : [t_null_index()]"
	world.log << "18 proc in var     : [t_proc_in_var()]"
	world.log << "19 separator runs  : [t_separator_runs()]"
	world.log << "20 for exit value  : [t_for_exit_value()]"
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
18 proc in var     : kept=this one survives  in vars=no  call -> runtime: undefined proc or verb /datum/swallowed/vanished().
19 separator runs  : r=3 caught=yes
20 for exit value  : normal=NULL break=b empty=NULL
```

The file compiles with 0 errors and 0 warnings, and the run above is its actual output.

One caveat if you edit the file: do not put a `\~` — or any other escaped punctuation, such as
`\:` — inside a DM string literal. In string context a backslash begins a text macro, and both
fail with *"undefined text macro or escape sequence"*. The escape is legal in a **name**, not in
a string. An earlier revision of this appendix had exactly that bug: a `\:` in a log label meant
the printed file did not compile, while the claim above said it did.
