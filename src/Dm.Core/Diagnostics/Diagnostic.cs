using Dm.Core.Text;

namespace Dm.Core.Diagnostics;

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Information,
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
    public Diagnostic(
        string id, DiagnosticSeverity severity, TextSpan span, string message, string? file = null)
    {
        Id = id;
        Severity = severity;
        Span = span;
        Message = message;
        File = file;
    }

    public string Id { get; }

    public DiagnosticSeverity Severity { get; }

    public TextSpan Span { get; }

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

    public static Diagnostic Error(string id, TextSpan span, string message, string? file = null)
        => new(id, DiagnosticSeverity.Error, span, message, file);

    public static Diagnostic Warning(string id, TextSpan span, string message, string? file = null)
        => new(id, DiagnosticSeverity.Warning, span, message, file);

    /// <summary>The same diagnostic, attributed to a file.</summary>
    public Diagnostic In(string file) => new(Id, Severity, Span, Message, file);

    public override string ToString()
        => File is null ? $"{Severity} {Id} {Span}: {Message}" : $"{Severity} {Id} {File}{Span}: {Message}";
}
