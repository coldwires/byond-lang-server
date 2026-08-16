// The compiler echoes a `#warn` body back as a warning, at the DIRECTIVE'S OWN
// line. Reporting it needed per-file attribution for walk-time diagnostics:
// every preprocessor diagnostic was collapsed onto the .dme at line 0, so this
// one could never match no matter how it was worded.
//
// BOTH SPELLINGS ARE REAL. `#warning` is what warklan writes and dm.exe echoes
// it rather than rejecting an unknown directive; only `warn` was mapped here
// until 2026-08-12, so that whole family fell through as Unknown.
//
// The body is free text - §8: apostrophes and unbalanced quotes are legal in
// it - and the file still compiles, which is why this is a warning fixture
// rather than an error one.

#warn this is the short spelling

#warning this is the long spelling

// A `\`-continued body is joined - the break and the backslash removed - and
// echoed at the directive's LAST line. Read as a line end, the continuation was
// a code line at column 0 that declared a type. Probed 2026-08-16.
#warn (this one is continued \
onto a second line)

/proc/still_compiles()
	return 1
