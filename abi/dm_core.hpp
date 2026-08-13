// dm_core.hpp — an optional C++ RAII wrapper over dm_core.h.
//
// Header-only, C++17, no dependencies beyond the standard library. Include it instead of
// dm_core.h and you get ownership handled: handles close themselves, every string the library
// hands back is copied into a std::string and freed, and a failed call throws instead of leaving
// you to check a status you might not check.
//
// NOTHING HERE IS REQUIRED. dm_core.h is the contract and this file only wraps it; anything this
// does not cover, call directly. The two mix freely — `workspace::get()` hands you the raw handle.
//
// WHAT IT DELIBERATELY DOES NOT DO:
//
//   - It does not default the position encoding. The C header refuses to guess and so does this:
//     UTF-16 and UTF-8 agree on ASCII, so a wrong choice survives every test you write and then
//     misplaces every span the first time a user types an accented character. See INTEGRATION.txt
//     section 5, which is the one part of that guide worth reading before writing any code.
//
//   - It does not parse the JSON. Which parser to use is your decision, not ours, and vendoring
//     one into a header you are meant to copy would make that decision for you.
//
//   - It does not make the library thread-safe. One workspace is used from one thread at a time —
//     that is a contract, not a suggestion, and violating it corrupts state rather than failing
//     cleanly. Different workspaces on different threads are fine.

#ifndef DM_CORE_HPP
#define DM_CORE_HPP

#include "dm_core.h"

// <cstdint> is spelled out because MSVC's library headers pull it in transitively and libstdc++'s
// do not — every std::int32_t below compiled on both Windows architectures and failed on every
// Linux runner, which is how the first CI run after this header landed went red.
#include <cstdint>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

namespace dm {

/// A failed ABI call. Carries the status and whatever dm_last_error had to say.
class error : public std::runtime_error {
public:
    error(dm_status status, const std::string &message)
        : std::runtime_error(message), status_(status) {}

    dm_status status() const noexcept { return status_; }

private:
    dm_status status_;
};

namespace detail {

/// The library's last error message, copied and freed. Empty when it had none.
inline std::string last_error() {
    char *message = dm_last_error();

    if (message == nullptr) {
        return {};
    }

    std::string copied(message);
    dm_free(message);
    return copied;
}

inline void check(dm_status status, const char *call) {
    if (status == DM_OK) {
        return;
    }

    std::string message = last_error();
    throw error(status, message.empty() ? std::string(call) + " failed" : message);
}

/// Takes ownership of a char* the library allocated, copying it out and freeing it.
///
/// Every string-returning export is documented as caller-frees, so this is the only correct way to
/// consume one. Returning std::string rather than a smart pointer keeps the ownership question
/// from reaching your code at all.
inline std::string take(char *owned) {
    if (owned == nullptr) {
        return {};
    }

    std::string copied(owned);
    dm_free(owned);
    return copied;
}

} // namespace detail

/// One classification span: where it starts, how long it is, and what to colour it.
struct span {
    std::int32_t offset;
    std::int32_t length;
    std::int32_t kind;
};

/// The result of a classify call, freed on destruction.
///
/// The spans cross the ABI as one contiguous block of int32 triples because this runs on every
/// scroll and keystroke. `to_vector()` copies them out if you would rather own them; iterating in
/// place costs nothing and is what a paint loop wants.
class classification {
public:
    classification() = default;

    explicit classification(dm_classification handle) noexcept : handle_(handle) {}

    classification(const classification &) = delete;
    classification &operator=(const classification &) = delete;

    classification(classification &&other) noexcept
        : handle_(std::exchange(other.handle_, nullptr)) {}

    classification &operator=(classification &&other) noexcept {
        if (this != &other) {
            reset();
            handle_ = std::exchange(other.handle_, nullptr);
        }
        return *this;
    }

    ~classification() { reset(); }

    /// Number of spans, or 0 on an empty result. Never negative here — an invalid handle cannot
    /// reach this type, because only a successful call constructs one.
    std::size_t size() const noexcept {
        if (handle_ == nullptr) {
            return 0;
        }

        std::int32_t count = dm_classification_count(handle_);
        return count < 0 ? 0 : static_cast<std::size_t>(count);
    }

    bool empty() const noexcept { return size() == 0; }

    span operator[](std::size_t index) const noexcept {
        const std::int32_t *data = dm_classification_data(handle_);
        return span{data[index * 3], data[index * 3 + 1], data[index * 3 + 2]};
    }

