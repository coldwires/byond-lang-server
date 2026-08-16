// `#if` rejects an undefined identifier rather than treating it as 0 - the
// opposite of C, and why real DM guards with #ifdef. A preprocessor error, one
// per unit. Re-probed 2026-08-16 on 516.1687.
#if NOT_DEFINED_ANYWHERE
/obj/never
#endif
