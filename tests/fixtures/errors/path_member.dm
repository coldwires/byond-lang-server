// A path expression's final segment names a MEMBER only when the spelling matches how the
// declaration site was written, and the site has to belong to the type in front of it.
//
// Probed as a 38-case matrix on 516.1687 (PLAN.md §8). The row that pins it is a bare
// `/mob/Login()` override, after which `/mob/Login` resolves and `/mob/proc/Login` does not —
// so the two spellings are exclusive rather than one being a shorthand for the other.
//
// This file's rejections are the ones we report. Two rows dm.exe also rejects are deliberate
// MISSES and are absent here rather than recorded wrongly: the marker form on a type that only
// INHERITS the proc, and the marker form naming the wrong kind. Both are left lenient because
// `TYPE_PROC_REF` expands into `nameof()`, where the marker form DOES resolve through
// inheritance — checking the strict rule invented 89 diagnostics on /tg/station.

// ---- controls, all legal ------------------------------------------------------

obj/small
	verb
		get()
			return 1

// A bare override gives the subtype a site written without a marker, which is what makes
// mlaas's `verbs += /obj/small/trap/get` legal.
obj/small/trap
	get()
		return 2

/proc/control_bare_override()
	return /obj/small/trap/get

// The declaration itself is reachable through its own marker.
/proc/control_marker_form()
	return /obj/small/verb/get

// A path ending at the marker names the container, where the type declares one.
mob/admin
	proc
		help()
			return 1

/proc/control_container()
	return /mob/admin/proc

// Inside nameof() the marker form resolves through inheritance, unlike everywhere else — this
// is why the marker spelling is left lenient. `/obj/vault/inner.proc/unlock` in ordinary
// expression position is rejected; inside nameof it compiles.
obj/vault
	proc
		unlock()
			return 1

obj/vault/inner
	var/hp = 1

/proc/control_nameof_inherited()
	return nameof(/obj/vault/inner.proc/unlock)

// ---- rejections ---------------------------------------------------------------

obj/lever
	verb/pull()
		return 1

// Declared WITH the marker, so the bare spelling does not reach it.
/proc/bare_spelling_of_a_marker_declaration()
	return /obj/lever/pull

obj/crate
	var/hp = 1

// A var never satisfies a path's final segment.
/proc/a_var_is_not_a_path_member()
	return /obj/crate/hp

// The container resolves only where the type declares a proc of its own.
/proc/container_on_a_type_with_no_procs()
	return /obj/crate/proc
