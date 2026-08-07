/*
 * dm_core.h - C ABI for the DM analysis library.
 *
 * Source of truth for the native boundary. Keep in sync with src/Dm.Native/Exports.cs
 * and src/Dm.Native/DmStatus.cs.
 *
 * Conventions:
 *   - All strings crossing this boundary are null-terminated UTF-8.
 *   - Strings returned by this library are owned by the caller and freed with dm_free().
 *     Strings passed in are owned by the caller and are copied before the call returns.
 *   - Functions returning dm_status write DM_OK on success. Out-parameters are cleared
 *     before any work, so they are safe to read even if the status is ignored.
 *   - On failure, dm_last_error() returns a message for the calling thread.
 *   - Handles are opaque. A closed handle stays invalid; it is never recycled into a
 *     different object. Closing twice is a no-op.
 *   - One workspace is used from one thread at a time. Not enforced.
 */

#ifndef DM_CORE_H
#define DM_CORE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* -- versioning ---------------------------------------------------------- */

/*
 * Packed as (major << 16) | minor. Check this at startup before anything else.
 * Additive changes bump minor; a client built against an older minor keeps working.
 * Breaking changes bump major; a client must not proceed on a major mismatch.
 */
#define DM_ABI_VERSION_MAJOR(v) (((v) >> 16) & 0xFFFF)
#define DM_ABI_VERSION_MINOR(v) ((v) & 0xFFFF)

#define DM_ABI_EXPECTED_MAJOR 0

int32_t dm_abi_version(void);

/* -- status codes -------------------------------------------------------- */

typedef int32_t dm_status;

#define DM_OK                0
#define DM_ERR_INVALID_ARG   1
#define DM_ERR_INVALID_HANDLE 2
#define DM_ERR_NOT_FOUND     3
#define DM_ERR_OUT_OF_MEMORY 4
#define DM_ERR_INTERNAL      5

/* Message for the last failure on the calling thread, or NULL. Caller frees. */
char *dm_last_error(void);

/* -- memory -------------------------------------------------------------- */

/* Frees any pointer this library returned. NULL is accepted. */
void dm_free(void *ptr);

/* -- workspace ----------------------------------------------------------- */

/*
 * An opened DM project, rooted at a .dme file.
 *
 * Opening validates the .dme exists and records its directory; it parses nothing. The
 * include graph is walked and the object tree built on the first call that needs them,
 * then cached until a buffer changes.
 */
typedef void *dm_workspace;

dm_status dm_workspace_open(const char *dme_path, dm_workspace *out_workspace);

/*
 * Opens a workspace with NO .dme, rooted at a directory. Added in ABI 0.20.
 *
 * For a host with no project to point at: a single file, a folder with no .dme
 * in it, a scratch buffer. There is no include walk, so there is no project
 * tree beyond the BYOND builtins - but every file still resolves against the
 * builtins PLUS ITSELF, so a lone .dm gets its own declarations, completion
 * and hover rather than nothing. dm_file_in_project answers 0 for everything.
 *
 * Prefer dm_workspace_open when you have a .dme: a real project resolves
 * across files, and this cannot.
 */
dm_status dm_workspace_open_standalone(const char *root_directory,
                                       dm_workspace *out_workspace);

/* Safe on an invalid or already-closed handle. */
void dm_workspace_close(dm_workspace workspace);

/* Absolute path to the directory containing the .dme. Caller frees. */
dm_status dm_workspace_root(dm_workspace workspace, char **out_root);

/* -- documents ----------------------------------------------------------- */

/*
 * Records the text the client currently has open for a file.
 *
 * Once set, this text is the only source for that path until dm_close_buffer; disk is
 * never consulted for it. That is what makes editor-side line-ending normalisation
 * harmless -- the analyzer sees exactly what the editor displays.
 *
 * `content` is UTF-8 and is copied before the call returns. Pass its length in bytes;
 * a negative length means it is null-terminated. Prefer passing the length: it avoids a
 * scan, and DM source may legitimately contain a NUL inside a string literal.
 *
 * `file` may be absolute, or relative to the directory containing the .dme.
 */
dm_status dm_set_buffer(dm_workspace workspace, const char *file,
                        const char *content, int32_t length);

/* Drops a client buffer. Later reads for that path fall back to disk. */
dm_status dm_close_buffer(dm_workspace workspace, const char *file);

/*
 * Drops every derived answer, so the next question rebuilds against what is on
 * disk now. Added in ABI 0.14.
 *
 * Call it when files change OUTSIDE the editor - a git checkout, a branch
 * switch, another program saving. Cheap by construction: the per-file caches
 * revalidate against write time and length during the rebuild, so only files
 * that actually changed are re-read, re-walked and re-parsed. Pushed buffers
 * stay authoritative.
 */
dm_status dm_invalidate(dm_workspace workspace);

