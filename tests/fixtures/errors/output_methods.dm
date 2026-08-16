// The reserved output methods: message(), link(), run(), ftp(). Legal only as
// the right side of `<<`; an error anywhere else; and not declarable as procs
// ("invalid proc name: reserved word" - a syntax error, so that half lives in
// its own unit, errors/output_method_name). Probed 2026-08-16 on 516.1687.
//
// message() is the second of dm.exe's three new_name messages and is in no
// reference, which is how `usr << message("hi")` - which compiles - was an
// invented "undefined proc" until this fixture existed.
var/x

/proc/a()
	usr << message("hi")            // new_name, whatever the receiver
/proc/b()
	world << message("hi", "two")   // and whatever the argument count
/proc/c()
	var/m = message("hi")           // an error, not a warning: no `<<`
	return m
/proc/d()
	link("http://x")                // same for the others
/proc/e()
	world << link("http://x")       // and silent here, since link is current
/proc/f()
	usr << run("file")
/proc/g()
	x = ftp("file")
