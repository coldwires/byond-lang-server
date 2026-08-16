// A reserved output method cannot be a proc name, on a type any more than at
// root - and this is a SYNTAX error, so it stops the compiler and gets its own
// unit. Probed 2026-08-16 on 516.1687: message, link, run and ftp all say it.
/datum/d
	proc/message(t)
		return t
