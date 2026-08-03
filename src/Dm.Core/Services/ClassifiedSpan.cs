using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>A run of source with a single colouring category.</summary>
public readonly struct ClassifiedSpan
{
    public ClassifiedSpan(TextSpan span, ClassificationKind kind)
    {
        Span = span;
        Kind = kind;
    }

    public TextSpan Span { get; }

    public ClassificationKind Kind { get; }

    public override string ToString() => $"{Kind}{Span}";
}