/*
 * Whether the object tree exists right now. Added in ABI 0.15.
 *
 * Returns 1 when a tree is built, 0 when the next tree-backed query will pay
 * for a build, -1 when the handle is invalid. Costs nothing to ask, so poll it
 * freely - it is how a client shows "indexing" instead of a frozen UI, and it
 * turns to 0 whenever a buffer, define or invalidation drops the tree.
 */
int32_t dm_tree_ready(dm_workspace workspace);

/*
 * Builds the object tree now, blocking until it exists. Added in ABI 0.15.
 *
 * The warm-at-open call: pay the cold cost at a moment of your choosing - a
 * splash screen, a background thread at startup - instead of on the user's
 * first completion. A warm tree makes this a no-op, so calling it defensively
 * is free. Threading contract unchanged: one workspace, one thread at a time.
 */
dm_status dm_build_tree(dm_workspace workspace);

/* -- build configuration ------------------------------------------------- */

/*
 * Defines macros for the project, exactly as dm.exe's -D switch does. Added in ABI 0.5.
 *
 * PASS WHAT THE PROJECT'S BUILD PASSES. The flags decide which #ifdef branches exist,
 * so a workspace opened without them describes a different program from the one the
 * build produces - code behind a guard is invisible, or visible when it should not be.
 * /tg/station, for instance, builds with -DCBT.
 *
 * Each entry uses the compiler's own spelling: "NAME", "NAME=value", or the
 * function-like "FN(x)=((x)*2)". A bare "NAME" defines it EMPTY rather than to 1,
 * matching dm.exe.
 *
 * Separate from dm_workspace_open because the object tree is built lazily: calling this
 * straight after opening still applies to the first query, and build flags can be
 * changed later without reopening. Passing NULL or count 0 clears them. The strings are
 * copied before the call returns.
 *
 * Changing defines invalidates the cached tree, so the next query rebuilds.
 */
dm_status dm_set_defines(dm_workspace workspace, const char *const *defines, int32_t count);

/* -- position encoding --------------------------------------------------- */

/*
 * How offsets and lengths are measured. Never assumed; always passed explicitly,
 * because LSP and native clients disagree.
 *
 * Qt's QString and .NET's string are both UTF-16, so a client holding either wants
 * DM_ENCODING_UTF16. A client holding raw bytes wants DM_ENCODING_UTF8. For pure ASCII
 * the two are identical, which is exactly why a mismatch survives testing and then
 * misplaces spans the first time someone types a non-ASCII character.
 */
typedef int32_t dm_position_encoding;

#define DM_ENCODING_UTF8  0
#define DM_ENCODING_UTF16 1

/* -- classification ------------------------------------------------------ */

/*
 * Colouring categories. These are a stable numeric contract; values are never reused.
 *
 * DM_CLASS_TYPE_NAME onward are the semantic kinds, and they ARE produced now. They only
 * ever refine a span the lexical pass already made - none is added, removed or moved - so
 * a client that ignores them sees exactly the pre-0.7 output.
 *
 * DM_CLASS_TYPE_NAME needs the object tree, and classification will not build one: that is
 * a whole-project walk and this runs on every scroll. Type names stay plain identifiers
 * until something else has built a tree (dm_complete_at, dm_document_symbols), then light
 * up. Call dm_complete_at once after opening if you want them immediately.
 */
#define DM_CLASS_NONE                    0
#define DM_CLASS_COMMENT                 1
#define DM_CLASS_KEYWORD                 2
#define DM_CLASS_IDENTIFIER              3
#define DM_CLASS_NUMBER                  4
#define DM_CLASS_STRING                  5
#define DM_CLASS_INTERPOLATION_DELIMITER 6
#define DM_CLASS_RESOURCE                7
#define DM_CLASS_OPERATOR                8
#define DM_CLASS_PUNCTUATION             9
#define DM_CLASS_PREPROCESSOR            10
#define DM_CLASS_ERROR                   11
#define DM_CLASS_TYPE_NAME               12  /* a path segment naming a real type   */
#define DM_CLASS_PROC_NAME               13  /* a name followed by (                */
#define DM_CLASS_VAR_NAME                14  /* a member read with no call parens   */
#define DM_CLASS_MACRO_NAME              15  /* a name the project #defines         */

typedef void *dm_classification;

/*
 * Classifies an inclusive range of lines. Line numbers are zero-based and clamp.
 *
 * The whole file is lexed and cached; only the requested range is returned. Lexing just
 * the visible range would be wrong, because a {" "} string or a nested block comment can
 * begin thousands of lines earlier and decides whether the range is code or text.
 */
dm_status dm_classify_range(dm_workspace workspace, const char *file,
                            int32_t start_line, int32_t end_line,
                            dm_position_encoding encoding,
                            dm_classification *out_classification);

/* Number of spans. Returns -1 for an invalid handle. */
int32_t dm_classification_count(dm_classification classification);

/*
 * Pointer to 3 * count consecutive int32 values: offset, length, kind. Valid until
 * dm_classification_free. Returns NULL for an invalid handle.
 *
 * One contiguous block rather than per-span accessors, because this is called on every
 * scroll and every keystroke.
 */
