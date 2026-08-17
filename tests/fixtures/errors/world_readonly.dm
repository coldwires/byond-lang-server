// DM0408: /world vars that cannot be set at compile time at all. Twenty of
// them, found by the same run that produced DM0407 - assigning -1 to all 42 of
// /world's vars and grouping the answers.
//
// TWO MESSAGES, ONE RULE. dm.exe splits them: nine are `X: bad variable` and
// eleven are `X: may not be set at compile-time`. From an author's side both
// say the same thing, so they share an id and carry the compiler's own wording.
//
// VALUE-INDEPENDENT, which had to be probed rather than assumed: the -1 that
// found them proves nothing on its own, since -1 is nonsense for a port. With
// a sensible value - `port = 1234`, `time = 5` - each fails identically, so the
// var is the error and the value is not part of it.
//
// The controls are in ok/_harness.dm, which sets maxx/maxy/maxz/fps/tick_lag:
// none of those is in this table, and the fixture world compiles and runs.

// -- "bad variable": genuinely read-only ---------------------------------

/world/port = 1234
/world/byond_build = 500
/world/url = "x"
/world/timeofday = 5

// -- "may not be set at compile-time": runtime state ---------------------

/world/time = 5
/world/cpu = 1
/world/log = "x"
/world/host = "x"
