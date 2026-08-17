// DM0406, `X: invalid parent type`, probed as a matrix on 516.1687.
//
// THE ONE THAT WOULD HAVE BEEN GUESSED WRONG is (c): an UNDEFINED path in this
// slot is "invalid parent type", not the "undefined type path" every other
// expression position gets. Binding the initialiser as an ordinary expression
// reports the wrong message on the right line, which is why parent_type owns
// its whole slot in the binder.
//
// A CYCLE IS ONE ERROR however many types it runs through, and it lands on the
// participant declared FIRST in compile order - verified by writing the same
// cycle in both orders and again split across two files included both ways.
// Reporting at every participant would invent diagnostics dm.exe never emits.
//
// The controls are half the point. `parent_type` is ordinary DM, and a check
// that fired on a relative path, a forward reference or a builtin parent would
// light up most of a real game.

// -- rejected -----------------------------------------------------------

// a. not a path at all
/obj/number
	parent_type = 5

// b. an empty string
/obj/empty
	parent_type = ""

// c. a path no file declares - "invalid parent type", NOT "undefined type path"
/obj/undefined_target
	parent_type = /no/such/type

// d. a two-type cycle: ONE error, on the first of them
/obj/cycle_a
	parent_type = /obj/cycle_b
/obj/cycle_b
	parent_type = /obj/cycle_a

// e. a type that is its own parent
/obj/self
	parent_type = /obj/self

// f. a type parented to its own descendant, which closes the same loop by path
/obj/ancestor
	parent_type = /obj/ancestor/below
/obj/ancestor/below
	var/hp = 1

// -- controls, all legal DM ---------------------------------------------

// g. an ordinary absolute path
/obj/base
	var/marker = 1
/obj/ok_absolute
	parent_type = /obj/base

// h. a RELATIVE path, which searches upward from this type's own path
/obj/ok_relative
	parent_type = .base

// i. a FORWARD reference - the type is declared further down the file
/obj/ok_forward
	parent_type = /obj/declared_later
/obj/declared_later
	var/hp = 1

// j. a BUILTIN parent
/obj/ok_builtin
	parent_type = /mob

// k. a root-level global that happens to be named parent_type
var/parent_type = 5