const int32_t *dm_classification_data(dm_classification classification);

void dm_classification_free(dm_classification classification);

/* -- document symbols ---------------------------------------------------- */

/*
 * The file's outline plus its syntax diagnostics, as a UTF-8 JSON document.
 * Added in ABI 0.3.
 *
 * You own the buffer. Release it with dm_free.
 *
 * Serialized rather than handle-based because symbols carry names, which a packed
 * int32 block cannot express without a string table on both sides. An outline is
 * rebuilt per edit, not per scroll, so this is the cheaper trade here - unlike
 * dm_classify_range, which is on the paint path.
 *
 * Shape:
 *
 *   {
 *     "symbols": [
 *       {
 *         "name": "item",
 *         "detail": "/obj/item",     annotation for the outline; may be ""
 *         "kind": 0,                 see dm_symbol_kind below
 *         "startLine": 0, "startChar": 5,    whole declaration, members included
 *         "endLine": 3,   "endChar": 0,
 *         "selStartLine": 0, "selStartChar": 5,   the NAME alone
 *         "selEndLine": 0,   "selEndChar": 9,
 *         "children": [ ... ]
 *       }
 *     ],
 *     "diagnostics": [
 *       { "id": "DM0200", "severity": "error",
 *         "message": "expected a declaration",
 *         "startLine": 4, "startChar": 0, "endLine": 4, "endChar": 3 }
 *     ]
 *   }
 *
 * "severity" is "error", "warning", "information" or "hint" - a word rather than a
 * number, because LSP numbers these from 1 and our own enum from 0, and shipping
 * either integer invites a client to decode it with the other scheme's table.
 * Added in ABI 0.10; treat a missing key as "error", which is what every
 * diagnostic was before it. Handle an unknown word as "information".
 *
 * Not every diagnostic here is a syntax error. DM0300 marks code that COMPILES
 * CLEAN and does not mean what it looks like - a proc block indented into a var
 * block, which dm.exe accepts and then declares nothing for. Those declarations
 * are absent from "symbols" for the same reason: the compiler does not create
 * them either.
 *
 * Use the sel* range to highlight or navigate: it covers the name, so clicking an
 * outline entry puts the caret on the identifier rather than on the whole block.
 *
 * Lines and characters are ZERO-BASED, and characters follow the encoding you pass,
 * exactly as in dm_classify_range.
 */
dm_status dm_document_symbols(dm_workspace workspace, const char *file,
                              dm_position_encoding encoding, char **out_json);

/* -- completion ---------------------------------------------------------- */

/*
 * What can be typed at a position, as a UTF-8 JSON document. Added in ABI 0.4.
 *
 * You own the buffer. Release it with dm_free.
 *
 * Line and character are ZERO-BASED and follow the encoding you pass, as everywhere
 * else. Point them at the caret: the word being typed is not the trigger, whatever
 * precedes it is, so "mob.he" completes against /mob just like "mob." does.
 *
 * Shape:
 *
 *   {
 *     "context": "Member",
 *     "truncated": false,
 *     "items": [
 *       { "name": "loc", "detail": "/atom", "kind": 1, "builtin": true,
 *         "inferred": false, "type": "/atom", "value": "", "documentation": "" }
 *     ]
 *   }
 *
 * context tells you why the list is what it is:
 *   "Identifier"      a bare word: locals, parameters, src members, globals
 *   "Member"          after `.` - the declared type and what it inherits
 *   "SubtypeMember"   after `:` - the above PLUS members declared on subtypes
 *   "TypePath"        after `/` - type paths
 *   "ReturnValue"     a bare leading `.` - DM's return-value variable, not member
 *                     access. The list is EMPTY; show nothing. Added in 0.14, and
 *                     it exists so you do not have to guess what a user who just
 *                     typed `.` on a fresh line meant.
 *   "None"            nothing useful here
 *
 * `.` and `:` differ on purpose and both are checked. `:` widens the check to the
 * subtype tree rather than removing it, so neither list contains members of an
 * unrelated type. Do not merge the two.
 *
 * An empty list after `.` means the receiver has no declared type - a call result or
 * an index. That is the case where DM itself stops checking; offering everything
 * there would be noise.
 *
 * kind is the dm_completion_kind values below. builtin is true for BYOND's own
 * members rather than anything the project declared, so you can style them apart.
 *
 * "inferred" (0.14) is true exactly when the RECEIVER'S type was worked out rather
 * than written down - an untyped local through `new` or an assignment, or an `as`
 * clause. dm.exe performs no local type inference at all, so those items are the
 * one place we knowingly go further than the compiler and accepting one can produce
 * code that does not build. It is a per-item fact, NOT a property of the context:
 * a member list off a written type carries false throughout, so deriving it from
 * the trigger character marks correct items as risky. Badge them, rank them lower
 * or drop them - the flag is the fact that decision needs.
 *
 * "type" and "value" (0.21) are the ITEM'S OWN declared type and its initialiser
 * as written - "/mob" and "" for var/mob/M, "" and "6" for var/fatigue = 6 - so
 * a list renders without re-parsing the file. Both are held at the declaration by
 * the parser; a client assembling them itself is doing our job again.
 *
 * Empty is the ordinary answer for both and it is honest rather than missing. DM
 * has no "num" or "text" to name: an initialiser does not type a variable, so
 * var/fatigue = 6 genuinely has no declared type and what a reader wants there is
 * the value. A parameter's `as` clause is NOT reported as a type either - it is an
 * input filter, and dm.exe does not check members through it - so it stays in
 * "detail" where it reads as what it is.
 *
 * "value" is source text, not an evaluated result: `5 + 1` stays `5 + 1`. Folding
 * waits on a constant evaluator, and until there is one this is the author's text
 * rather than a claim about what it comes to.
 *
 * "documentation" (0.9) is the doc comment above the declaration - a run of ///
 * lines or a slash-star-star block - or empty. Use dm_complete_brief and
 * dm_complete_resolve to defer it.
 *
 * (Spelled out rather than written literally: a block-comment terminator inside
 * this comment ends it, and everything below becomes code. C does not nest block
 * comments and quotes do not protect the terminator - the same rule PLAN 8
 * records for DM. It cost a build here.)
 *
 * "truncated" (0.18) says a cap cut the list, and is false unless you asked for one
 * with dm_set_completion_limit. It also tells you whether filtering our list by the
 * typed prefix is still safe: over a truncated list it is not.
 *
 * The list is RANKED by scope distance, nearest first (0.18) - locals, parameters,
 * the enclosing type's members, globals, macros, builtins last. The ORDER is the
 * ranking; nothing repeats it as a number, so preserve it.
 *
 * COST: the first call after an edit rebuilds the project's object tree. Debounce
 * this on a keystroke path rather than calling it per character.
 */
