using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>A run of source with a single colouring category.</summary>
public readonly struct ClassifiedSpan
{
    /// <summary>A span and its category.</summary>
    public ClassifiedSpan(TextSpan span, ClassificationKind kind)
    {
        Span = span;
        Kind = kind;
    }

    /// <summary>The classified run of source.</summary>
    public TextSpan Span { get; }

    /// <summary>Colouring category for the span.</summary>
    public ClassificationKind Kind { get; }

    /// <summary>Debug rendering.</summary>
    public override string ToString() => $"{Kind}{Span}";
}
