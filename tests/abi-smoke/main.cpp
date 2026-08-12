// Smoke test for the dm_core C ABI.
//
// Purpose is twofold: verify the boundary works from C++, and serve as the reference
// integration for the Qt client. If this passes and a client still misbehaves, the bug is
// in the client.
//
// Covers the three things that are expensive to get wrong later: version negotiation,
// string ownership, and handle lifetime including use-after-close.

#include "dm_core.h"

// The optional C++ wrapper, included here so it is COMPILED ON EVERY RID rather than only on
// whatever machine last touched it. It is meant to be copied verbatim by a client, and the public
// CI run of 2026-08-07 is the reason that matters: the documented Linux and macOS link recipes had
// never worked for anyone following them, because nothing built them.
#include "dm_core.hpp"

#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace fs = std::filesystem;

static int g_failures = 0;

static void check(bool condition, const char *what)
{
    std::printf("  [%s] %s\n", condition ? "ok" : "FAIL", what);
    if (!condition)
        ++g_failures;
}

// Reads and frees a string owned by the library.
static std::string take(char *owned)
{
    if (owned == nullptr)
        return std::string();

    std::string copy(owned);
    dm_free(owned);
    return copy;
}

static void test_version()
{
    std::printf("version\n");

    const int32_t version = dm_abi_version();
    std::printf("  reported %d.%d\n", DM_ABI_VERSION_MAJOR(version), DM_ABI_VERSION_MINOR(version));

    check(DM_ABI_VERSION_MAJOR(version) == DM_ABI_EXPECTED_MAJOR,
          "major version matches the header this client was built against");
}

static void test_open_missing()
{
    std::printf("open a .dme that does not exist\n");

    dm_workspace ws = nullptr;
    const dm_status status = dm_workspace_open("this-file-does-not-exist.dme", &ws);

    check(status == DM_ERR_NOT_FOUND, "returns DM_ERR_NOT_FOUND");
    check(ws == nullptr, "out-parameter is cleared even on failure");

    const std::string message = take(dm_last_error());
    check(!message.empty(), "dm_last_error reports a message");
    std::printf("  message: %s\n", message.c_str());
}

static void test_open_null_out()
{
    std::printf("open with a null out-parameter\n");
    check(dm_workspace_open("whatever.dme", nullptr) == DM_ERR_INVALID_ARG,
          "returns DM_ERR_INVALID_ARG instead of crashing");
}

static void test_lifecycle(const fs::path &dme)
{
    std::printf("workspace lifecycle\n");

    dm_workspace ws = nullptr;
    const dm_status status = dm_workspace_open(dme.string().c_str(), &ws);

    check(status == DM_OK, "open succeeds");
    check(ws != nullptr, "handle is non-null");

    if (status != DM_OK)
        return;

    char *root = nullptr;
    check(dm_workspace_root(ws, &root) == DM_OK, "root query succeeds");

    const std::string root_path = take(root);
    check(!root_path.empty(), "root is a non-empty string");
    std::printf("  root: %s\n", root_path.c_str());

    check(fs::equivalent(fs::path(root_path), dme.parent_path()),
          "root is the directory containing the .dme");

    dm_workspace_close(ws);

    // The handle is now stale. A client bug that reuses it must produce a clean error, not
    // a resolved-but-wrong object and not a crash.
    char *after = reinterpret_cast<char *>(0x1);
    check(dm_workspace_root(ws, &after) == DM_ERR_INVALID_HANDLE,
          "using a closed handle returns DM_ERR_INVALID_HANDLE");
    check(after == nullptr, "out-parameter is cleared on the stale-handle path");

    dm_workspace_close(ws);
    check(true, "closing twice does not crash");
}

static void test_free_null()
{
    std::printf("dm_free(nullptr)\n");
    dm_free(nullptr);
    check(true, "accepted");
}

// -- classification -------------------------------------------------------

struct Span
{
    int32_t offset;
    int32_t length;
    int32_t kind;
};

static std::vector<Span> read_spans(dm_classification handle)
{
    std::vector<Span> spans;

    const int32_t count = dm_classification_count(handle);
    if (count <= 0)
        return spans;

    const int32_t *data = dm_classification_data(handle);
    if (data == nullptr)
        return spans;

    spans.reserve(static_cast<size_t>(count));
    for (int32_t i = 0; i < count; ++i)
        spans.push_back({ data[i * 3], data[i * 3 + 1], data[i * 3 + 2] });

    return spans;
}

static void test_classification(const fs::path &dme)
{
    std::printf("classification\n");

    dm_workspace ws = nullptr;
    if (dm_workspace_open(dme.string().c_str(), &ws) != DM_OK) {
        check(false, "workspace opened");
        return;
    }

    // Pushed buffer, never written to disk. This is how an editor feeds unsaved text.
    const std::string source = "/mob/test\n\tvar/hp = 42 // note\n";
    check(dm_set_buffer(ws, "unsaved.dm", source.c_str(),
                        static_cast<int32_t>(source.size())) == DM_OK,
          "buffer accepted for a file that does not exist on disk");

    dm_classification spans_handle = nullptr;
    check(dm_classify_range(ws, "unsaved.dm", 0, 1, DM_ENCODING_UTF16, &spans_handle) == DM_OK,
          "classify succeeds");

    const std::vector<Span> spans = read_spans(spans_handle);
    check(!spans.empty(), "returned at least one span");

    for (const Span &s : spans) {
        std::printf("    %3d +%-3d kind=%-2d  \"%s\"\n",
                    s.offset, s.length, s.kind,
                    source.substr(static_cast<size_t>(s.offset),
                                  static_cast<size_t>(s.length)).c_str());
    }

    bool ordered = true;
    int32_t previous_end = 0;
    for (const Span &s : spans) {
        if (s.offset < previous_end)
            ordered = false;
        previous_end = s.offset + s.length;
    }
    check(ordered, "spans are ordered and do not overlap");

    bool has_keyword = false, has_number = false, has_comment = false;
    for (const Span &s : spans) {
        has_keyword |= s.kind == DM_CLASS_KEYWORD;
        has_number |= s.kind == DM_CLASS_NUMBER;
        has_comment |= s.kind == DM_CLASS_COMMENT;
    }
    check(has_keyword, "found the `var` keyword");
    check(has_number, "found the number");
    check(has_comment, "found the comment");

    dm_classification_free(spans_handle);

    // A freed classification must not resolve, same contract as the workspace handle.
    check(dm_classification_count(spans_handle) == -1,
          "a freed classification handle reports -1");
    check(dm_classification_data(spans_handle) == nullptr,
          "a freed classification handle yields no data");

    dm_workspace_close(ws);
}

