using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dm.Core.Services;
using Dm.Core.Syntax;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Services;

public class DocumentSymbolServiceTests
{
    private static IReadOnlyList<DocumentSymbol> Symbols(
        string source,
        bool includeParameters = false,
        PositionEncoding encoding = PositionEncoding.Utf16)
    {
        ParseResult parse = DeclarationParser.Parse(Lexer.Lex(SourceText.From(source)));
        return DocumentSymbolService.GetSymbols(parse, includeParameters, encoding);
    }

    [Fact]
    public void A_type_carries_its_members_as_children()
    {
        DocumentSymbol type = Assert.Single(Symbols("/obj/item\n\tvar/hp = 1\n\tproc/use()\n\t\treturn\n"));

        Assert.Equal(SymbolKind.Type, type.Kind);
        Assert.Equal("item", type.Name);
        Assert.Equal(new[] { SymbolKind.Variable, SymbolKind.Proc }, type.Children.Select(c => c.Kind).ToArray());
    }

    /// <summary>A verb is worth telling apart from a proc: a player can invoke it directly.</summary>
    [Fact]
    public void A_verb_is_distinguished_from_a_proc()
    {
        IReadOnlyList<DocumentSymbol> symbols = Symbols("/mob/verb/shout()\n\treturn\n/mob/proc/think()\n\treturn\n");

        Assert.Equal(SymbolKind.Verb, symbols[0].Kind);
        Assert.Equal(SymbolKind.Proc, symbols[1].Kind);
    }

    /// <summary>
    /// The selection range covers the name alone, which is what an editor highlights on navigation
    /// and what rename would replace. The full range covers the declaration and its members.
    /// </summary>
    [Fact]
    public void The_selection_range_covers_the_name_only()
    {
        DocumentSymbol type = Assert.Single(Symbols("/obj/item\n\tvar/hp = 1\n"));

        Assert.Equal(0, type.SelectionStart.Line);
        Assert.Equal(5, type.SelectionStart.Character);
        Assert.Equal(9, type.SelectionEnd.Character);

        // The whole declaration reaches past the name into the indented member.
        Assert.True(type.End.Line > type.Start.Line);
    }

    /// <summary>`var/a = 1, b = 2` lists two variables, not one with a child.</summary>
    [Fact]
    public void Names_sharing_one_var_are_listed_as_peers()
    {
        DocumentSymbol type = Assert.Single(Symbols("/obj/item\n\tvar/a = 1, b = 2\n"));

        Assert.Equal(new[] { "a", "b" }, type.Children.Select(c => c.Name).ToArray());
        Assert.All(type.Children, c => Assert.Equal(SymbolKind.Variable, c.Kind));
    }

    [Fact]
    public void A_var_details_its_modifiers_and_type()
    {
        DocumentSymbol type = Assert.Single(Symbols("/obj/item\n\tvar/const/mob/test/M = null\n"));

        Assert.Equal("const /mob/test", Assert.Single(type.Children).Detail);
    }

    [Fact]
    public void A_proc_details_its_parameters()
    {
        DocumentSymbol proc = Assert.Single(Symbols("/mob/proc/attack(target, damage)\n\treturn\n"));

        Assert.Equal("(target, damage)", proc.Detail);
    }

    /// <summary>An override has no `proc/` segment, and the outline says so.</summary>
    [Fact]
    public void An_override_is_marked()
    {
        Assert.Contains("override", Assert.Single(Symbols("/mob/Login()\n\treturn\n")).Detail);
    }

    [Fact]
    public void Parameters_appear_only_when_asked_for()
    {
        Assert.Empty(Assert.Single(Symbols("/mob/proc/f(a, b)\n\treturn\n")).Children);

        DocumentSymbol withParameters = Assert.Single(Symbols("/mob/proc/f(a, b)\n\treturn\n", includeParameters: true));
        Assert.Equal(new[] { "a", "b" }, withParameters.Children.Select(c => c.Name).ToArray());
        Assert.All(withParameters.Children, c => Assert.Equal(SymbolKind.Parameter, c.Kind));
    }

    /// <summary>
    /// UTF-8 and UTF-16 columns agree for ASCII, so picking the wrong one survives testing and then
    /// misplaces every position the first time someone types a non-ASCII character. The emoji is two
    /// UTF-16 code units and four UTF-8 bytes, so the two must differ by exactly two here.
    /// </summary>
    [Fact]
    public void Columns_follow_the_requested_encoding()
    {
        // `after` sits on the same line as the emoji, past it, so its column is where the two
        // encodings have to disagree.
        const string source = "/obj/item\n\tvar/x = \"\U0001F600\", after = 1\n";

        DocumentSymbol utf16 = Symbols(source)[0].Children[1];
        DocumentSymbol utf8 = Symbols(source, encoding: PositionEncoding.Utf8)[0].Children[1];

        Assert.Equal("after", utf16.Name);
        Assert.Equal(utf16.SelectionStart.Line, utf8.SelectionStart.Line);
        Assert.Equal(utf16.SelectionStart.Character + 2, utf8.SelectionStart.Character);
    }

