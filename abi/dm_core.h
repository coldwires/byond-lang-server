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
 * M0 note: opening validates the .dme exists and records its directory. Parsing the
 * include graph and building the object tree arrive in M2 and M4.
 */
typedef void *dm_workspace;

dm_status dm_workspace_open(const char *dme_path, dm_workspace *out_workspace);

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
 * Members from DM_CLASS_TYPE_NAME onward are reserved for semantic classification and
 * are not produced yet -- they are declared now so client colour tables do not have to
 * be renumbered when M6 lands.
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
#define DM_CLASS_TYPE_NAME               12  /* reserved, M6 */
#define DM_CLASS_PROC_NAME               13  /* reserved, M6 */
#define DM_CLASS_VAR_NAME                14  /* reserved, M6 */
#define DM_CLASS_MACRO_NAME              15  /* reserved, M6 */

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
 *       { "id": "DM0200", "message": "expected a declaration",
 *         "startLine": 4, "startChar": 0, "endLine": 4, "endChar": 3 }
 *     ]
 *   }
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
 *     "items": [
 *       { "name": "loc", "detail": "/atom", "kind": 1, "builtin": true }
 *     ]
 *   }
 *
 * context tells you why the list is what it is:
 *   "Identifier"      a bare word: locals, parameters, src members, globals
 *   "Member"          after `.` - the declared type and what it inherits
 *   "SubtypeMember"   after `:` - the above PLUS members declared on subtypes
 *   "TypePath"        after `/` - type paths
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
 * COST: the first call after an edit rebuilds the project's object tree. Debounce
 * this on a keystroke path rather than calling it per character.
 */
dm_status dm_complete_at(dm_workspace workspace, const char *file,
                         int32_t line, int32_t character,
                         dm_position_encoding encoding, char **out_json);

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
