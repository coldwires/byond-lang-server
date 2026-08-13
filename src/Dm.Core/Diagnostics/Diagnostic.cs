using Dm.Core.Text;

namespace Dm.Core.Diagnostics;

/// <summary>How serious a diagnostic is, in dm.exe's terms.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Blocks a build in dm.exe terms.</summary>
    Error,

    /// <summary>Reported by dm.exe without blocking the build.</summary>
    Warning,

    /// <summary>Informational only; dm.exe has no equivalent.</summary>
    Information,

    /// <summary>Editor-only nudge, rendered least prominently by LSP clients.</summary>
    Hint,
}

/// <summary>
/// A problem found in source, anchored to a span.
/// </summary>
/// <remarks>
/// <see cref="Id"/> is a stable string like <c>DM0001</c> so lints can be enabled and disabled
/// individually from a config file checked into the game repo. With three people sharing one
/// analyzer, everyone needs to see the same warnings.
/// </remarks>
public readonly struct Diagnostic
{
    /// <summary>Creates a diagnostic. No validation; the factories are the usual entry points.</summary>
    public Diagnostic(
        string id, DiagnosticSeverity severity, TextSpan span, string message, string? file = null)
    {
        Id = id;
        Severity = severity;
        Span = span;
        Message = message;
        File = file;
    }

    /// <summary>Stable <c>DMxxxx</c> word, sharing dm.exe's numbering where one exists.</summary>
    public string Id { get; }

    /// <summary>How serious the problem is; <see cref="DiagnosticSeverity.Error"/> blocks a build in dm.exe terms.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Where in the source the problem sits, in UTF-16 code units.</summary>
    public TextSpan Span { get; }

    /// <summary>Human-readable description of the problem.</summary>
    public string Message { get; }

    /// <summary>
    /// The file the span belongs to, or null when the caller already knows it.
    /// </summary>
    /// <remarks>
    /// Parse and binder diagnostics leave this null: they are produced per file and every consumer
    /// has that file in hand. WALK-TIME diagnostics cannot — the preprocessor crosses files, so a
    /// span alone is ambiguous, and every one of them used to be attributed to the `.dme` at line
    /// 0. That is why `#warn` could not be echoed at the line dm.exe reports it on, and why every
    /// other preprocessor diagnostic pointed at the wrong place.
    /// </remarks>
    public string? File { get; }

    /// <summary>A diagnostic that would block a build in dm.exe terms.</summary>
    public static Diagnostic Error(string id, TextSpan span, string message, string? file = null)
        => new(id, DiagnosticSeverity.Error, span, message, file);

    /// <summary>A diagnostic dm.exe would report without blocking the build.</summary>
    public static Diagnostic Warning(string id, TextSpan span, string message, string? file = null)
        => new(id, DiagnosticSeverity.Warning, span, message, file);

    /// <summary>The same diagnostic, attributed to a file.</summary>
    public Diagnostic In(string file) => new(Id, Severity, Span, Message, file);

    /// <summary>Formats as severity, id, span and message, with the file ahead of the span when one is set.</summary>
    public override string ToString()
        => File is null ? $"{Severity} {Id} {Span}: {Message}" : $"{Severity} {Id} {File}{Span}: {Message}";
}