dm_status dm_complete_at(dm_workspace workspace, const char *file,
                         int32_t line, int32_t character,
                         dm_position_encoding encoding, char **out_json);

/* -- workspace symbols ---------------------------------------------------- */

/*
 * Every symbol in the project whose name matches a query. Added in ABI 0.8.
 *
 * You own the buffer. Release it with dm_free.
 *
 * RANKED AND CAPPED, not exhaustive. A two-character query on a large project matches
 * tens of thousands of symbols; an unranked wall of them is useless in a picker. Order
 * is exact name, then prefix, then substring, with shorter names first inside each
 * band. Matching is case-insensitive.
 *
 * Pass 0 for limit to take the default (200). Ask for what you will display.
 *
 * Builtins are never returned: nothing declares them, so a hit could not be opened.
 *
 * Shape - the same two ranges as dm_document_symbols, plus the file:
 *
 *   {
 *     "symbols": [
 *       {
 *         "name": "sharpness",
 *         "detail": "/obj/sword/sharpness",   the owning path, so two `New`s differ
 *         "kind": 1,                          dm_symbol_kind
 *         "file": "C:/game/code/sword.dm",
 *         "startLine": 4, "startChar": 1,
 *         "endLine": 4,   "endChar": 20,
 *         "selStartLine": 4, "selStartChar": 5,
 *         "selEndLine": 4,   "selEndChar": 14
 *       }
 *     ]
 *   }
 *
 * COST: the first call after an edit rebuilds the object tree, same as dm_complete_at.
 */
dm_status dm_workspace_symbols(dm_workspace workspace, const char *query, int32_t limit,
                               dm_position_encoding encoding, char **out_json);

/* -- hover ---------------------------------------------------------------- */

/*
 * The declaration behind the symbol at a position, for a tooltip. Added in ABI 0.7.
 *
 * You own the buffer. Release it with dm_free. Line and character are ZERO-BASED.
 *
 * Returns an EMPTY JSON OBJECT - {} - when nothing resolves, and DM_OK with it. A
 * pointer resting on a local, a keyword or whitespace is the ordinary case rather than
 * a failure, so check for the "detail" key instead of the status.
 *
 * Where a symbol has several declarations this renders the NEAREST one. Hover is a
 * glance; a reader who wants the whole override chain is asking dm_definition_at.
 *
 * Shape:
 *
 *   {
 *     "detail": "/mob/guy/health",           the resolved path
 *     "signature": "var/health = 1",         the declaration as written
 *     "documentation": "How much ...",       preceding /// lines, markers stripped
 *     "startLine": 9, "startChar": 3,        the token you hovered, to highlight
 *     "endLine": 9,   "endChar": 9
 *   }
 *
 * documentation is a run of `///` lines directly above the declaration, joined with
 * newlines. A blank line or a plain `//` comment ends the run, matching what a reader
 * takes to be attached to the declaration.
 */
dm_status dm_hover_at(dm_workspace workspace, const char *file,
                      int32_t line, int32_t character,
                      dm_position_encoding encoding, char **out_json);

/* -- diagnostics ---------------------------------------------------------- */

