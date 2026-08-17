// DM0407, `X: out of bounds`, probed on 516.1687 by assigning -1 to all 42 of
// /world's vars and reading what came back. FIVE answer "out of bounds":
// maxx, maxy, maxz, fps and tick_lag. The other 37 fall into five families
// this check deliberately does not implement - "bad text", "expected 1 or 0",
// "expected 0, 1, or 2", "bad turf|area|mob", "may not be set at compile-time"
// and "bad variable" - each of which needs its own matrix (PLAN §8).
//
// THE COMPILER FOLDS BEFORE IT CHECKS: `(1 - 5)` is out of bounds, and it names
// the value as an EMPTY string there because no single token holds it. We name
// the author's text instead, which is more use to a reader and which diagdiff
// does not compare.
//
// fps IS THE ONLY ONE WITH A CEILING - 100 compiles and 101 does not, bisected.
// maxx takes a billion and tick_lag a million.
//
// EACH VAR APPEARS ONCE, because a second assignment to the same one is a
// duplicate definition and would measure that instead. The legal side - zero, a
// fraction, the boundary value - is in ok/_harness.dm, where it compiles AND
// runs on every fixture run.

// a. below zero
/world/maxx = -5

// b. a string where a number belongs - "out of bounds", not "bad text"
/world/maxy = "abc"

// c. a list
/world/maxz = list(1)

// d. past the only ceiling there is
/world/fps = 1000

// e. folded first, then checked
/world/tick_lag = (1 - 5)