static void test_encodings_differ_only_for_non_ascii(const fs::path &dme)
{
    std::printf("position encodings\n");

    dm_workspace ws = nullptr;
    if (dm_workspace_open(dme.string().c_str(), &ws) != DM_OK) {
        check(false, "workspace opened");
        return;
    }

    // "日" is one UTF-16 unit but three UTF-8 bytes, so every offset after it diverges.
    // For pure ASCII the two encodings agree, which is why a mismatched client survives
    // testing right up until someone types a non-ASCII character.
    const std::string source = "var/s = \"\xE6\x97\xA5\" // after\n";
    dm_set_buffer(ws, "unicode.dm", source.c_str(), static_cast<int32_t>(source.size()));

    dm_classification utf8_handle = nullptr;
    dm_classification utf16_handle = nullptr;
    dm_classify_range(ws, "unicode.dm", 0, 0, DM_ENCODING_UTF8, &utf8_handle);
    dm_classify_range(ws, "unicode.dm", 0, 0, DM_ENCODING_UTF16, &utf16_handle);

    const std::vector<Span> utf8 = read_spans(utf8_handle);
    const std::vector<Span> utf16 = read_spans(utf16_handle);

    check(utf8.size() == utf16.size(), "both encodings produce the same number of spans");

    bool diverged = false;
    if (utf8.size() == utf16.size()) {
        for (size_t i = 0; i < utf8.size(); ++i) {
            if (utf8[i].offset != utf16[i].offset)
                diverged = true;
            if (utf8[i].kind != utf16[i].kind)
                check(false, "kinds must not depend on encoding");
        }
    }
    check(diverged, "offsets diverge after a multi-byte character");

    // The final span is the comment; its UTF-8 offset must be exactly 2 bytes further along.
    if (!utf8.empty() && utf8.size() == utf16.size()) {
        const Span &a = utf8.back();
        const Span &b = utf16.back();
        check(a.offset - b.offset == 2, "divergence equals the extra UTF-8 bytes");
    }

    dm_classification_free(utf8_handle);
    dm_classification_free(utf16_handle);

    dm_classification rejected = nullptr;
    check(dm_classify_range(ws, "unicode.dm", 0, 0, 99, &rejected) == DM_ERR_INVALID_ARG,
          "an unknown encoding is rejected");

    dm_workspace_close(ws);
}

// ---------------------------------------------------------------------------
// Document symbols. The outline crosses as JSON rather than as a packed block,
// so what matters here is that the buffer is well formed, that the caller owns
// it, and that the name-only selection range is present - that range is what an
// outline pane navigates with.
// ---------------------------------------------------------------------------
static void test_document_symbols(const fs::path &dme)
{
    dm_workspace ws = nullptr;
    check(dm_workspace_open(dme.string().c_str(), &ws) == DM_OK, "symbols: workspace opens");

    const char *src =
        "/obj/item\n"
        "\tvar/hp = 1\n"
        "\tproc/use()\n"
        "\t\treturn\n";

    check(dm_set_buffer(ws, "outline.dm", src, (int32_t)std::strlen(src)) == DM_OK,
          "symbols: buffer pushed");

    char *json = nullptr;
    check(dm_document_symbols(ws, "outline.dm", DM_ENCODING_UTF16, &json) == DM_OK,
          "symbols: call succeeds");
    check(json != nullptr, "symbols: json returned");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"symbols\":") != std::string::npos, "symbols: has a symbols array");
        check(doc.find("\"diagnostics\":") != std::string::npos, "symbols: has a diagnostics array");
        check(doc.find("\"name\":\"item\"") != std::string::npos, "symbols: the type is present");
        check(doc.find("\"name\":\"hp\"") != std::string::npos, "symbols: the var is present");
        check(doc.find("\"name\":\"use\"") != std::string::npos, "symbols: the proc is present");
        check(doc.find("\"kind\":2") != std::string::npos, "symbols: proc carries DM_SYMBOL_PROC");
        check(doc.find("\"selStartChar\":") != std::string::npos, "symbols: selection range present");
        check(doc.find("\"children\":[{") != std::string::npos, "symbols: members nest as children");

        // Every symbol names what contains it, so a hierarchy view never string-slices
        // an owner off a hover detail. The members sit on /obj/item; the type on /obj.
        check(doc.find("\"owner\":\"/obj/item\"") != std::string::npos,
              "symbols: members carry their owning type");
        check(doc.find("\"owner\":\"/obj\"") != std::string::npos,
              "symbols: a type carries its path parent");

        // We hand it back, the caller frees it. Anything else leaks across the boundary.
        dm_free(json);
    }

    // A proc block indented into a var block declares nothing - dm.exe compiles it
    // with 0 errors and drops it - so the outline must not list `vanished`, and the
    // warning must be distinguishable from a syntax error. Both halves matter: the
    // first keeps us honest with the compiler, the second is what stops a client
    // painting "this compiles but does nothing" in the same red as a parse failure.
    const char *swallowed =
        "/datum/swallowed\n"
        "\tvar\n"
        "\t\tkept = 1\n"
        "\t\tproc\n"
        "\t\t\tvanished()\n";

    check(dm_set_buffer(ws, "swallowed.dm", swallowed, (int32_t)std::strlen(swallowed)) == DM_OK,
          "symbols: discarded-proc buffer pushed");

    char *discarded = nullptr;
    check(dm_document_symbols(ws, "swallowed.dm", DM_ENCODING_UTF16, &discarded) == DM_OK,
          "symbols: discarded-proc call succeeds");

    if (discarded)
    {
        const std::string doc(discarded);
        check(doc.find("\"name\":\"kept\"") != std::string::npos, "symbols: the sibling var survives");
        check(doc.find("\"name\":\"vanished\"") == std::string::npos, "symbols: the discarded proc is absent");
        check(doc.find("\"id\":\"DM0300\"") != std::string::npos, "symbols: DM0300 reported");
        check(doc.find("\"severity\":\"warning\"") != std::string::npos, "symbols: severity is warning, not error");

        dm_free(discarded);
    }

    // A bad encoding is rejected before any work, and the out-param is cleared so an
    // ignored error leaves the caller holding NULL rather than a stale pointer.
    char *rejected = reinterpret_cast<char *>(0x1);
    check(dm_document_symbols(ws, "outline.dm", 99, &rejected) == DM_ERR_INVALID_ARG,
          "symbols: unknown encoding rejected");
    check(rejected == nullptr, "symbols: out-param cleared on failure");

    check(dm_document_symbols(nullptr, "outline.dm", DM_ENCODING_UTF16, &rejected) == DM_ERR_INVALID_HANDLE,
          "symbols: null workspace rejected");

    dm_workspace_close(ws);
}

