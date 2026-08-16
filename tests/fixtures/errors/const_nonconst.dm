// A NON-const var named in a type-level initialiser is a compile ERROR, which
// is why the constant fold resolves a name only to a `const`. One case per
// unit: dm.exe stops at the first error. Probed 2026-08-16 on 516.1687.
var/plain = 7

/datum/holder
	var/from_plain = plain + 1
