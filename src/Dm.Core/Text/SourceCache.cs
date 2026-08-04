using System;
using System.Collections.Generic;
using System.IO;
using Dm.Core.Syntax;

namespace Dm.Core.Text;

/// <summary>
/// Keeps the read and the lex of each file on disk, revalidated against the filesystem.
/// </summary>
/// <remarks>
/// <para>
/// Rebuilding the object tree re-reads and re-lexes every file the project includes. On a
/// 7,000-file project that is seconds of work repeated on every keystroke, and almost none of it is
/// different from last time — one file changed, in the editor, and the client already told us about
/// it through a buffer.
/// </para>
/// <para>
/// **Staleness is checked, not assumed.** An entry is reused only when the file's last-write time
/// and length both match what they were when it was read, so a <c>git checkout</c> or an external
/// edit under a running IDE is picked up without the client having to announce it. That costs one
/// filesystem probe per file per rebuild, which is the trade this cache is: probes instead of reads
/// and lexes. The residual risk is a write that changes neither the timestamp nor the length, which
/// ordinary tooling does not produce.
/// </para>
/// <para>
/// Buffers are not cached here. A pushed buffer is authoritative and its <see cref="Document"/>
/// already caches its own lex, so the one file being typed in is handled where it belongs.
/// </para>
/// </remarks>
public sealed class SourceCache
{
    private readonly Dictionary<string, Entry> _entries;

    public SourceCache(StringComparer? comparer = null)
        => _entries = new Dictionary<string, Entry>(comparer ?? StringComparer.OrdinalIgnoreCase);

    /// <summary>Files served from cache since the last <see cref="ResetStatistics"/>.</summary>
    public int Hits { get; private set; }

    /// <summary>Files read from disk since the last <see cref="ResetStatistics"/>.</summary>
    public int Misses { get; private set; }

    public void ResetStatistics()
    {
        Hits = 0;
        Misses = 0;
    }

    /// <summary>Drops everything, for a client that would rather pay than trust the probe.</summary>
    public void Clear()
    {
        _entries.Clear();
        ResetStatistics();
    }

    /// <summary>
    /// The file's text, read from disk only when the cached copy no longer matches it.
    /// </summary>
    public SourceText Read(string path)
    {
        FileInfo info = new(path);

        if (_entries.TryGetValue(path, out Entry entry) && entry.Matches(info))
        {
            Hits++;
            return entry.Text;
        }

        SourceText text = SourceFileReader.Read(path);

        // Stamped from the same FileInfo the decision was made with. Re-probing after the read
        // would record a state the text may not correspond to.
        _entries[path] = new Entry(info.Exists ? info.LastWriteTimeUtc.Ticks : 0, info.Exists ? info.Length : -1, text, null);
        Misses++;

        return text;
    }

    /// <summary>
    /// The file's lex, reused when this is the text we cached for it.
    /// </summary>
    /// <remarks>
    /// Keyed on the <see cref="SourceText"/> instance rather than on the path alone, so a caller
    /// that supplies its own text — a pushed buffer, or a file read before this cache existed —
    /// gets a fresh lex instead of the one belonging to some other content.
    /// </remarks>
    public LexResult Lex(string path, SourceText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_entries.TryGetValue(path, out Entry entry) && ReferenceEquals(entry.Text, text))
        {
            if (entry.Lex is { } cached)
                return cached;

            LexResult lexed = Syntax.Lexer.Lex(text);
            _entries[path] = entry with { Lex = lexed };
            return lexed;
        }

        return Syntax.Lexer.Lex(text);
    }

    private readonly record struct Entry(long Ticks, long Length, SourceText Text, LexResult? Lex)
    {
        /// <summary>
        /// True when the file on disk still looks like the one that was read.
        /// </summary>
        /// <remarks>
        /// A file that has since been deleted never matches, so the next read reports the failure
        /// rather than serving text for something that is gone.
        /// </remarks>
        public bool Matches(FileInfo info)
            => info.Exists && info.LastWriteTimeUtc.Ticks == Ticks && info.Length == Length;
    }
}