/*
 * Every diagnostic for one file - syntax and semantic - as a UTF-8 JSON
 * document. Added in ABI 0.13.
 *
 * You own the buffer. Release it with dm_free.
 *
 * This is diagnostics WITHOUT the outline: dm_document_symbols still carries
 * the syntax errors beside the symbols, but a client drawing squiggles for a
 * file no panel shows should not pay for the outline - and the semantic set
 * (undefined var/proc through a typed receiver, duplicate definitions, the
 * DM03xx compiles-clean-but-lies warnings) arrives ONLY here. It is the same
 * set the LSP shell publishes and `dmc diagdiff` holds at zero invented
 * against dm.exe.
 *
 * Shape - the elements are byte-identical to dm_document_symbols' diagnostics:
 *
 *   { "diagnostics": [
 *       { "id": "DM0400", "severity": "error",     severity is a WORD
 *         "message": "I.nowhere: undefined var",
 *         "startLine": 4, "startChar": 8,          zero-based, your encoding
 *         "endLine": 4,   "endChar": 15 } ] }
 *
 * An empty array with DM_OK is the ordinary answer on a clean file.
 *
 * COST: the semantic half needs the object tree, so the first call after an
 * edit rebuilds it - same as dm_complete_at. Debounce it.
 */
dm_status dm_diagnostics(dm_workspace workspace, const char *file,
                         dm_position_encoding encoding, char **out_json);

/*
 * Caps every completion list for this workspace, or 0 for no cap. Added in 0.18.
 *
 * OFF BY DEFAULT, and that is deliberate. A bare identifier offers 19,898
 * items on /tg/station, so capping looks like the obvious fix - but section 4
 * tells you to filter the list by the typed prefix yourself, and over a capped
 * list that silently misses the item your user is typing toward. Switch this on
 * only if you handle "truncated" on the response, which tells you per position
 * when local filtering stopped being safe. In LSP terms it is isIncomplete:
 * re-ask as the user types instead of filtering what you have.
 *
 * Filtering server-side instead would be sound and costs a call per keystroke -
 * a keystroke drops the tree, so that is ~909 ms per character on /tg/station
 * against one rebuild per trigger today. That is why we did not do it for you.
 */
dm_status dm_set_completion_limit(dm_workspace workspace, int32_t limit);

/*
 * The completion list with NO documentation attached. Added in ABI 0.17.
 * YOU FREE the result with dm_free.
 *
 * Same list, same shapes as dm_complete_at - every item's "documentation" is
 * an empty string. A bare identifier on /tg/station offers 19,898 items and
 * the user reads one.
 *
 * WHAT THIS ACTUALLY BUYS, measured rather than assumed: documentation is
 * 12.7% of that 1.0 MB payload, so this cuts the bytes you marshal. It is NOT
 * a latency win - the lookups run over text we have already cached, and
 * full-versus-brief timing came back inside run-to-run noise. If your client
 * pays for JSON volume, use it; if you were hoping for speed, the item COUNT
 * is where the cost is and neither call here changes that.
 *
 * Pair it with dm_complete_resolve when the user highlights an item.
 */
dm_status dm_complete_brief(dm_workspace workspace, const char* file,
                            int32_t line, int32_t character,
                            dm_position_encoding encoding, char** out_json);

/*
 * The documentation for ONE item of the list a position offers. Added in 0.17.
 * YOU FREE the result with dm_free.
 *
 *   { "documentation": "How much damage it can take." }
 *
 * Pass the same file and position you completed at, plus the item's "name".
 * Stateless by design: nothing is retained between the two calls, so there is
 * no handle to go stale and no ordering to get wrong. DM has no overloads, so
 * a name at a position is unambiguous.
 *
 * An empty string is a normal answer - most symbols carry no doc comment.
 */
dm_status dm_complete_resolve(dm_workspace workspace, const char* file,
                              int32_t line, int32_t character, const char* name,
                              dm_position_encoding encoding, char** out_json);

/* -- the .dme's tickmarks -------------------------------------------------- */

/*
 * Whether DreamMaker's include block lists this file. Added in ABI 0.20.
 * Returns 1 ticked, 0 not, -1 on a bad handle or path.
 *
 * `file` may be absolute or relative to the project root - we work out the
 * spelling the block uses.
 */
int32_t dm_dme_is_ticked(dm_workspace workspace, const char* file);

