// The legacy rand STATEMENT: `rand(...)` at statement start governs the ONE
// expression that follows - same line, next line whatever its indent, or
// indented - and dm.exe warns new_name on every one. Undocumented; probed
// 2026-08-16 on 516.1687. Read as an expression statement, the indented body
// was a silent stray block.
//
// The compiler's error on a non-expression body is recorded because it says
// what the body must be: `return 1` is "missing expression", `if(x)` is
// "invalid expression", and a second indented line is "invalid expression"
// on the FIRST body line (on the second when there are three - dm.exe's own
// inconsistency, and this fixture pins the two-line shape).
var/x

/proc/a()
	rand(50)
		x = 1
/proc/b()
	rand(50) x = 1
/proc/c()
	rand(50)
	x = 1
	x = 2
/proc/d()
	rand(50)
	return 2
/proc/e()
	rand(1, 2)
		x = 1
		x = 2
/proc/f()
	rand(50)
		if(x)
			x = 3
/proc/g()
	x = rand(50)
/proc/h()
	if(rand(50))
		x = 4
