// `#pragma syntax C for` INSIDE A PROC BODY applies from its own line, and
// `#pragma pop` restores the default grammar from its own line. Both halves are
// compiler-verified here rather than reasoned about.
//
// The discriminator is what a COMMA means. By default commas separate the three
// clauses; under `C for` that is removed and comma becomes a statement chainer,
// so a comma-clause header is "malformed for statement" - which is the error
// this file records, at the line inside the body.
//
// WE DO NOT REPORT IT YET, and that is deliberate rather than unnoticed. We
// accept a mode-inappropriate header in BOTH directions - the default-grammar
// C form is "too many args" to dm.exe and silent from us too - so the missing
// piece is a for-header shape check, not pragma handling. This file is the
// target for whoever writes it.

/proc/mid_body_pragma()
	var/r = 0
	#pragma push
	#pragma syntax C for
	for(var/i = 0, i < 3, i++)
		r++
	#pragma pop
	return r

// After the pop the default grammar is back, so this one is fine. It is the
// control: without it, a file that rejected everything would look the same.
/proc/after_pop()
	var/r = 0
	for(var/i = 0, i < 3, i++)
		r++
	return r