    std::vector<span> to_vector() const {
        std::vector<span> spans;
        spans.reserve(size());

        for (std::size_t i = 0; i < size(); ++i) {
            spans.push_back((*this)[i]);
        }

        return spans;
    }

private:
    void reset() noexcept {
        if (handle_ != nullptr) {
            dm_classification_free(handle_);
            handle_ = nullptr;
        }
    }

    dm_classification handle_ = nullptr;
};

/// An open project. Move-only, closes itself.
class workspace {
public:
    /// Opens a `.dme`. Throws dm::error if it is not there.
    static workspace open(const std::string &dme_path) {
        dm_workspace handle = nullptr;
        detail::check(dm_workspace_open(dme_path.c_str(), &handle), "dm_workspace_open");
        return workspace(handle);
    }

    /// Opens a directory with NO `.dme`, where every file is its own compilation unit of the
    /// builtins plus itself. Call this rather than refusing to open a folder — per-file answers
    /// are available the whole time, and a standalone file resolving nothing from its neighbours
    /// is what dm.exe would also do.
    static workspace open_standalone(const std::string &root_directory) {
        dm_workspace handle = nullptr;
        detail::check(dm_workspace_open_standalone(root_directory.c_str(), &handle),
                      "dm_workspace_open_standalone");
        return workspace(handle);
    }

    workspace(const workspace &) = delete;
    workspace &operator=(const workspace &) = delete;

    workspace(workspace &&other) noexcept : handle_(std::exchange(other.handle_, nullptr)) {}

    workspace &operator=(workspace &&other) noexcept {
        if (this != &other) {
            close();
            handle_ = std::exchange(other.handle_, nullptr);
        }
        return *this;
    }

    ~workspace() { close(); }

    /// The raw handle, for anything this wrapper does not cover. Still owned here.
    dm_workspace get() const noexcept { return handle_; }

    // -- project ----------------------------------------------------------

    std::string root() const {
        char *out = nullptr;
        detail::check(dm_workspace_root(handle_, &out), "dm_workspace_root");
        return detail::take(out);
    }

    /// What the project's build passes to `dm.exe -D`. Call it straight after opening.
    ///
    /// Without these you are analysing a DIFFERENT PROGRAM from the one the build produces: code
    /// behind a guard is invisible, or visible when it should not be. /tg/station builds with CBT.
    void set_defines(const std::vector<std::string> &defines) {
        std::vector<const char *> raw;
        raw.reserve(defines.size());

        for (const std::string &define : defines) {
            raw.push_back(define.c_str());
        }

        detail::check(dm_set_defines(handle_, raw.empty() ? nullptr : raw.data(),
                                     static_cast<std::int32_t>(raw.size())),
                      "dm_set_defines");
    }

    /// Pushes the text the user currently has on screen. Once called, that file is never read from
    /// disk again until close_buffer. Length is in BYTES.
    void set_buffer(const std::string &file, const std::string &content) {
        detail::check(dm_set_buffer(handle_, file.c_str(), content.data(),
                                    static_cast<std::int32_t>(content.size())),
                      "dm_set_buffer");
    }

    void close_buffer(const std::string &file) {
        detail::check(dm_close_buffer(handle_, file.c_str()), "dm_close_buffer");
    }

    /// Files changed outside the editor — a git checkout, a branch switch. Pushed buffers stay
    /// authoritative; the rebuild revalidates per file, so only what actually changed is redone.
    void invalidate() { detail::check(dm_invalidate(handle_), "dm_invalidate"); }

    /// Whether the object tree exists right now: 1 built, 0 not, -1 invalid handle. Reads a field,
    /// so poll it freely — this is how you show "indexing" instead of inferring it from latency.
    std::int32_t tree_ready() const noexcept { return dm_tree_ready(handle_); }

    /// Builds the tree now, blocking. The warm-at-open call; a no-op when already warm.
    void build_tree() { detail::check(dm_build_tree(handle_), "dm_build_tree"); }

    /// Whether the `.dme`'s include walk reaches this file: 1 in, 0 out, -1 bad handle or path.
    ///
    /// Ask before blaming your own code. set_buffer accepts any path and succeeds, but a buffer
    /// only joins the tree if the walk asks for that path — so a file the `.dme` does not include
    /// analyses fine per-file while its own declarations resolve nowhere, which reads exactly like
    /// a broken push.
    std::int32_t file_in_project(const std::string &file) const noexcept {
        return dm_file_in_project(handle_, file.c_str());
    }

