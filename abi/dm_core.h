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

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* DM_CORE_H */