// ---------------------------------------------------------------------------
// Completion. The interesting part is that `.` and `:` are different lists: `:`
// widens the check to the subtype tree rather than removing it, so neither one
// reaches an unrelated type.
// ---------------------------------------------------------------------------
static void test_completion(const fs::path &dir)
{
    // A project of one file, so the tree has something to resolve against.
    const fs::path dme = dir / "complete.dme";
    {
        std::ofstream out(dme);
        out << "#include \"complete.dm\"\n";
    }
    {
        std::ofstream out(dir / "complete.dm");
        // base_var is UNTYPED with an initialiser and friend is TYPED with none, so the two
        // together pin "type" and "value" independently - a check on one var alone could pass
        // with the pair swapped.
        out << "/mob/test\n\tvar/base_var = 1\n\tvar/mob/test/friend\n";
        out << "/mob/test/special\n\tvar/subtype_var = 2\n";
        out << "/datum/unrelated\n\tvar/elsewhere = 3\n";
        out << "/proc/f()\n\tvar/mob/test/t = new\n\tt.\n";
        out << "/proc/g()\n\tvar/u = new /mob/test\n\tu.\n";
        out << "/proc/h()\n\t.\n";
        // Line 15 is the signature, 16 is `\tM.` - a receiver typed by an `as` clause.
        out << "/proc/i(M as mob)\n\tM.\n";
    }

    dm_workspace ws = nullptr;
    check(dm_workspace_open(dme.string().c_str(), &ws) == DM_OK, "complete: workspace opens");

    char *json = nullptr;
    // Line 8 (0-based) is `\tt.`; character 3 is just past the dot.
    check(dm_complete_at(ws, "complete.dm", 9, 3, DM_ENCODING_UTF16, &json) == DM_OK,
          "complete: call succeeds");
    check(json != nullptr, "complete: json returned");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"context\":\"Member\"") != std::string::npos, "complete: member context");
        check(doc.find("\"name\":\"base_var\"") != std::string::npos, "complete: declared member offered");
        check(doc.find("subtype_var") == std::string::npos, "complete: `.` excludes subtype members");
        check(doc.find("elsewhere") == std::string::npos, "complete: `.` excludes unrelated types");
        check(doc.find("\"builtin\":") != std::string::npos, "complete: builtin flag present");
        check(doc.find("\"inferred\":true") == std::string::npos,
              "complete: a declared receiver marks nothing inferred");

        // The item's OWN type and initialiser, so a client renders the list without re-parsing.
        // base_var is `var/base_var = 1`: no declared type, value 1. DM has no `num` to name, so
        // the empty type is the honest answer rather than a missing one.
        check(doc.find("\"name\":\"base_var\",\"detail\":\"/mob/test\",\"kind\":1,\"builtin\":false,"
                       "\"inferred\":false,\"typeFrom\":\"written\",\"type\":\"\",\"value\":\"1\"") != std::string::npos,
              "complete: an untyped var carries its value and an empty type");

        // friend is `var/mob/test/friend`: a declared type and no initialiser - the other way
        // round, so neither field can be passing by accident.
        check(doc.find("\"name\":\"friend\",\"detail\":\"/mob/test\",\"kind\":1,\"builtin\":false,"
                       "\"inferred\":false,\"typeFrom\":\"written\",\"type\":\"/mob/test\",\"value\":\"\"") != std::string::npos,
              "complete: a typed var carries its type and an empty value");
        dm_free(json);
    }

    // Line 11 (0-based) is `\tu.` — an UNTYPED local initialised with `new /mob/test`. The list
    // rides on inference dm.exe does not do, and every item says so.
    json = nullptr;
    check(dm_complete_at(ws, "complete.dm", 12, 3, DM_ENCODING_UTF16, &json) == DM_OK,
          "complete: inferred-receiver call succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"name\":\"base_var\"") != std::string::npos,
              "complete: inference offers the members");
        check(doc.find("\"inferred\":true") != std::string::npos,
              "complete: inferred items carry the flag");

        // `var/u = new /mob/test` - worked out from the initialiser, so "inferred" is the right
        // word here and typeFrom says which route produced it.
        check(doc.find("\"typeFrom\":\"initializer\"") != std::string::npos,
              "complete: typeFrom names the route that produced the type");
        dm_free(json);
    }

    // A parameter's `as` clause. The author WROTE it, so "inferred" is a misleading word even
    // though the flag is correct - dm.exe still refuses members through an input filter. This is
    // the case typeFrom exists to let a client word properly.
    json = nullptr;
    check(dm_complete_at(ws, "complete.dm", 16, 3, DM_ENCODING_UTF16, &json) == DM_OK,
          "complete: as-clause receiver call succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"inferred\":true") != std::string::npos,
              "complete: an as-clause receiver is still flagged");
        check(doc.find("\"typeFrom\":\"as\"") != std::string::npos,
              "complete: typeFrom distinguishes a written as clause from a guess");
        dm_free(json);
    }

    // Line 13 (0-based) is `\t.` — a bare leading dot is DM's return-value variable, not member
    // access, and the distinct context is what lets a client show nothing without guessing.
    json = nullptr;
    check(dm_complete_at(ws, "complete.dm", 14, 2, DM_ENCODING_UTF16, &json) == DM_OK,
          "complete: bare-dot call succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"context\":\"ReturnValue\"") != std::string::npos,
              "complete: a bare leading dot is the return-value context");
        check(doc.find("\"items\":[]") != std::string::npos, "complete: and its list is empty");
        dm_free(json);
    }

    // Ranking and the opt-in cap. The list is ordered by scope distance, so the type's
    // own var comes before the builtins it inherits; and a cap is off until asked for,
    // because a client filtering locally over a truncated list misses what is being typed.
    json = nullptr;
    check(dm_complete_at(ws, "complete.dm", 9, 3, DM_ENCODING_UTF16, &json) == DM_OK,
          "limit: uncapped call succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"truncated\":false") != std::string::npos,
              "limit: an uncapped list says it was not cut");

        // base_var is declared on /mob/test; loc is a builtin off /atom. Ranking puts ours first.
        const std::size_t ours = doc.find("\"name\":\"base_var\"");
        const std::size_t theirs = doc.find("\"name\":\"loc\"");
        check(ours != std::string::npos && theirs != std::string::npos && ours < theirs,
              "limit: a declared member ranks above a builtin");
        dm_free(json);
    }

    check(dm_set_completion_limit(ws, 3) == DM_OK, "limit: accepted");

    json = nullptr;
    check(dm_complete_at(ws, "complete.dm", 9, 3, DM_ENCODING_UTF16, &json) == DM_OK,
          "limit: capped call succeeds");

    if (json)
    {
        check(std::string(json).find("\"truncated\":true") != std::string::npos,
              "limit: a capped list reports that it was cut");
        dm_free(json);
    }

    check(dm_set_completion_limit(ws, -1) == DM_ERR_INVALID_ARG, "limit: a negative cap is rejected");
    check(dm_set_completion_limit(ws, 0) == DM_OK, "limit: zero restores no cap");

    // Lazy resolve: the brief list is the same items with documentation left off, and
    // resolve fills in the one the user highlighted. A bare identifier on a real project
    // offers tens of thousands of items and the user reads one.
    json = nullptr;
    check(dm_complete_brief(ws, "complete.dm", 9, 3, DM_ENCODING_UTF16, &json) == DM_OK,
          "resolve: brief list succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"name\":\"base_var\"") != std::string::npos,
              "resolve: the brief list has the same items");
        check(doc.find("\"documentation\":\"\"") != std::string::npos,
              "resolve: and carries no documentation");
        dm_free(json);
    }

    json = nullptr;
    check(dm_complete_resolve(ws, "complete.dm", 9, 3, "base_var", DM_ENCODING_UTF16, &json) == DM_OK,
          "resolve: resolving one item succeeds");

    if (json)
    {
        check(std::string(json).find("\"documentation\":") != std::string::npos,
              "resolve: the response carries a documentation field");
        dm_free(json);
    }

    // A name the position does not offer is an empty answer, not an error.
    json = nullptr;
    check(dm_complete_resolve(ws, "complete.dm", 9, 3, "no_such_item", DM_ENCODING_UTF16, &json) == DM_OK,
          "resolve: an unknown item still succeeds");
    dm_free(json);

    char *rejected = reinterpret_cast<char *>(0x1);
    check(dm_complete_resolve(ws, "complete.dm", 9, 3, nullptr, DM_ENCODING_UTF16, &rejected)
              == DM_ERR_INVALID_ARG,
          "resolve: a null name is rejected");
    check(rejected == nullptr, "resolve: out-param cleared on failure");

    rejected = reinterpret_cast<char *>(0x1);
    check(dm_complete_at(ws, "complete.dm", 9, 3, 99, &rejected) == DM_ERR_INVALID_ARG,
          "complete: unknown encoding rejected");
    check(rejected == nullptr, "complete: out-param cleared on failure");

    dm_workspace_close(ws);
}

