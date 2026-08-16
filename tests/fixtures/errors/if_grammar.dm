// The #if grammar is narrow: `%` is rejected (so are `<<`, `>>`, `&`, `|` and
// string literals). One operator per unit, since it is a preprocessor error;
// this unit carries `%`. Re-probed 2026-08-16 on 516.1687.
#define IF_FIXTURE_Y 5
#if IF_FIXTURE_Y % 2
/obj/never
#endif