    /// <summary>
    /// A bare <c>var</c> block header is not itself a symbol. Leaving it in shows an outline entry
    /// called "var" with the real variables hidden one level deeper.
    /// </summary>
    [Fact]
    public void A_bare_var_block_header_is_not_a_symbol()
    {
        DocumentSymbol type = Assert.Single(Symbols("/obj/item\n\tvar\n\t\tstrength = 1\n\t\tduration = 2\n"));

        Assert.Equal(new[] { "strength", "duration" }, type.Children.Select(c => c.Name).ToArray());
        Assert.All(type.Children, c => Assert.Equal(SymbolKind.Variable, c.Kind));
    }

    /// <summary>
    /// A bare assignment overrides an inherited var and needs no <c>var/</c> — <c>world/maxx = 3</c>.
    /// Modelled as a type it would put <c>maxx</c> in the object tree as a subtype of <c>/world</c>.
    /// </summary>
    [Fact]
    public void An_inherited_var_override_is_a_variable_not_a_type()
    {
        DocumentSymbol world = Assert.Single(Symbols("world\n\tmaxx = 3\n\tmaxy = 3\n"));

        Assert.Equal(SymbolKind.Type, world.Kind);
        Assert.All(world.Children, c => Assert.Equal(SymbolKind.Variable, c.Kind));
        Assert.Equal(new[] { "maxx", "maxy" }, world.Children.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        ParseResult parse = DeclarationParser.Parse(Lexer.Lex(SourceText.From("/obj/a\n/obj/b\n")));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        Assert.Throws<System.OperationCanceledException>(
            () => DocumentSymbolService.GetSymbols(parse, false, PositionEncoding.Utf16, cancelled.Token));
    }

    [Fact]
    public void An_empty_file_has_no_symbols()
    {
        Assert.Empty(Symbols(string.Empty));
    }

    /// <summary>
    /// Every symbol reports the resolved path of what contains it, so no client string-slices the
    /// owner off a hover detail. A one-line <c>/mob/TEA()</c> is the shape dm-patch reported: the
    /// outline entry said nothing about <c>/mob</c>.
    /// </summary>
    [Fact]
    public void A_one_line_proc_reports_its_owner()
    {
        DocumentSymbol proc = Assert.Single(Symbols("/mob/proc/TEA()\n\treturn\n"));

        Assert.Equal("/mob", proc.Owner);
    }

    /// <summary>Only the trailing keyword is the group marker; `mob/proc` owns children on /mob.</summary>
    [Fact]
    public void A_group_header_with_a_path_owns_its_children()
    {
        IReadOnlyList<DocumentSymbol> symbols = Symbols("mob/proc\n\tattack()\n\t\treturn\n");

        Assert.Equal("/mob", Assert.Single(symbols).Owner);
    }

    [Fact]
    public void Nested_members_report_the_enclosing_type()
    {
        DocumentSymbol type = Assert.Single(Symbols("/obj/item\n\tvar/hp = 1\n\tproc/use()\n\t\treturn\n"));

        Assert.Equal("/obj", type.Owner);
        Assert.All(type.Children, c => Assert.Equal("/obj/item", c.Owner));
    }

    /// <summary>
    /// The var fork, same as the tree builder's: under a <c>var</c> the leading segments are the
    /// declared type and the owner is the enclosing type; a bare override's leading segments ARE
    /// the owner.
    /// </summary>
    [Fact]
    public void A_typed_var_belongs_to_the_enclosing_type_and_a_bare_override_to_its_path()
    {
        DocumentSymbol mob = Assert.Single(Symbols("mob\n\tvar\n\t\tatom/movable/locker\n"));
        Assert.Equal("/mob", Assert.Single(mob.Children).Owner);

        DocumentSymbol overridden = Assert.Single(Symbols("/obj/item/hp = 3\n"));
        Assert.Equal(SymbolKind.Variable, overridden.Kind);
        Assert.Equal("/obj/item", overridden.Owner);
    }

    /// <summary>Root-level symbols are owned by <c>/</c>, and a global proc's parameters by it.</summary>
    [Fact]
    public void Root_level_symbols_report_the_root_and_parameters_their_proc()
    {
        IReadOnlyList<DocumentSymbol> symbols = Symbols(
            "var/gx = 5\n/proc/bump(amount)\n\treturn\n", includeParameters: true);

        Assert.Equal("/", symbols[0].Owner);
        Assert.Equal("/", symbols[1].Owner);
        Assert.Equal("/bump()", Assert.Single(symbols[1].Children).Owner);
    }
}