// ---------------------------------------------------------------------------
// Injected defines. The flags a project builds with decide which #ifdef
// branches exist, so a workspace without them is analysing a different
// program. Set after open on purpose: the tree is lazy, so this still
// applies to the first query.
// ---------------------------------------------------------------------------
static void test_defines(const fs::path &dir)
{
    const fs::path dme = dir / "defines.dme";
    {
        std::ofstream out(dme);
        out << "#include \"defines.dm\"\n";
    }
    {
        std::ofstream out(dir / "defines.dm");
        out << "#ifdef CBT\n/obj/with_cbt\n\tvar/flagged = 1\n";
        out << "#else\n/obj/without_cbt\n\tvar/plain = 1\n#endif\n";
        out << "/proc/f()\n\tvar/obj/with_cbt/t = new\n\tt.\n";
    }

    std::printf("defines\n");

    dm_workspace ws = nullptr;
    check(dm_workspace_open(dme.string().c_str(), &ws) == DM_OK, "defines: workspace opens");

    const char *flags[] = { "CBT" };
    check(dm_set_defines(ws, flags, 1) == DM_OK, "defines: accepted");

    char *json = nullptr;
    // Line 9 (0-based) is `\tt.`; character 3 is just past the dot.
    check(dm_complete_at(ws, "defines.dm", 9, 3, DM_ENCODING_UTF16, &json) == DM_OK,
          "defines: completion succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"name\":\"flagged\"") != std::string::npos,
              "defines: the guarded branch is the one analysed");
        dm_free(json);
    }

    check(dm_set_defines(ws, nullptr, 0) == DM_OK, "defines: clearing is accepted");
    check(dm_set_defines(nullptr, flags, 1) == DM_ERR_INVALID_HANDLE,
          "defines: null workspace rejected");
    check(dm_set_defines(ws, flags, -1) == DM_ERR_INVALID_ARG, "defines: negative count rejected");

    dm_workspace_close(ws);
}

// ---------------------------------------------------------------------------
// Go to definition. The point of interest is that several results is normal
// rather than an error: a proc override chain is genuinely two declarations,
// and a client that shows only the first should do so knowingly.
// ---------------------------------------------------------------------------
static void test_definition(const fs::path &dir)
{
    const fs::path dme = dir / "define.dme";
    {
        std::ofstream out(dme);
        out << "#include \"define.dm\"\n";
    }
    {
        std::ofstream out(dir / "define.dm");
        out << "/mob/base\n\tvar/health = 1\n\tproc/attack()\n\t\treturn\n";
        out << "/mob/base/child\n\tattack()\n\t\treturn\n";
        out << "/proc/f()\n\tvar/mob/base/child/c = new\n\tc.health = 2\n\tc.attack()\n";
    }

    std::printf("definition\n");

    dm_workspace ws = nullptr;
    check(dm_workspace_open(dme.string().c_str(), &ws) == DM_OK, "definition: workspace opens");

    // Line 9 (0-based) is `\tc.health = 2`; character 4 is inside `health`.
    char *json = nullptr;
    check(dm_definition_at(ws, "define.dm", 9, 4, DM_ENCODING_UTF16, &json) == DM_OK,
          "definition: call succeeds");
    check(json != nullptr, "definition: json returned");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"definitions\":") != std::string::npos, "definition: has a definitions array");
        check(doc.find("/mob/base/health") != std::string::npos, "definition: resolved to the base");
        check(doc.find("\"selStartLine\":") != std::string::npos, "definition: selection range present");
        dm_free(json);
    }

    // Line 10 is `\tc.attack()`; the override and the base declaration are both real.
    json = nullptr;
    check(dm_definition_at(ws, "define.dm", 10, 4, DM_ENCODING_UTF16, &json) == DM_OK,
          "definition: override chain call succeeds");

    if (json)
    {
        const std::string doc(json);
        size_t first = doc.find("\"file\":");
        size_t second = first == std::string::npos
            ? std::string::npos
            : doc.find("\"file\":", first + 1);

        check(second != std::string::npos, "definition: an override chain reports both");
        dm_free(json);
    }

    char *rejected = reinterpret_cast<char *>(0x1);
    check(dm_definition_at(ws, "define.dm", 9, 4, 99, &rejected) == DM_ERR_INVALID_ARG,
          "definition: unknown encoding rejected");
    check(rejected == nullptr, "definition: out-param cleared on failure");

    dm_workspace_close(ws);
}

// ---------------------------------------------------------------------------
// Hover. Nothing-to-show is an empty object with DM_OK, not an error, because a
// pointer resting on whitespace is the ordinary case.
// ---------------------------------------------------------------------------
static void test_hover(const fs::path &dir)
{
    const fs::path dme = dir / "hover.dme";
    {
        std::ofstream out(dme);
        out << "#include \"hover.dm\"\n";
    }
    {
        std::ofstream out(dir / "hover.dm");
        out << "/mob/guy\n\t/// How much damage it can take.\n\tvar/health = 1\n";
        out << "/proc/f()\n\tvar/mob/guy/g = new\n\tg.health = 2\n";
        out << "#define CLIP_SIZE 30\n/proc/g2()\n\treturn CLIP_SIZE\n";
        out << "/proc/g3()\n\tvar/mob/guy/m = new\n\treturn m.loc\n";
    }

    std::printf("hover\n");

    dm_workspace ws = nullptr;
    check(dm_workspace_open(dme.string().c_str(), &ws) == DM_OK, "hover: workspace opens");

    // Line 5 (0-based) is `\tg.health = 2`; character 4 is inside `health`.
    char *json = nullptr;
    check(dm_hover_at(ws, "hover.dm", 5, 4, DM_ENCODING_UTF16, &json) == DM_OK,
          "hover: call succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("/mob/guy/health") != std::string::npos, "hover: resolved the member");
        check(doc.find("var/health = 1") != std::string::npos, "hover: rendered the declaration");
        check(doc.find("How much damage") != std::string::npos, "hover: carried the doc comment");
        dm_free(json);
    }

    // A macro resolves to its #define — the preprocessor replaces the token before
    // the parser sees it, so the macro reading wins wherever the name appears.
    // Line 8 (0-based) is `\treturn CLIP_SIZE`; character 8 is inside CLIP_SIZE.
    json = nullptr;
    check(dm_hover_at(ws, "hover.dm", 8, 8, DM_ENCODING_UTF16, &json) == DM_OK,
          "hover: macro call succeeds");

    if (json)
    {
        check(std::string(json).find("#define CLIP_SIZE") != std::string::npos,
              "hover: a macro resolves to its #define");
        dm_free(json);
    }

    // A builtin member hovers from the symbol table - nothing declares `loc`, so the
    // signature is rendered rather than read from a file. Line 11 (0-based) is
    // `\treturn m.loc`; character 10 is inside `loc`.
    json = nullptr;
    check(dm_hover_at(ws, "hover.dm", 11, 10, DM_ENCODING_UTF16, &json) == DM_OK,
          "hover: builtin call succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("/atom/loc") != std::string::npos, "hover: a builtin member resolves");
        // `var/atom/loc`, not `var/loc`: builtin vars carry a compiler-probed declared type as of
        // the table that took them from 1 recorded to 39, so the rendering names what loc holds.
        check(doc.find("var/atom/loc") != std::string::npos, "hover: its signature is rendered");
        dm_free(json);
    }

    // Whitespace resolves to nothing, and that is DM_OK with an empty object.
    json = nullptr;
    check(dm_hover_at(ws, "hover.dm", 3, 0, DM_ENCODING_UTF16, &json) == DM_OK,
          "hover: an unresolved position still succeeds");

    if (json)
    {
        check(std::string(json).find("detail") == std::string::npos,
              "hover: nothing to show is an empty object");
        dm_free(json);
    }

    dm_workspace_close(ws);
}