    // -- painting ---------------------------------------------------------

    /// Lines are ZERO-BASED and INCLUSIVE, and out-of-range values clamp rather than erroring.
    /// Ask for what is visible.
    classification classify_range(const std::string &file, std::int32_t start_line,
                                  std::int32_t end_line, dm_position_encoding encoding) const {
        dm_classification out = nullptr;
        detail::check(dm_classify_range(handle_, file.c_str(), start_line, end_line, encoding, &out),
                      "dm_classify_range");
        return classification(out);
    }

    // -- position-shaped queries, all returning UTF-8 JSON ----------------

    std::string document_symbols(const std::string &file, dm_position_encoding encoding) const {
        return json(dm_document_symbols, file, encoding, "dm_document_symbols");
    }

    std::string diagnostics(const std::string &file, dm_position_encoding encoding) const {
        return json(dm_diagnostics, file, encoding, "dm_diagnostics");
    }

    std::string document_colors(const std::string &file, dm_position_encoding encoding) const {
        return json(dm_document_colors, file, encoding, "dm_document_colors");
    }

    std::string document_links(const std::string &file, dm_position_encoding encoding) const {
        return json(dm_document_links, file, encoding, "dm_document_links");
    }

    std::string complete_at(const std::string &file, std::int32_t line, std::int32_t character,
                            dm_position_encoding encoding) const {
        return at(dm_complete_at, file, line, character, encoding, "dm_complete_at");
    }

    /// The same list with every "documentation" empty. A payload cut and NOT a speed win — the
    /// item count is the cost and neither call changes it.
    std::string complete_brief(const std::string &file, std::int32_t line, std::int32_t character,
                               dm_position_encoding encoding) const {
        return at(dm_complete_brief, file, line, character, encoding, "dm_complete_brief");
    }

    /// Fills in one item's documentation. Stateless: nothing is retained between the two calls.
    std::string complete_resolve(const std::string &file, std::int32_t line, std::int32_t character,
                                 const std::string &name, dm_position_encoding encoding) const {
        char *out = nullptr;
        detail::check(dm_complete_resolve(handle_, file.c_str(), line, character, name.c_str(),
                                          encoding, &out),
                      "dm_complete_resolve");
        return detail::take(out);
    }

    /// Opt-in cap on the completion list. Off by default on purpose: with no cap the list is
    /// complete and "truncated" is honestly false, which preserves one call per trigger.
    void set_completion_limit(std::int32_t limit) {
        detail::check(dm_set_completion_limit(handle_, limit), "dm_set_completion_limit");
    }

    /// Every declaration, nearest first. A list rather than one location, because DM reopens types
    /// and overrides procs as a matter of course.
    std::string definition_at(const std::string &file, std::int32_t line, std::int32_t character,
                              dm_position_encoding encoding) const {
        return at(dm_definition_at, file, line, character, encoding, "dm_definition_at");
    }

    /// One hop further than definition, and it follows a WRITTEN type only — an inferred one would
    /// send the caret into a guess, so an untyped local answers empty.
    std::string type_definition_at(const std::string &file, std::int32_t line,
                                   std::int32_t character, dm_position_encoding encoding) const {
        return at(dm_type_definition_at, file, line, character, encoding, "dm_type_definition_at");
    }

    std::string hover_at(const std::string &file, std::int32_t line, std::int32_t character,
                         dm_position_encoding encoding) const {
        return at(dm_hover_at, file, line, character, encoding, "dm_hover_at");
    }

    /// Which call encloses the position and which parameter the caret is in. Exact mid-keystroke:
    /// it comes from a token scan, not from counting text commas.
    std::string signature_at(const std::string &file, std::int32_t line, std::int32_t character,
                             dm_position_encoding encoding) const {
        return at(dm_signature_at, file, line, character, encoding, "dm_signature_at");
    }

    /// Best-effort rename: the provable edits, plus the "uncertain" sites a human must check.
    /// A refusal comes back as data ("refusal" != "none"), not as an exception.
    std::string rename_at(const std::string &file, std::int32_t line, std::int32_t character,
                          dm_position_encoding encoding, const std::string &new_name) const {
        char *out = nullptr;
        detail::check(dm_rename_at(handle_, file.c_str(), line, character, encoding,
                                   new_name.c_str(), &out),
                      "dm_rename_at");
        return detail::take(out);
    }

