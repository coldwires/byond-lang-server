// What a leading `.` does NOT reach - the rejections behind the search rule the
// ok/ world asserts by value (ok/notes.dm). Semantic errors, so one unit holds
// all three; each is "undefined type path". Probed 2026-08-16 on 516.1687.
/a/inh
/a/inh/target
/b/thing
	parent_type = /a/inh
	// The anchor is the PATH, not the inheritance chain: /a/inh's child is not
	// visible through parent_type. (Nothing named target sits under /b or root.)
	var/p = .target
/x/sword/deep
/x/magic/thing
	// The whole path must resolve; a trailing segment nothing has is an error,
	// not a shrug.
	var/q = .sword/nonexistent
/c/only_here
/proc/f()
	// A global proc anchors at root and reaches only root's own children, so
	// /c/only_here is out of reach from here.
	return .only_here