// ---------------------------------------------------------------------------
// The reference index through dm_query_json, the ancestor chain in one call,
// and dm_invalidate.
// ---------------------------------------------------------------------------
static void test_references(const fs::path &dir)
{
    const fs::path dme = dir / "refs.dme";
    {
        std::ofstream out(dme);
        out << "#include \"refs.dm\"\n";
    }
    {
        std::ofstream out(dir / "refs.dm");
        out << "/mob/guy\n\tvar/hp = 1\n\tproc/hurt()\n\t\thp = 2\n";
        out << "/proc/f()\n\tvar/mob/guy/g = new\n\treturn g.hp\n";

        // Appended rather than inserted: the type-definition check below asks about line 5,
        // and a colour written earlier in the file would move it.
        out << "/obj/paint\n\tcolor = \"#ff0080\"\n\tvar/c = rgb(255, 0, 128)\n";
    }
    {
        // A minimal .dmi. Dream Maker writes the metadata as a DEFLATED zTXt chunk; the reader
        // also accepts an uncompressed tEXt one, which is what lets this file be built here
        // without linking zlib into the smoke test.
        const std::string meta =
            "# BEGIN DMI\nversion = 4.0\nwidth = 32\nheight = 32\n"
            "state = \"door\"\n\tdirs = 4\n\tframes = 1\n"
            "# END DMI\n";

        std::string body = "Description";
        body.push_back('\0');
        body += meta;

        std::ofstream out(dir / "icon.dmi", std::ios::binary);

        const unsigned char signature[] = { 0x89, 'P', 'N', 'G', '\r', '\n', 0x1A, '\n' };
        out.write(reinterpret_cast<const char *>(signature), sizeof(signature));

        const auto chunk = [&out](const char *kind, const std::string &data) {
            const uint32_t length = static_cast<uint32_t>(data.size());
            const char header[4] = {
                static_cast<char>((length >> 24) & 0xFF), static_cast<char>((length >> 16) & 0xFF),
                static_cast<char>((length >> 8) & 0xFF), static_cast<char>(length & 0xFF),
            };

            out.write(header, 4);
            out.write(kind, 4);
            out.write(data.data(), static_cast<std::streamsize>(data.size()));
            out.write("\0\0\0\0", 4);   // CRC, which the reader does not check
        };

        chunk("tEXt", body);
        chunk("IEND", std::string());
    }

    std::printf("references\n");

    dm_workspace ws = nullptr;
    check(dm_workspace_open(dme.string().c_str(), &ws) == DM_OK, "references: workspace opens");

    char *json = nullptr;
    check(dm_query_json(ws, "{\"query\":\"references\",\"path\":\"/mob/guy/hp\"}", &json) == DM_OK,
          "references: query succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"kind\":\"write\"") != std::string::npos, "references: the write is found");
        check(doc.find("\"kind\":\"read\"") != std::string::npos, "references: the read is found");
        check(doc.find("\"inside\":\"/mob/guy/hurt()\"") != std::string::npos,
              "references: hits carry the enclosing symbol");
        check(doc.find("\"truncated\":false") != std::string::npos, "references: truncation reported");
        dm_free(json);
    }

    json = nullptr;
    check(dm_query_json(ws, "{\"query\":\"ancestorsOf\",\"path\":\"/mob/guy\"}", &json) == DM_OK,
          "references: ancestorsOf answers");

    if (json)
    {
        check(std::string(json).find("/atom/movable") != std::string::npos,
              "references: the chain reaches the builtins in one call");
        dm_free(json);
    }

    // The editor surfaces, added at 0.19 because the LSP had them and the C ABI did not.
    json = nullptr;
    check(dm_folding_ranges(ws, "refs.dm", &json) == DM_OK, "editor: folding succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"ranges\":") != std::string::npos, "editor: folding has a ranges array");
        check(doc.find("\"kind\":\"region\"") != std::string::npos, "editor: kind is a word");
        dm_free(json);
    }

    json = nullptr;
    check(dm_document_links(ws, "refs.dme", DM_ENCODING_UTF16, &json) == DM_OK,
          "editor: document links succeed");

    if (json)
    {
        check(std::string(json).find("refs.dm") != std::string::npos,
              "editor: the include resolves to a target");
        dm_free(json);
    }

    // Icon states, added at 0.24 (M8). refs.dme's directory holds no .dmi, so these check the two
    // answers a client must tell apart: a file that is not there, and a file that is not an icon.
    json = nullptr;
    check(dm_icon_states(ws, "nosuchicon.dmi", &json) == DM_ERR_NOT_FOUND,
          "icons: a missing file is NOT_FOUND");
    check(json == nullptr, "icons: out-param cleared when the file is missing");

    // refs.dm is real and is emphatically not a PNG.
    json = nullptr;
    check(dm_icon_states(ws, "refs.dm", &json) == DM_OK,
          "icons: a file that is not an icon still succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"isDmi\":false") != std::string::npos,
              "icons: and reports isDmi false rather than failing");
        check(doc.find("\"states\":[]") != std::string::npos, "icons: with no states");
        dm_free(json);
    }

    json = nullptr;
    check(dm_icon_states(ws, "icon.dmi", &json) == DM_OK, "icons: a real icon reads");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"isDmi\":true") != std::string::npos, "icons: reported as an icon");
        check(doc.find("\"name\":\"door\"") != std::string::npos, "icons: the state name is read");
        check(doc.find("\"dirs\":4") != std::string::npos, "icons: dirs is read");
        check(doc.find("\"width\":32") != std::string::npos, "icons: the cell size is read");
        dm_free(json);
    }

    // Colours, added at 0.23. No buffer is pushed and no tree is needed, so these sit here
    // rather than at the end: the inlay-hint checks once dropped the tree a later check
    // asserted, by closing a buffer they had opened.
    json = nullptr;
    check(dm_document_colors(ws, "refs.dm", DM_ENCODING_UTF16, &json) == DM_OK,
          "colors: the call succeeds");

    if (json)
    {
        const std::string doc(json);

        check(doc.find("\"red\":255") != std::string::npos, "colors: components are 0-255");
        check(doc.find("\"blue\":128") != std::string::npos, "colors: the literal is read");
        check(doc.find("\"alpha\":255") != std::string::npos, "colors: a missing alpha is opaque");
        check(doc.find("\"form\":\"literal\"") != std::string::npos, "colors: form is a word");
        check(doc.find("\"form\":\"rgb\"") != std::string::npos, "colors: an rgb() call is found too");

        // The form it was written in leads, so accepting a picker's colour does not rewrite
        // an rgb() call into a literal or the reverse.
        check(doc.find("\"presentations\":[\"\\\"#ff0080\\\"\",\"rgb(255, 0, 128)\"]") != std::string::npos,
              "colors: a literal offers the hex spelling first");
        check(doc.find("\"presentations\":[\"rgb(255, 0, 128)\",\"\\\"#ff0080\\\"\"]") != std::string::npos,
              "colors: an rgb() call offers the call spelling first");

        dm_free(json);
    }

    // refs.dm declares `var/mob/guy/g` - a written type, so its type-definition is /mob/guy.
    // Line 5 (0-based) is `\tvar/mob/guy/g = new`; character 15 is inside `g`.
    json = nullptr;
    check(dm_type_definition_at(ws, "refs.dm", 5, 15, DM_ENCODING_UTF16, &json) == DM_OK,
          "editor: type definition succeeds");
    dm_free(json);

    // The out-of-project signal: refs.dm is included, a scratch path is not, and the
    // difference is invisible to a client without this call.
    check(dm_file_in_project(ws, "refs.dm") == 1, "editor: an included file reports in-project");
    check(dm_file_in_project(ws, "notinproject.dm") == 0,
          "editor: a file the .dme never includes reports out-of-project");

    // A buffer for that path still SUCCEEDS - which is the whole confusion this answers.
    const char *orphan = "/mob/orphan\n\tvar/hp = 1\n";
    check(dm_set_buffer(ws, "notinproject.dm", orphan, (int32_t)std::strlen(orphan)) == DM_OK,
          "editor: pushing an out-of-project buffer still succeeds");
    check(dm_file_in_project(ws, "notinproject.dm") == 0,
          "editor: and it is still reported out-of-project");
    check(dm_close_buffer(ws, "notinproject.dm") == DM_OK, "editor: orphan buffer closed");

    // The .dme tickmarks. refs.dme has no BEGIN_INCLUDE block, so both edits refuse with a
    // reason rather than inventing one - which is the behaviour a client has to handle.
    json = nullptr;
    check(dm_dme_tick(ws, "newfile.dm", &json) == DM_OK, "dme: tick succeeds");

    if (json)
    {
        check(std::string(json).find("\"refusal\":\"noBlock\"") != std::string::npos,
              "dme: a .dme with no block refuses rather than inventing one");
        dm_free(json);
    }

    check(dm_dme_is_ticked(ws, "refs.dm") == 0, "dme: no block means nothing reads as ticked");

    // A real block: tick lands as a zero-length insert carrying the whole line.
    const fs::path ticked = dir / "ticked.dme";
    {
        std::ofstream out(ticked);
        out << "// BEGIN_INCLUDE\r\n#include \"src\\a.dm\"\r\n// END_INCLUDE\r\n";
    }

    dm_workspace tw = nullptr;
    check(dm_workspace_open(ticked.string().c_str(), &tw) == DM_OK, "dme: block workspace opens");

    check(dm_dme_is_ticked(tw, "src\\a.dm") == 1, "dme: an entry reads as ticked");
    check(dm_dme_is_ticked(tw, "src/a.dm") == 1, "dme: separators normalise");
    check(dm_dme_is_ticked(tw, "src\\b.dm") == 0, "dme: a missing entry is not ticked");

    json = nullptr;
    check(dm_dme_tick(tw, "src\\b.dm", &json) == DM_OK, "dme: tick into a real block succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"refusal\":\"none\"") != std::string::npos, "dme: the tick is allowed");
        check(doc.find("\"length\":0") != std::string::npos,
              "dme: a tick is a zero-length insert, so it applies to a dirty buffer");
        check(doc.find("src\\\\b.dm") != std::string::npos, "dme: the line carries the path");
        dm_free(json);
    }

    json = nullptr;
    check(dm_dme_untick(tw, "src\\a.dm", &json) == DM_OK, "dme: untick succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"refusal\":\"none\"") != std::string::npos, "dme: the untick is allowed");
        check(doc.find("\"length\":0") == std::string::npos, "dme: an untick removes a span");
        dm_free(json);
    }

    json = nullptr;
    check(dm_dme_untick(tw, "src\\nothere.dm", &json) == DM_OK, "dme: unticking a missing file succeeds");

    if (json)
    {
        check(std::string(json).find("\"refusal\":\"noChange\"") != std::string::npos,
              "dme: and reports noChange rather than an error");
        dm_free(json);
    }

    dm_workspace_close(tw);

    // A workspace with no .dme at all: analysis stays on, per file.
    dm_workspace sw = nullptr;
    check(dm_workspace_open_standalone(dir.string().c_str(), &sw) == DM_OK,
          "standalone: opens with no .dme");
    check(dm_file_in_project(sw, "refs.dm") == 0, "standalone: nothing is in a project");

    const char *lone = "/mob/lone\n\tvar/hp = 1\n\tproc/f()\n\t\treturn hp\n";
    check(dm_set_buffer(sw, "lone.dm", lone, (int32_t)std::strlen(lone)) == DM_OK,
          "standalone: buffer accepted");

    json = nullptr;
    check(dm_document_symbols(sw, "lone.dm", DM_ENCODING_UTF16, &json) == DM_OK,
          "standalone: the outline works");
    dm_free(json);

    // The point of it: the file's OWN declarations resolve, so it is not half-broken.
    json = nullptr;
    check(dm_complete_at(sw, "lone.dm", 3, 9, DM_ENCODING_UTF16, &json) == DM_OK,
          "standalone: completion works");

    if (json)
    {
        check(std::string(json).find("\"name\":\"hp\"") != std::string::npos,
              "standalone: a lone file resolves its own members");
        dm_free(json);
    }

    dm_workspace_close(sw);

    // The readiness signal. These checks share one workspace with everything above, so they
    // establish the state they assert instead of inheriting it - closing a buffer legitimately
    // drops the tree, and a check that depends on invisible prior ordering breaks the moment a
    // section is added before it. That has now happened twice.
    check(dm_build_tree(ws) == DM_OK, "ready: dm_build_tree succeeds");
    check(dm_tree_ready(ws) == 1, "ready: a built workspace reports a tree");
    check(dm_build_tree(ws) == DM_OK, "ready: warming a warm tree is a no-op");

    check(dm_invalidate(ws) == DM_OK, "references: dm_invalidate succeeds");

    check(dm_tree_ready(ws) == 0, "ready: invalidation drops it to 0");
    check(dm_build_tree(ws) == DM_OK, "ready: dm_build_tree warms it back");
    check(dm_tree_ready(ws) == 1, "ready: and the tree reports built");

    json = nullptr;
    check(dm_query_json(ws, "{\"query\":\"references\",\"path\":\"/mob/guy/hp\"}", &json) == DM_OK,
          "references: the workspace rebuilds after invalidation");
    dm_free(json);

    // Inlay hints. refs.dm declares `var/mob/guy/g` with a WRITTEN type, so the
    // whole file hints nothing - a hint beside a written type would be noise.
    json = nullptr;
    check(dm_inlay_hints(ws, "refs.dm", 0, 100, DM_ENCODING_UTF16, &json) == DM_OK,
          "hints: call succeeds");

    if (json)
    {
        check(std::string(json).find("\"hints\":[]") != std::string::npos,
              "hints: written types produce no hints");
        dm_free(json);
    }

    // An untyped local pushed as a buffer: the inferred type is rendered after it.
    const char *hinted =
        "/mob/guy\n\tvar/hp = 1\n\tproc/hurt()\n\t\thp = 2\n"
        "/proc/f()\n\tvar/g = new /mob/guy\n\treturn g.hp\n";
    check(dm_set_buffer(ws, "refs.dm", hinted, (int32_t)std::strlen(hinted)) == DM_OK,
          "hints: buffer pushed");

    json = nullptr;
    check(dm_inlay_hints(ws, "refs.dm", 0, 100, DM_ENCODING_UTF16, &json) == DM_OK,
          "hints: untyped-local call succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\": /mob/guy\"") != std::string::npos,
              "hints: the inferred type is rendered");
        check(doc.find("\"kind\":\"type\"") != std::string::npos, "hints: kind is a word");
        dm_free(json);
    }

    check(dm_close_buffer(ws, "refs.dm") == DM_OK, "hints: buffer closed");

    dm_workspace_close(ws);

    // Stale-handle answers, in each call's own convention: the boolean is -1, the
    // status call is the usual invalid-handle error.
    check(dm_tree_ready(ws) == -1, "ready: a closed handle reports -1");
    check(dm_build_tree(ws) == DM_ERR_INVALID_HANDLE, "ready: build on a closed handle errors");
}