/*
 * The edit that adds (dm_dme_tick) or removes (dm_dme_untick) a file from the
 * .dme's include block. Added in ABI 0.20. YOU FREE IT with dm_free.
 *
 *   { "refusal": "none", "start": 412, "length": 0,
 *     "text": "#include \"src\\mob.dm\"\r\n" }
 *
 * ** WE RETURN THE EDIT, WE DO NOT WRITE THE FILE **
 * The .dme is usually open in the editor that asked, and often has unsaved
 * changes. Writing it underneath you would lose them. A tick is a zero-length
 * insert, which applies cleanly to a dirty buffer. Offsets index the .dme text
 * THIS WORKSPACE CURRENTLY SEES, so if you hold unsaved changes, push the .dme
 * with dm_set_buffer first and apply the edit to that same text.
 *
 * "refusal" is always present and is a word:
 *   "none"         an edit is included
 *   "noChange"     already in the state you asked for; nothing to do
 *   "noBlock"      no // BEGIN_INCLUDE ... // END_INCLUDE pair to edit
 *   "conditional"  the block contains #if/#else/#elif/#endif. A line inside a
 *                  conditional does not mean the file is in the build, so
 *                  neither ticking nor unticking has a correct answer and we
 *                  refuse rather than guess at your project file.
 *
 * The block can list the same path twice - DreamMaker's generated block
 * re-adding one the author wrote manually. Untick removes one per call, so
 * call again until you get "noChange" if you want them all gone.
 *
 * Only the region between the BEGIN and END markers is ever touched. Includes
 * written above or below it are the author's and are left alone, and an
 * #include <library> is skipped entirely - it is not a project file.
 */
dm_status dm_dme_tick(dm_workspace workspace, const char* file, char** out_json);
dm_status dm_dme_untick(dm_workspace workspace, const char* file, char** out_json);

/* -- editor surfaces ------------------------------------------------------ */

/*
 * Where the TYPE of the symbol at a position is declared. Added in ABI 0.19.
 * YOU FREE the result with dm_free. Same shape as dm_definition_at.
 *
 * One hop past go-to-definition: on `var/mob/test/M` that lands on the variable,
 * this lands on /mob/test. Only a WRITTEN type is followed - an inferred one
 * would send your user's caret into a guess, so it answers empty instead.
 */
dm_status dm_type_definition_at(dm_workspace workspace, const char* file,
                                int32_t line, int32_t character,
                                dm_position_encoding encoding, char** out_json);

/*
 * Foldable regions for a file. Added in ABI 0.19. YOU FREE IT with dm_free.
 *
 *   { "ranges": [ { "startLine": 0, "endLine": 4, "kind": "region" } ] }
 *
 * Lines are zero-based and INCLUSIVE of both ends. kind is "region" or
 * "comment" - a word, not a number, so it cannot be decoded with the wrong
 * table. Treat an unknown kind as "region".
 *
 * Built from the AST, not from indentation: DM's brace blocks and significant
 * indentation nest freely, so folding on leading whitespace silently drops
 * everything written inside braces, which is most macro-generated code. Needs
 * no object tree, so it is cheap and works before the project is walked.
 */
dm_status dm_folding_ranges(dm_workspace workspace, const char* file, char** out_json);

/*
 * Resolved #include targets in a file, for clickable navigation. Added in 0.19.
 * YOU FREE IT with dm_free.
 *
 *   { "links": [ { "startLine": 0, "startChar": 10, "endLine": 0,
 *                  "endChar": 20, "target": "C:\\game\\src\\mob.dm" } ] }
 *
 * The span covers the path text alone, inside its quotes or brackets, so the
 * hit target is the file rather than the whole directive line. An include that
 * does not resolve yields NO link: navigation that dead-ends is worse than
 * none, and a broken include is where a reader most wants to notice.
 */
dm_status dm_document_links(dm_workspace workspace, const char* file,
                            dm_position_encoding encoding, char** out_json);

/*
 * Whether the .dme's include walk actually reaches this file. Added in 0.19.
 *
 * Returns 1 in the project, 0 outside it, -1 on a bad handle or path.
 *
 * ** THIS IS THE ANSWER TO A CONFUSION THAT WILL COST YOU A DEBUGGING SESSION **
 * dm_set_buffer accepts ANY path and returns DM_OK. But a buffer only joins the
 * object tree if the walk asks for that path. So a file the .dme does not
 * include analyses fine for anything per-file - outline, colours, syntax
 * diagnostics - while its own procs and vars resolve to NOTHING, even as
 * symbols from project files resolve normally in the same buffer. That
 * asymmetry reads exactly like a broken buffer push.
 *
 * Ask this before blaming your own code, and tell your user "this file is not
 * part of the project" instead of drawing an empty outline that looks like a
 * failure. Cheap: the walk already produced the list, so the answer is free
 * once a tree exists.
 */
int32_t dm_file_in_project(dm_workspace workspace, const char* file);

/* -- inlay hints ---------------------------------------------------------- */

/*
 * Inferred-type annotations for untyped locals, as UTF-8 JSON. Added in ABI 0.16.
 * YOU FREE the result with dm_free.
 *
 * DM code is full of `var/x = new /obj/item` and the type is exactly what a
 * reader does not have - the compiler never checks it, so nothing forces the
 * author to write it. Each hint carries the same inference completion rides on:
 *
 *   { "hints": [ { "line": 3, "char": 6,          zero-based, in the requested
 *                                                 encoding, AFTER the name
 *                  "label": ": /obj/item",        render as written
 *                  "kind": "type" } ] }           treat unknown kinds as opaque
 *
 * Line range is zero-based and inclusive, same as dm_classify_range; ask for
 * what is visible. A local with a WRITTEN type gets no hint, and every hint is
 * inference dm.exe does not do - that is the point of showing it.
 *
 * COST: the first call after an edit builds the object tree, same as
 * dm_complete_at. Debounce it, and re-ask on scroll rather than per keystroke.
 */
