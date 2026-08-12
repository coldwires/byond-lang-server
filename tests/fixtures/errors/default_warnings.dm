// dm.exe's DEFAULT-ON warnings, on code that compiles with ZERO ERRORS.
//
// The point of the file: these fire on a clean build, so an editor silent about
// them is silent about something the build is telling the author. Each case is
// one line so a moved warning names itself.

// new_name (4005): a builtin that has been renamed. Sixteen candidate renames
// were compiled and only lentext warns - text2list and list2text are removed
// outright rather than deprecated.
/proc/uses_lentext(t)
	return lentext(t)

// no_parent (3013): a `proc/` NEW declaration has nothing above it to call.
/proc/global_orphan()
	return ..()

/datum/np
	proc/fresh()
		return ..()

// ...and a proc/ declaration whose name matches a builtin on an UNRELATED type
// still has no parent, because the question is this type's ancestry.
/datum/np_unrelated
	proc/Login()
		return ..()

// Silent by contrast: every override reaches something.
/datum/np/sub
	fresh()
		return ..()

/mob/Login()
	return ..()

/datum/np_twice
	proc/twice()
		return 1

/datum/np_twice/twice()
	return ..()