// ---------------------------------------------------------------------------
// Diagnostics without the outline - and the only call carrying the binder's
// semantic set across the ABI.
// ---------------------------------------------------------------------------
static void test_diagnostics(const fs::path &dir)
{
    const fs::path dme = dir / "diag.dme";
    {
        std::ofstream out(dme);
        out << "#include \"diag.dm\"\n";
    }
    {
        // `g.nowhere` is DM0400: the receiver's type is written down and no type in the
        // program declares the member.
        std::ofstream out(dir / "diag.dm");
        out << "/mob/guy\n\tvar/health = 1\n";
        out << "/proc/f()\n\tvar/mob/guy/g = new\n\treturn g.nowhere\n";
    }

    std::printf("diagnostics\n");

    dm_workspace ws = nullptr;
    check(dm_workspace_open(dme.string().c_str(), &ws) == DM_OK, "diagnostics: workspace opens");

    char *json = nullptr;
    check(dm_diagnostics(ws, "diag.dm", DM_ENCODING_UTF16, &json) == DM_OK,
          "diagnostics: call succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"DM0400\"") != std::string::npos,
              "diagnostics: the semantic set crosses the ABI");
        check(doc.find("nowhere") != std::string::npos, "diagnostics: the message names the member");
        check(doc.find("\"severity\":\"error\"") != std::string::npos,
              "diagnostics: severity is a word");
        dm_free(json);
    }

    // A clean buffer answers an empty array, not an error.
    const char *clean = "/mob/guy2\n\tvar/mana = 2\n";
    check(dm_set_buffer(ws, "diag.dm", clean, -1) == DM_OK, "diagnostics: buffer replaces the file");

    json = nullptr;
    check(dm_diagnostics(ws, "diag.dm", DM_ENCODING_UTF16, &json) == DM_OK,
          "diagnostics: clean file still succeeds");

    if (json)
    {
        check(std::string(json).find("\"diagnostics\":[]") != std::string::npos,
              "diagnostics: a clean file is an empty array");
        dm_free(json);
    }

    char *rejected = reinterpret_cast<char *>(0x1);
    check(dm_diagnostics(ws, "diag.dm", 99, &rejected) == DM_ERR_INVALID_ARG,
          "diagnostics: unknown encoding rejected");
    check(rejected == nullptr, "diagnostics: out-param cleared on failure");

    dm_workspace_close(ws);
}

