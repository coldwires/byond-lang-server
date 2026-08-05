// The separator before `else` is REQUIRED. `if(a) r = 1; else r = 2` compiles
// (ok/parsing.dm) and this file differs only by the missing `;` - dm.exe
// rejects it, so the tolerance is for a separator run, not for else anywhere.
//
// Its own .dme: a syntax error stops dm.exe before anything else in the same
// compilation unit is checked.

/proc/e_unseparated_else(a)
	var/r = 0
	if(a) r = 1 else r = 2
	return r
