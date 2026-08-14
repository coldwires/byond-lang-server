// dm.exe rejects a FOURTH for-clause under the DEFAULT grammar - including
// the C idiom `i++, j++`, where the comma separates a fourth clause rather
// than chaining statements. Probed as a matrix on 516.1686 and reported by
// us from the same matrix; the C-for half lives in pragma_syntax_for.

/proc/too_many()
	var/i = 0
	var/j = 0
	var/r = 0
	for(i = 0; i < 3; i++, j++)
		r++
	return r + i + j