// ---------------------------------------------------------------------------
// Signature help. The popup for an open argument list: which proc, and which
// parameter the caret sits in. Nothing-to-show is an empty object with DM_OK,
// as with hover.
// ---------------------------------------------------------------------------
static void test_signature(const fs::path &dir)
{
    const fs::path dme = dir / "signature.dme";
    {
        std::ofstream out(dme);
        out << "#include \"signature.dm\"\n";
    }
    {
        std::ofstream out(dir / "signature.dm");
        out << "/mob/guy\n\tproc/heal(mob/target, amount as num, silent = 0)\n\t\treturn\n";
        out << "/proc/f()\n\tvar/mob/guy/g = new\n\tg.heal(g, 5)\n";
    }

    std::printf("signature help\n");

    dm_workspace ws = nullptr;
    check(dm_workspace_open(dme.string().c_str(), &ws) == DM_OK, "signature: workspace opens");

    // Line 5 (0-based) is `\tg.heal(g, 5)`; character 11 sits in the second argument.
    char *json = nullptr;
    check(dm_signature_at(ws, "signature.dm", 5, 11, DM_ENCODING_UTF16, &json) == DM_OK,
          "signature: call succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("/mob/guy/heal") != std::string::npos, "signature: resolved the proc");
        check(doc.find("amount as num") != std::string::npos,
              "signature: parameters keep types and as clauses");
        check(doc.find("silent = 0") != std::string::npos, "signature: defaults render");
        check(doc.find("\"activeParameter\":1") != std::string::npos,
              "signature: the comma put the caret in parameter one");
        dm_free(json);
    }

    // Outside any argument list there is nothing to show, and that is DM_OK with {}.
    json = nullptr;
    check(dm_signature_at(ws, "signature.dm", 4, 0, DM_ENCODING_UTF16, &json) == DM_OK,
          "signature: a position outside a call still succeeds");

    if (json)
    {
        check(std::string(json).find("name") == std::string::npos,
              "signature: nothing to show is an empty object");
        dm_free(json);
    }

    char *rejected = reinterpret_cast<char *>(0x1);
    check(dm_signature_at(ws, "signature.dm", 5, 11, 99, &rejected) == DM_ERR_INVALID_ARG,
          "signature: unknown encoding rejected");
    check(rejected == nullptr, "signature: out-param cleared on failure");

    dm_workspace_close(ws);
}

