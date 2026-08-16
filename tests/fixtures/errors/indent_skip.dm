// Unit 1 (a tab), then a body at three tabs: level 3 with level 2 skipped,
// which is "inconsistent indentation" - a nested line must be exactly one
// unit deeper. Probed 2026-08-16.
var/x
/proc/f()
	if(1)
			x = 1
