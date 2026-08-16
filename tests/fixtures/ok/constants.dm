// What DM itself computes and prints for the expressions our constant evaluator
// folds. Every value here was read off 516.1687 rather than reasoned about, and
// the point of running them is that DM's arithmetic is NOT C#'s: 32-bit floats,
// six significant digits, a truncating `%`, and a left-associative `**`.
//
// The evaluator asserts these same strings in ConstantEvaluatorTests. That is a
// unit test over our own code and is structurally blind to the compiler
// changing; this file is the half that is not, so a BYOND release that moves any
// of them fails a build rather than silently making every folded value wrong.

/datum/constants

	// Six significant digits, and scientific beyond them - so a large integer does
	// NOT round-trip through DM's own rendering. This is why a bare literal is
	// never folded: replacing what the author typed with 1.23457e+08 is true and
	// useless.
	proc/rendering()
		return "[1 / 3]:[123456789]:[2 ** 0.5]"

	// 32-bit floats, hidden by that same six-digit rendering. A double would print
	// 0.30000000000000004 here, and DM prints 0.3.
	proc/float_width()
		return "[0.1 + 0.2]:[1 / 10]"

	// `%` truncates BOTH operands to integers before dividing; `%%` is the
	// fractional one. Swapping them is a wrong number with nothing to say so.
	proc/modulo()
		return "[7.5 % 2]:[7.5 %% 2]"

	// `**` is LEFT-associative - 64, not 512 - and unary minus binds tighter than
	// it, so -2 ** 2 is 4 rather than -4. Both are the opposite of the C instinct.
	proc/exponent()
		return "[2 ** 3 ** 2]:[-2 ** 2]"

	// The rest of what folds: integer arithmetic, shifts, comparisons yielding
	// 1 or 0, and string concatenation.
	proc/ordinary()
		return "[5 + 1]:[5 * 60]:[1 << 10]:[5 > 3]:["a" + "b"]"

// A `const` var named in an initialiser folds too - the compiler's own folding,
// through every scope: the type's own const, an ancestor's, a global, a const of
// a const, a string const, and the /path::NAME static form. That it is FOLDED
// rather than evaluated at init is pinned by errors/const_fold under a live
// `#pragma warn init_proc`; this is the half that pins the values. A non-const
// name in the same position is "expected a constant expression" - errors/const_nonconst.
var/const/CONST_MAX = 100
var/const/CONST_HALF = CONST_MAX / 2
var/const/CONST_STR = "ab"

/datum/constants
	var/const/OWN_MAX = 40
	var/const/OWN_TWICE = OWN_MAX * 2
	var/own_const = OWN_MAX - 5
	var/global_const = CONST_MAX + 1
	var/const_of_const = OWN_TWICE + CONST_HALF
	var/bare_const = OWN_MAX
	var/str_const = CONST_STR + "x"

/datum/constants/child
	var/inherited_const = OWN_MAX + 1

// The static form is written from a SIBLING. Written from the ancestor
// (/datum/constants naming /datum/constants/child::OWN_MAX) it is "compile
// failed (possible infinite cross-reference loop)" - errors/const_static_loop.
/datum/constants/other
	var/static_form = /datum/constants/child::OWN_MAX + 1

/proc/run_constants()
	var/datum/constants/child/C = new
	var/datum/constants/other/O = new

	CHECK("six significant digits, scientific beyond", C.rendering(), "0.333333:1.23457e+08:1.41421")
	CHECK("numbers are 32-bit floats", C.float_width(), "0.3:0.1")
	CHECK("% truncates its operands, %% does not", C.modulo(), "1:1.5")
	CHECK("** binds left, unary minus binds tighter", C.exponent(), "64:4")
	CHECK("ordinary folding", C.ordinary(), "6:300:1024:1:ab")
	CHECK("a const by name folds, every scope", "[C.own_const]:[C.global_const]:[C.const_of_const]:[C.bare_const]:[C.str_const]:[O.static_form]:[C.inherited_const]", "35:101:130:40:abx:41:41")