dm_status dm_inlay_hints(dm_workspace workspace, const char* file,
                         int32_t start_line, int32_t end_line,
                         dm_position_encoding encoding, char** out_json);

/* -- signature help ------------------------------------------------------- */

/*
 * Which call encloses a position, whose proc it is, and which parameter the
 * caret sits in. Added in ABI 0.12.
 *
 * You own the buffer. Release it with dm_free. Line and character are ZERO-BASED.
 *
 * Returns an EMPTY JSON OBJECT - {} - with DM_OK when no call encloses the
 * position, which is where a caret spends most of its time. Check for the
 * "name" key rather than the status. Ask when the user types `(` or `,`, and
 * re-ask while an argument list stays open.
 *
 * The enclosing call and the active parameter come from a scan over the
 * TOKENS, so the answer stays exact mid-keystroke - `f(a,` is a prefix the
 * parser only sees through error recovery - and a comma inside a string or a
 * nested call never miscounts the parameter. DM has no overloads, so there is
 * exactly one signature per site rather than a set to pick from.
 *
 * Shape:
 *
 *   {
 *     "detail": "/mob/guy/heal",         the owning path
 *     "name": "heal",
 *     "label": "heal(mob/target, amount as num, silent = 0)",
 *     "parameters": ["mob/target", "amount as num", "silent = 0"],
 *     "activeParameter": 1               zero-based
 *   }
 *
 * Each parameter is rendered as declared - type, `as` clause and default
 * included - and is a substring of "label", so the active one can be
 * highlighted by search instead of by re-parsing the label.
 *
 * COST: the first call after an edit rebuilds the object tree, same as
 * dm_complete_at.
 */
dm_status dm_signature_at(dm_workspace workspace, const char *file,
                          int32_t line, int32_t character,
                          dm_position_encoding encoding, char **out_json);

/* -- go to definition ---------------------------------------------------- */

/*
 * Where the symbol at a position is declared, as a UTF-8 JSON document. Added in ABI 0.6.
 *
 * You own the buffer. Release it with dm_free. Line and character are ZERO-BASED and
 * follow the encoding you pass, as everywhere else.
 *
 * ** THIS RETURNS A LIST, AND SEVERAL RESULTS IS NORMAL **
 * DM declares one symbol in several places as a matter of course: a type is reopened
 * across files, and a proc has an override chain. We report all of them rather than
 * picking one, because the pick would be arbitrary and the rest are what a reader in an
 * override-heavy codebase actually wants. Order is nearest first - the nearest override
 * is what a call reaches - so a client that insists on one destination should take the
 * first and offer the rest.
 *
 * An empty array means nothing resolved. That is normal for a local, a parameter or a
 * macro: those are not in the object tree.
 *
 * Shape:
 *
 *   {
 *     "definitions": [
 *       {
 *         "file": "C:/game/code/mob.dm",
 *         "detail": "/mob/proc/attack",   what was found, for a picker
 *         "startLine": 12, "startChar": 1,     the whole declaration
 *         "endLine": 15,   "endChar": 0,
 *         "selStartLine": 12, "selStartChar": 6,   the NAME alone
 *         "selEndLine": 12,   "selEndChar": 12
 *       }
 *     ]
 *   }
 *
 * Navigate with the sel* range, exactly as with dm_document_symbols.
 *
 * COST: the first call after an edit rebuilds the object tree, same as dm_complete_at.
 */
dm_status dm_definition_at(dm_workspace workspace, const char *file,
                           int32_t line, int32_t character,
                           dm_position_encoding encoding, char **out_json);

/* -- bulk queries --------------------------------------------------------- */