// ---------------------------------------------------------------------------
// Workspace symbol search. Ranking is the feature: a short query in a real
// project matches far too much to show unranked.
// ---------------------------------------------------------------------------
// ---------------------------------------------------------------------------
// Bulk queries. This is what a tree-browser panel runs on, so the checks are the
// ones a panel actually depends on: that a depth-limited node still says how many
// children it has, that a capped list admits it was capped, and that a path which
// is not in the tree is NOT_FOUND rather than an empty success.
// ---------------------------------------------------------------------------
static void test_query_json(const fs::path &dir)
{
    const fs::path dme = dir / "query.dme";
    {
        std::ofstream out(dme);
        out << "#include \"query.dm\"\n";
    }
    {
        std::ofstream out(dir / "query.dm");
        out << "/obj/item\n\tvar/hp = 1\n/obj/item/sword\n\tvar/damage = 5\n/obj/item/sword/magic\n";
    }

    std::printf("bulk queries\n");

    dm_workspace ws = nullptr;
    check(dm_workspace_open(dme.string().c_str(), &ws) == DM_OK, "query: workspace opens");

    char *json = nullptr;
    check(dm_query_json(ws, "{\"query\":\"objectTree\",\"path\":\"/obj/item\"}", &json) == DM_OK,
          "query: objectTree succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"path\":\"/obj/item\"") != std::string::npos, "query: the node is the one asked for");
        check(doc.find("\"path\":\"/obj/item/sword\"") != std::string::npos, "query: one level of children");

        // Depth 1 by default, so the grandchild is counted and not included.
        check(doc.find("/obj/item/sword/magic") == std::string::npos, "query: depth stops at one level");
        check(doc.find("\"childCount\":1") != std::string::npos, "query: childCount describes what exists");
        dm_free(json);
    }

    json = nullptr;
    check(dm_query_json(ws, "{\"query\":\"subtypesOf\",\"path\":\"/obj/item\",\"limit\":1}", &json) == DM_OK,
          "query: subtypesOf succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"truncated\":true") != std::string::npos, "query: a capped listing says so");
        dm_free(json);
    }

    json = nullptr;
    check(dm_query_json(ws, "{\"query\":\"members\",\"path\":\"/obj/item/sword\"}", &json) == DM_OK,
          "query: members succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"name\":\"damage\"") != std::string::npos, "query: the type's own var is present");

        // Inherited by default, and it says where from rather than pretending it is local.
        check(doc.find("\"name\":\"hp\"") != std::string::npos, "query: an inherited var is present");
        check(doc.find("\"inherited\":true") != std::string::npos, "query: inheritance is marked");
        check(doc.find("\"owner\":\"/obj/item\"") != std::string::npos, "query: the owner is named");
        dm_free(json);
    }

    // A path that is not in the tree is NOT_FOUND. An empty success would read as
    // "this type has no members", which is a different answer.
    char *missing = reinterpret_cast<char *>(0x1);
    check(dm_query_json(ws, "{\"query\":\"members\",\"path\":\"/obj/nothing\"}", &missing) == DM_ERR_NOT_FOUND,
          "query: an unknown path is NOT_FOUND");
    check(missing == nullptr, "query: out-param cleared for an unknown path");

    char *rejected = reinterpret_cast<char *>(0x1);
    check(dm_query_json(ws, "{\"query\":\"nonsense\"}", &rejected) == DM_ERR_INVALID_ARG,
          "query: an unknown query name is rejected");
    check(dm_query_json(ws, "not json at all", &rejected) == DM_ERR_INVALID_ARG,
          "query: a malformed request is rejected");
    check(rejected == nullptr, "query: out-param cleared on failure");

    check(dm_query_json(nullptr, "{\"query\":\"objectTree\"}", &rejected) == DM_ERR_INVALID_HANDLE,
          "query: null workspace rejected");

    dm_workspace_close(ws);
}

static void test_workspace_symbols(const fs::path &dir)
{
    const fs::path dme = dir / "wsym.dme";
    {
        std::ofstream out(dme);
        out << "#include \"wsym.dm\"\n";
    }
    {
        std::ofstream out(dir / "wsym.dm");
        out << "/obj/unhit\n/obj/hitbox\n/obj/hit\n\tvar/hitpoints = 1\n";
    }

    std::printf("workspace symbols\n");

    dm_workspace ws = nullptr;
    check(dm_workspace_open(dme.string().c_str(), &ws) == DM_OK, "wsymbols: workspace opens");

    char *json = nullptr;
    check(dm_workspace_symbols(ws, "hit", 0, DM_ENCODING_UTF16, &json) == DM_OK,
          "wsymbols: call succeeds");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"symbols\":") != std::string::npos, "wsymbols: has a symbols array");

        // Exact before prefix before substring: /obj/hit must precede /obj/hitbox.
        const size_t exact = doc.find("/obj/hit\"");
        const size_t prefix = doc.find("/obj/hitbox");
        check(exact != std::string::npos && prefix != std::string::npos && exact < prefix,
              "wsymbols: an exact match is ranked before a prefix");
        check(doc.find("\"file\":") != std::string::npos, "wsymbols: hits carry a file");
        dm_free(json);
    }

    json = nullptr;
    check(dm_workspace_symbols(ws, "hit", 1, DM_ENCODING_UTF16, &json) == DM_OK,
          "wsymbols: a limit is accepted");

    if (json)
    {
        const std::string doc(json);
        check(doc.find("\"name\":", doc.find("\"name\":") + 1) == std::string::npos,
              "wsymbols: the limit is honoured");
        dm_free(json);
    }

    char *rejected = reinterpret_cast<char *>(0x1);
    check(dm_workspace_symbols(ws, "", 0, DM_ENCODING_UTF16, &rejected) == DM_ERR_INVALID_ARG,
          "wsymbols: an empty query is rejected");
    check(rejected == nullptr, "wsymbols: out-param cleared on failure");

    dm_workspace_close(ws);
}

/// The C++ wrapper, exercised rather than merely compiled.
///
/// Three things worth one check each: that a failed open THROWS instead of returning a status
/// nobody read, that a workspace closes itself, and that a string crossing the boundary is copied
/// out and freed without the caller seeing a char*.
static void test_cpp_wrapper(const fs::path &dme)
{
    std::printf("c++ wrapper\n");

    check(dm::abi_compatible(), "wrapper: major version matches the header");

    bool threw = false;

    try
    {
        dm::workspace missing = dm::workspace::open((dme.parent_path() / "nope.dme").string());
        (void)missing;
    }
    catch (const dm::error &e)
    {
        threw = true;
        check(e.status() == DM_ERR_NOT_FOUND, "wrapper: a missing .dme throws NOT_FOUND");
    }

    check(threw, "wrapper: a failed open throws rather than returning");

    {
        dm::workspace ws = dm::workspace::open(dme.string());
        check(!ws.root().empty(), "wrapper: root() copies the string out and frees it");
        check(ws.get() != nullptr, "wrapper: the raw handle is still reachable");
    }

    std::printf("\n");
}

int main()
{
    const fs::path dir = fs::temp_directory_path() / "dm_abi_smoke";
    fs::create_directories(dir);

    const fs::path dme = dir / "smoke.dme";
    {
        std::ofstream out(dme);
        out << "// generated by dm_abi_smoke\n#include \"smoke.dm\"\n";
    }

    std::printf("dm_core ABI smoke test\n\n");

    test_cpp_wrapper(dme);
    test_version();
    test_open_missing();
    test_open_null_out();
    test_lifecycle(dme);
    test_free_null();
    test_classification(dme);
    test_encodings_differ_only_for_non_ascii(dme);
    test_document_symbols(dme);
    test_completion(dir);
    test_defines(dir);
    test_definition(dir);
    test_hover(dir);
    test_signature(dir);
    test_diagnostics(dir);
    test_references(dir);
    test_workspace_symbols(dir);

    test_query_json(dir);

    std::error_code ignored;
    fs::remove_all(dir, ignored);

    std::printf("\n%s\n", g_failures == 0 ? "all checks passed" : "FAILURES PRESENT");
    return g_failures == 0 ? 0 : 1;
}
