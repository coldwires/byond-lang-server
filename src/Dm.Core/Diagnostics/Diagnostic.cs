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
    public Diagnostic(string id, DiagnosticSeverity severity, TextSpan span, string message)
    {
        Id = id;
        Severity = severity;
        Span = span;
        Message = message;
    }

    public string Id { get; }

    public DiagnosticSeverity Severity { get; }

    public TextSpan Span { get; }

    public string Message { get; }

    public static Diagnostic Error(string id, TextSpan span, string message)
        => new(id, DiagnosticSeverity.Error, span, message);

    public static Diagnostic Warning(string id, TextSpan span, string message)
        => new(id, DiagnosticSeverity.Warning, span, message);

    public override string ToString() => $"{Severity} {Id} {Span}: {Message}";
}
