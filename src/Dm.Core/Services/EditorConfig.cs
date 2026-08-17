using System;
using System.Collections.Generic;
using System.IO;

namespace Dm.Core.Services;

/// <summary>
/// The <c>.editorconfig</c> properties that apply to one file.
/// </summary>
/// <remarks>
/// <para>
/// A subset of the format, chosen to be the part real DM projects write: sections, comments,
/// <c>root</c>, and globs built from <c>*</c>, <c>**</c>, <c>?</c> and <c>{a,b}</c>. Character
/// ranges and numeric ranges are not supported; nothing in the corpus uses them, and a pattern
/// this reader cannot parse matches nothing rather than matching everything, so an unsupported
/// section leaves the spec's defaults standing rather than silently applying the wrong ones.
/// </para>
/// <para>
/// The resolution order is the format's own: files are read from the target's own directory
/// upward, stopping after one that declares <c>root = true</c>, and applied outermost first so the
/// nearest file wins. Within a file, a later matching section wins over an earlier one — which is
/// what makes tgstation's <c>[*]</c> block and its <c>[*.{dm,json,md}]</c> block compose the way
/// its authors intend.
/// </para>
/// </remarks>
internal static class EditorConfig
{
    /// <summary>Every property that applies to <paramref name="filePath"/>, nearest file winning.</summary>
    /// <remarks>
    /// Keys and values are lowercased, which the format specifies for the ones it defines. A
    /// missing, unreadable or unparseable <c>.editorconfig</c> contributes nothing: this decides
    /// whitespace, and an IO failure is not worth failing a format request over.
    /// </remarks>
    internal static IReadOnlyDictionary<string, string> PropertiesFor(string filePath)
    {
        Dictionary<string, string> properties = new(StringComparer.Ordinal);

        string full;
        try
        {
            full = Path.GetFullPath(filePath);
        }
        catch (Exception)
        {
            return properties;
        }

        // Collected nearest-first, applied in reverse, so a nearer file overwrites a farther one.
        List<(string Directory, string Text)> chain = new();

        for (string? directory = Path.GetDirectoryName(full);
             directory is not null;
             directory = Path.GetDirectoryName(directory))
        {
            string candidate = Path.Combine(directory, ".editorconfig");
            string? text = TryRead(candidate);

            if (text is null)
                continue;

            chain.Add((directory, text));

            if (IsRoot(text))
                break;
        }

        for (int i = chain.Count - 1; i >= 0; i--)
            Apply(properties, chain[i].Text, chain[i].Directory, full);

        return properties;
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary><c>root = true</c> in the preamble, which stops the upward walk.</summary>
    private static bool IsRoot(string text)
    {
        foreach (string raw in Lines(text))
        {
            string line = Strip(raw);

            if (line.Length == 0)
                continue;

            // The preamble ends at the first section; a `root` written below one is not one.
            if (line[0] == '[')
                return false;

            if (Split(line) is not { } pair)
                continue;

            if (pair.Key == "root")
                return pair.Value == "true";
        }

        return false;
    }

    /// <summary>Every property in one file whose section matches, written over what is there.</summary>
    private static void Apply(
        Dictionary<string, string> properties, string text, string directory, string filePath)
    {
        bool matching = false;

        foreach (string raw in Lines(text))
        {
            string line = Strip(raw);

            if (line.Length == 0)
                continue;

            if (line[0] == '[')
            {
                int close = line.LastIndexOf(']');
                matching = close > 1 && SectionMatches(line[1..close], directory, filePath);
                continue;
            }

            // The preamble holds `root` and nothing that applies to a file.
            if (!matching)
                continue;

            if (Split(line) is not { } pair)
                continue;

            properties[pair.Key] = pair.Value;
        }
    }

    private static IEnumerable<string> Lines(string text)
        => text.Split('\n');

    /// <summary>A line with its comment and surrounding whitespace removed.</summary>
    private static string Strip(string line)
    {
        int comment = line.IndexOfAny(new[] { '#', ';' });

        if (comment >= 0)
            line = line[..comment];

        return line.Trim();
    }

    /// <summary>A <c>key = value</c> pair, lowercased, or null when the line is neither.</summary>
    private static (string Key, string Value)? Split(string line)
    {
        int equals = line.IndexOf('=');

        if (equals <= 0)
            return null;

        string key = line[..equals].Trim().ToLowerInvariant();
        string value = line[(equals + 1)..].Trim().ToLowerInvariant();

        return key.Length == 0 ? null : (key, value);
    }

    /// <summary>
    /// Whether a section's glob covers a file.
    /// </summary>
    /// <remarks>
    /// A pattern carrying no <c>/</c> is matched against the file NAME in any subdirectory, which
    /// is the format's rule and the reason <c>[*.dm]</c> in a project root reaches
    /// <c>code/mob.dm</c>. One carrying a separator is matched against the path relative to the
    /// <c>.editorconfig</c>'s own directory. Comparison is case-insensitive: these are Windows
    /// paths as often as not, and a section that stopped matching because a file was named
    /// <c>.DM</c> would be a puzzle rather than a policy.
    /// </remarks>
    internal static bool SectionMatches(string pattern, string directory, string filePath)
    {
        pattern = pattern.Trim();

        if (pattern.Length == 0)
            return false;

        string subject;

        if (pattern.Contains('/'))
        {
            string relative;
            try
            {
                relative = Path.GetRelativePath(directory, filePath);
            }
            catch (Exception)
            {
                return false;
            }

            // Outside this .editorconfig's own tree.
            if (relative.StartsWith("..", StringComparison.Ordinal))
                return false;

            subject = relative.Replace('\\', '/');

            // A leading `/` anchors the pattern at the config's directory rather than adding a
            // path element.
            if (pattern[0] == '/')
                pattern = pattern[1..];
        }
        else
        {
            subject = Path.GetFileName(filePath);
        }

        foreach (string expanded in ExpandBraces(pattern))
        {
            if (Matches(expanded, 0, subject, 0))
                return true;
        }

        return false;
    }

    /// <summary>
    /// <c>{a,b}</c> alternation, expanded into one concrete pattern per branch.
    /// </summary>
    /// <remarks>
    /// Doing this up front keeps the matcher below to three wildcards. Nesting works because each
    /// branch is expanded again; an unclosed brace yields the pattern unchanged, so a malformed
    /// section matches literally rather than throwing.
    /// </remarks>
    private static List<string> ExpandBraces(string pattern)
    {
        int open = pattern.IndexOf('{');

        if (open < 0)
            return new List<string> { pattern };

        int depth = 0;
        int close = -1;

        for (int i = open; i < pattern.Length; i++)
        {
            if (pattern[i] == '{')
                depth++;
            else if (pattern[i] == '}' && --depth == 0)
            {
                close = i;
                break;
            }
        }

        if (close < 0)
            return new List<string> { pattern };

        string prefix = pattern[..open];
        string suffix = pattern[(close + 1)..];
        List<string> expanded = new();

        foreach (string branch in TopLevelBranches(pattern[(open + 1)..close]))
            expanded.AddRange(ExpandBraces(prefix + branch + suffix));

        return expanded;
    }

    /// <summary>The comma-separated branches of one brace group, ignoring commas nested inside it.</summary>
    private static List<string> TopLevelBranches(string body)
    {
        List<string> branches = new();
        int depth = 0;
        int start = 0;

        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];

            if (c == '{')
                depth++;
            else if (c == '}')
                depth--;
            else if (c == ',' && depth == 0)
            {
                branches.Add(body[start..i]);
                start = i + 1;
            }
        }

        branches.Add(body[start..]);
        return branches;
    }

    /// <summary>
    /// Wildcard matching for one expanded pattern: <c>?</c> is any character but a separator,
    /// <c>*</c> is any run without one, and <c>**</c> crosses separators.
    /// </summary>
    private static bool Matches(string pattern, int p, string subject, int s)
    {
        while (p < pattern.Length)
        {
            char c = pattern[p];

            if (c == '*')
            {
                bool crossesDirectories = p + 1 < pattern.Length && pattern[p + 1] == '*';
                int next = p + (crossesDirectories ? 2 : 1);

                for (int end = s; end <= subject.Length; end++)
                {
                    if (Matches(pattern, next, subject, end))
                        return true;

                    if (end < subject.Length && !crossesDirectories && subject[end] == '/')
                        break;
                }

                return false;
            }

            if (s >= subject.Length)
                return false;

            if (c == '?')
            {
                if (subject[s] == '/')
                    return false;
            }
            else if (char.ToLowerInvariant(c) != char.ToLowerInvariant(subject[s]))
            {
                return false;
            }

            p++;
            s++;
        }

        return s == subject.Length;
    }
}
