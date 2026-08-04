using Dm.Core.Preprocessing;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Tests.Preprocessing;

/// <summary>
/// <see cref="MacroTable.StateHash"/> answers "does anything downstream see a different program than
/// it saw last time". Everything that reuses work between rebuilds keys on it, so a collision is not
/// a missed optimisation — it is a stale answer served as a fresh one.
/// </summary>
public class MacroTableTests
{
    /// <summary>Parses a real <c>#define</c>, so the bodies under test are the ones the walk sees.</summary>
    private static MacroDefinition Define(string name, string body)
    {
        LexResult lex = Lexer.Lex(SourceText.From($"#define {name} {body}\n"));

        return MacroDefinition.Parse(
            lex, DirectiveScanner.Scan(lex)[0], new List<Dm.Core.Diagnostics.Diagnostic>())!;
    }

    private static int HashOf(string name, string body)
    {
        MacroTable table = new();
        table.Define(Define(name, body));
        return table.StateHash;
    }

    /// <remarks>
    /// The case that shipped broken: the hash mixed a macro's name and the LENGTH of its body, so
    /// two definitions of the same name with same-length bodies were indistinguishable. A file using
    /// the macro then replayed its old expansion, and an edit to the define did nothing.
    /// </remarks>
    [Fact]
    public void Two_bodies_of_the_same_length_hash_differently()
    {
        Assert.NotEqual(HashOf("THING", "/obj/first"), HashOf("THING", "/obj/second"));
    }

    [Fact]
    public void The_same_definition_hashes_the_same()
    {
        Assert.Equal(HashOf("THING", "/obj/first"), HashOf("THING", "/obj/first"));
    }

    [Fact]
    public void A_different_name_hashes_differently()
    {
        Assert.NotEqual(HashOf("A", "1"), HashOf("B", "1"));
    }

    /// <summary>Order matters: what a file sees depends on the sequence that produced the state.</summary>
    [Fact]
    public void Define_order_changes_the_hash()
    {
        MacroTable first = new();
        first.Define(Define("A", "1"));
        first.Define(Define("B", "2"));

        MacroTable second = new();
        second.Define(Define("B", "2"));
        second.Define(Define("A", "1"));

        Assert.NotEqual(first.StateHash, second.StateHash);
    }

    [Fact]
    public void Undefining_changes_the_hash()
    {
        MacroTable table = new();
        table.Define(Define("A", "1"));
        int defined = table.StateHash;

        table.Undefine("A");

        Assert.NotEqual(defined, table.StateHash);
    }
}
