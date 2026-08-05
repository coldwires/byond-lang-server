// Self-checking harness for the must-compile-and-run fixtures.
//
// Deliberately boring DM: no macros, no brace blocks, no contextual keywords.
// Everything in the other files is under test, so the harness must not be — a
// harness written in the constructs it is checking cannot report their failure.

// A map has to exist for locate(x,y,z) to return a turf; without one every
// object is in nullspace and the bare-for case silently measures nothing.
world
	maxx = 3
	maxy = 3
	maxz = 1

var/global/checks_total = 0
var/global/checks_failed = 0

/proc/CHECK(label, actual, expected)
	checks_total++

	if(actual == expected)
		return 1

	checks_failed++
	world.log << "FAIL [label]: got '[actual]', want '[expected]'"
	return 0

/proc/CHECK_TRUE(label, actual)
	return CHECK(label, actual ? 1 : 0, 1)

world/New()
	run_semantics()
	run_parsing()
	run_macros()

	world.log << "----"
	world.log << "checks [checks_total] failed [checks_failed]"

	if(checks_failed == 0)
		world.log << "RESULT OK"
	else
		world.log << "RESULT FAILED"

	del src