    /// Inferred types for untyped locals. Lines are zero-based and inclusive, like classify.
    std::string inlay_hints(const std::string &file, std::int32_t start_line, std::int32_t end_line,
                            dm_position_encoding encoding) const {
        char *out = nullptr;
        detail::check(dm_inlay_hints(handle_, file.c_str(), start_line, end_line, encoding, &out),
                      "dm_inlay_hints");
        return detail::take(out);
    }

    std::string folding_ranges(const std::string &file) const {
        char *out = nullptr;
        detail::check(dm_folding_ranges(handle_, file.c_str(), &out), "dm_folding_ranges");
        return detail::take(out);
    }

    /// Every state in a `.dmi`. Read from disk rather than a buffer, since an icon is a PNG.
    ///
    /// A NAME IS NOT A KEY: one name may appear twice, once with "movement": true. And
    /// "isDmi": false with DM_OK is an answer, not an error — zero-byte `.dmi` files exist in
    /// shipped games.
    std::string icon_states(const std::string &file) const {
        char *out = nullptr;
        detail::check(dm_icon_states(handle_, file.c_str(), &out), "dm_icon_states");
        return detail::take(out);
    }

    // -- project-wide -----------------------------------------------------

    /// Ranked and capped, not exhaustive. Pass 0 for the default of 200.
    std::string workspace_symbols(const std::string &query, std::int32_t limit,
                                  dm_position_encoding encoding) const {
        char *out = nullptr;
        detail::check(dm_workspace_symbols(handle_, query.c_str(), limit, encoding, &out),
                      "dm_workspace_symbols");
        return detail::take(out);
    }

    /// A bulk question about the object tree. Request and response shapes are frozen in
    /// abi/schema/ — this is what a tree panel runs on.
    std::string query_json(const std::string &request) const {
        char *out = nullptr;
        detail::check(dm_query_json(handle_, request.c_str(), &out), "dm_query_json");
        return detail::take(out);
    }

    // -- the .dme's own include block -------------------------------------

    std::int32_t dme_is_ticked(const std::string &file) const noexcept {
        return dm_dme_is_ticked(handle_, file.c_str());
    }

    /// Returns an EDIT rather than writing the file, because the `.dme` is usually open and often
    /// dirty in the editor that asked. Offsets index the text this workspace currently sees, so
    /// push the `.dme` with set_buffer first if you hold unsaved changes.
    std::string dme_tick(const std::string &file) const {
        char *out = nullptr;
        detail::check(dm_dme_tick(handle_, file.c_str(), &out), "dm_dme_tick");
        return detail::take(out);
    }

    std::string dme_untick(const std::string &file) const {
        char *out = nullptr;
        detail::check(dm_dme_untick(handle_, file.c_str(), &out), "dm_dme_untick");
        return detail::take(out);
    }

private:
    explicit workspace(dm_workspace handle) noexcept : handle_(handle) {}

    void close() noexcept {
        if (handle_ != nullptr) {
            dm_workspace_close(handle_);
            handle_ = nullptr;
        }
    }

    template <typename Call>
    std::string json(Call call, const std::string &file, dm_position_encoding encoding,
                     const char *name) const {
        char *out = nullptr;
        detail::check(call(handle_, file.c_str(), encoding, &out), name);
        return detail::take(out);
    }

    template <typename Call>
    std::string at(Call call, const std::string &file, std::int32_t line, std::int32_t character,
                   dm_position_encoding encoding, const char *name) const {
        char *out = nullptr;
        detail::check(call(handle_, file.c_str(), line, character, encoding, &out), name);
        return detail::take(out);
    }

    dm_workspace handle_ = nullptr;
};

/// The ABI version this library was built with, packed as (major << 16) | minor.
inline std::int32_t abi_version() noexcept { return dm_abi_version(); }

/// Whether the loaded library's MAJOR matches what this header was compiled against. Check it once
/// at startup and refuse to run on a mismatch; a higher minor is always fine, since additions only.
inline bool abi_compatible() noexcept {
    return DM_ABI_VERSION_MAJOR(dm_abi_version()) == DM_ABI_EXPECTED_MAJOR;
}

} // namespace dm

#endif // DM_CORE_HPP
