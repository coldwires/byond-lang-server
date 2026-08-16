// A space then a tab is TWO columns to dm.exe - one level deeper than a
// one-tab sibling - so the var nests under the var above it, which is the
// same "empty type name" a nested var block draws. The language notes had
// this as the same level, on 516.1666 probing that does not reproduce on
// 1686 or 1687. One case per unit: this is a syntax error.
/datum/d
	var/a = 1
 	var/b = 2
