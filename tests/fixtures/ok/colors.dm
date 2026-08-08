// How DM writes and reads a colour.
//
// These back `ColorService`, which draws a swatch beside every colour in a file.
// Two of the rules are not what an implementation would guess, and both produce a
// visibly wrong colour rather than an error - so they are asserted by VALUE here,
// where a BYOND release that changes one fails a build.

/proc/run_colors()
	// The documented return format, and the alpha form.
	CHECK("rgb is #rrggbb", rgb(255, 0, 128), "#ff0080")
	CHECK("rgb with alpha is #rrggbbaa", rgb(255, 0, 128, 64), "#ff008040")
	CHECK("rgb black", rgb(0, 0, 0), "#000000")
	CHECK("rgb white", rgb(255, 255, 255), "#ffffff")

	// COMPONENTS CLAMP at both ends rather than wrapping.
	CHECK("component above 255 clamps", rgb(300, -20, 0), "#ff0000")
	CHECK("component below 0 clamps", rgb(-1, -1, -1), "#000000")

	// A FRACTION TRUNCATES rather than rounding. 1.5 goes to 1, not 2 - the value
	// most likely to be written, and the one a rounding implementation gets wrong.
	CHECK("fractional components truncate", rgb(1.4, 1.5, 1.6), "#010101")

	// A SHORT FORM DUPLICATES each digit. #f08 is 255, 0, 136 - shifting left by
	// four would give 128, which is a different colour on screen.
	var/list/short = rgb2num("#f08")
	CHECK("short form red", short[1], 255)
	CHECK("short form green", short[2], 0)
	CHECK("short form blue duplicates the digit", short[3], 136)

	// Four digits is #RGBA, not a malformed #RRGG, and the alpha duplicates too.
	var/list/rgba = rgb2num("#ff00")
	CHECK("four-digit red", rgba[1], 255)
	CHECK("four-digit green", rgba[2], 255)
	CHECK("four-digit blue", rgba[3], 0)
	CHECK("four-digit alpha", rgba[4], 0)

	var/list/eight = rgb2num("#ff008040")
	CHECK("eight-digit blue", eight[3], 128)
	CHECK("eight-digit alpha", eight[4], 64)

	// A NAMED COLOUR is real DM. ColorService does not offer these yet, and this
	// check is what says the omission is a choice rather than an oversight.
	var/list/named = rgb2num("red")
	CHECK("a named colour resolves", named[1], 255)
	CHECK("a named colour is red", named[2], 0)

	// The colour spaces, whose constants are #defines in stddef.dm. All three of
	// these are the same red, which is why reading the arguments of a spaced call
	// as RGB would draw the wrong swatch.
	CHECK("HSL red", rgb(0, 100, 50, space = COLORSPACE_HSL), "#ff0000")
	CHECK("HSV red", rgb(0, 100, 100, space = COLORSPACE_HSV), "#ff0000")
	CHECK("named arguments take the first letter", rgb(h = 0, s = 100, l = 50, space = COLORSPACE_HSL), "#ff0000")
	CHECK("COLORSPACE_HSL is 2", COLORSPACE_HSL, 2)