/*
 * A question about the object tree that is too big for a position-shaped call, as
 * a UTF-8 JSON request and response. Added in ABI 0.11.
 *
 * You own the response. Release it with dm_free.
 *
 * This is what the tree browser beside your editor runs on: it asks about a PATH
 * rather than a caret, and it asks for a lot at once. One export carrying a named
 * query keeps the ABI and the later LSP shell describable by one schema, which is
 * why there is no dm_object_tree / dm_subtypes_of / dm_members trio.
 *
 * Requests. Everything except "query" has a default, and unknown members are
 * ignored, so a client written against a later schema still gets an answer:
 *
 *   { "query": "objectTree", "path": "/obj", "depth": 1, "includeBuiltins": true }
 *   { "query": "subtypesOf", "path": "/obj", "limit": 500, "includeBuiltins": true }
 *   { "query": "members", "path": "/mob", "inherited": true, "includeBuiltins": true }
 *   { "query": "ancestorsOf", "path": "/mob" }                          0.14
 *   { "query": "references", "path": "/mob/hp", "limit": 1000,
 *     "encoding": "utf16" }                                             0.14
 *
 *   path             defaults to "/", the root
 *   depth            levels of children to include. 1 is one level, which is what
 *                    a panel needs for a node the user just expanded. 0 is the
 *                    node alone - a single-row refresh
 *   limit            cap on subtypesOf. Defaults to 500
 *   inherited        members: include everything the type inherits. Default true
 *   includeBuiltins  include BYOND's own types and members. Default true
 *
 * Responses:
 *
 *   { "query": "objectTree", "node": <node> }
 *   { "query": "subtypesOf", "path": "/obj", "truncated": false, "types": [<node>] }
 *   { "query": "members", "path": "/mob",
 *     "vars":  [<member>], "procs": [<member>] }
 *
 *   <node>   { "path": "/obj/item", "name": "item",
 *              "declared": true,        false = the node exists only because
 *                                       something deeper was declared
 *              "builtin": false,
 *              "parentType": "/atom",   where it INHERITS from, which is not
 *                                       always the path parent. null at the root
 *              "childCount": 12,        what exists, not what "children" holds -
 *                                       so a depth-limited node still tells you
 *                                       whether to draw an expander
 *              "varCount": 3, "procCount": 4,
 *              "children": [<node>] }
 *
 *   <member> { "name": "hp", "detail": "/obj/item",   signature for a proc
 *              "kind": 1,               dm_symbol_kind, as in dm_document_symbols
 *              "builtin": false,
 *              "inherited": true,       declared on an ancestor, not on the type
 *                                       you asked about
 *              "owner": "/atom",        which ancestor
 *              "file": "code/mob.dm" }
 *
 * ancestorsOf (0.14) answers the whole inheritance chain in one call, nearest
 * first, self excluded, as <node> objects with depth-0 children:
 *
 *   { "query": "ancestorsOf", "path": "/mob", "ancestors": [<node>] }
 *
 * references (0.14) is the reference index: every USE of a symbol across the
 * project, found by the same resolution the diagnostics use, so it and the
 * squiggles cannot disagree. The target is definition's detail spelling -
 * "/mob/hp" for a var, "/mob/heal()" for a proc, "/heal()" for a global, a
 * type's path for a type - canonicalised to the FARTHEST declaring type, so
 * a call through a subtype receiver and an override share one target. Ask
 * with the detail string a dm_definition_at hit gave you (the LAST hit is
 * the canonical one). Locals and parameters are not index symbols.
 *
 *   { "query": "references", "path": "/mob/hp", "truncated": false,
 *     "references": [
 *       { "file": "code/mob.dm",
 *         "kind": "write",              read | write | call | override
 *         "inside": "/mob/hurt()",     the enclosing symbol - group by it
 *                                      and you have a call hierarchy
 *         "startLine": 3, "startChar": 2,
 *         "endLine": 3,   "endChar": 4 } ] }
 *
 * "override" hits are proc declarations overriding the target - the incoming
 * half of a type hierarchy. Positions honour the request's "encoding"
 * ("utf16" default, "utf8"), since this call has no encoding parameter.
 *
 * COST: references walks every file's retained parse per query - bounded by
 * "limit", milliseconds on a normal project, a visible scan on a huge one.
 * Debounce it like everything else that needs the tree.
 *
 * "truncated" is reported rather than left to be inferred from the count, because
 * a list exactly as long as the limit looks identical to one that was cut. Show
 * it, or your picker will quietly claim a type has 500 subtypes.
 *
 * Returns DM_ERR_INVALID_ARG for a malformed request or an unknown query name, and
 * DM_ERR_NOT_FOUND when the path is not in the tree. An empty result is DM_OK with
 * an empty array - a type with no subtypes is an answer, not a failure.
 *
 * ** COST ** Same as dm_complete_at: the first call after an edit rebuilds the
 * object tree for the whole project.
 */
dm_status dm_query_json(dm_workspace workspace, const char *request, char **out_json);

typedef int32_t dm_completion_kind;

#define DM_COMPLETION_TYPE      0
#define DM_COMPLETION_VARIABLE  1
#define DM_COMPLETION_PROC      2
#define DM_COMPLETION_VERB      3
#define DM_COMPLETION_PARAMETER 4
#define DM_COMPLETION_LOCAL     5
#define DM_COMPLETION_MACRO     6
#define DM_COMPLETION_KEYWORD   7

/*
 * Values are a permanent contract and are never reused. Handle an unknown kind by
 * falling back to a neutral icon, so a library update cannot break your outline.
 */
typedef int32_t dm_symbol_kind;

#define DM_SYMBOL_TYPE      0   /* a node in the type tree, /obj/item          */
#define DM_SYMBOL_VARIABLE  1   /* a var, at type level or global              */
#define DM_SYMBOL_PROC      2   /* a proc                                      */
#define DM_SYMBOL_VERB      3   /* a verb - a player can invoke it directly    */
#define DM_SYMBOL_PARAMETER 4   /* a proc parameter                            */

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* DM_CORE_H */
