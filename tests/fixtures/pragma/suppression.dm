// Does #pragma ignore silence the two warnings we ship, by name and by number?
// And does a later `warn` bring one back, and does push/pop scope it?

#pragma ignore new_name
/proc/a_silenced_by_name(t)
	return lentext(t)

#pragma ignore 3013
/proc/b_silenced_by_number()
	return ..()

#pragma warn new_name
/proc/c_back_on(t)
	return lentext(t)

#pragma push
#pragma ignore new_name
/proc/d_ignored_inside_push(t)
	return lentext(t)
#pragma pop

/proc/e_after_pop(t)
	return lentext(t)
