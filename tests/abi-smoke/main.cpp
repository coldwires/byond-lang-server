// Smoke test for the dm_core C ABI.
//
// Purpose is twofold: verify the boundary works from C++, and serve as the reference
// integration for the Qt client. If this passes and a client still misbehaves, the bug is
// in the client.
//
// Covers the three things that are expensive to get wrong later: version negotiation,
// string ownership, and handle lifetime including use-after-close.

#include "dm_core.h"

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

    test_version();
    test_open_missing();
    test_open_null_out();
    test_lifecycle(dme);
    test_free_null();
    test_classification(dme);
    test_encodings_differ_only_for_non_ascii(dme);

    std::error_code ignored;
    fs::remove_all(dir, ignored);

    std::printf("\n%s\n", g_failures == 0 ? "all checks passed" : "FAILURES PRESENT");
    return g_failures == 0 ? 0 : 1;
}
