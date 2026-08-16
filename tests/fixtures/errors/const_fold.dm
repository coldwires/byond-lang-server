// dm.exe FOLDS an initialiser that names a `const` var, through every scope.
//
// The discriminator is init_proc: it fires on a /turf var whose initialiser
// is not a compile-time constant, so under `#pragma warn init_proc` a silent
// line is one the compiler folded. The `list()` control MUST warn - it is
// what proves the pragma is live and the check is looking - and the `total`
// line in the .expected is what asserts every other var is silent. Probed
// 2026-08-16 on 516.1687; the values themselves run in ok/constants.dm.
#pragma warn init_proc

var/const/GLOBAL_MAX = 100
var/const/GLOBAL_HALF = GLOBAL_MAX / 2       // a const of a const, at root
var/const/STR_C = "ab"

/turf/probe
	var/const/TYPE_MAX = 40
	var/const/TYPE_TWICE = TYPE_MAX * 2      // a const of a const, on the type
	var/a_literal = 5                        // silent
	var/b_arith = 5 * 60                     // silent: literal arithmetic folds
	var/c_list = list()                      // the CONTROL: warns
	var/e_own_const = TYPE_MAX - 5           // own const
	var/f_global_const = GLOBAL_MAX + 1      // global const
	var/g_const_of_const = TYPE_TWICE + 1    // const whose initialiser named a const
	var/h_global_cc = GLOBAL_HALF + 1        // the same at root
	var/j_bare_const = TYPE_MAX              // a bare const, no arithmetic
	var/k_str_const = STR_C + "x"            // a string const

/turf/probe/child
	var/l_inherited = TYPE_MAX + 1           // a const reached through inheritance

/turf/probe/other
	var/m_static = /turf/probe/child::TYPE_MAX + 1   // the :: static form
