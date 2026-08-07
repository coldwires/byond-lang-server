using Dm.Core.Diagnostics;
using Dm.Core.Preprocessing;
using Dm.Core.Services;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Tests.Services;

public class DefinitionServiceTests
{
    /// <summary>
    /// Resolves at the caret marked <c>|</c>, which is removed before parsing.
    /// </summary>
    private static IReadOnlyList<DefinitionLocation> Definition(
        string sourceWithCaret, MacroTable? macros = null)
    {
        int caret = sourceWithCaret.IndexOf('|');
        Assert.True(caret >= 0, "the source must mark the caret with |");

        string source = sourceWithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);

        ObjectTree tree = new();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret);
        return DefinitionService.DefinitionAt(
            tree, document, position.Line, position.Character, macros: macros);
    }

    /// <summary>A macro table holding every <c>#define</c> in <paramref name="source"/>.</summary>
    internal static MacroTable Macros(string source, string path)
    {
        LexResult lex = Lexer.Lex(SourceText.From(source, path));
        MacroTable table = new();

        foreach (Directive directive in DirectiveScanner.Scan(lex))
        {
            if (directive.Kind == DirectiveKind.Define
                && MacroDefinition.Parse(lex, directive, new List<Diagnostic>()) is { } macro)
            {
                table.Define(macro);
            }
        }

        return table;
    }

    private static string TextAt(string source, DefinitionLocation location)
        => source.Substring(location.NameSpan.Start, location.NameSpan.Length);

    [Fact]
    public void A_type_path_resolves_to_its_declaration()
    {
        const string Source = "/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/x = /obj/it|em\n";

        IReadOnlyList<DefinitionLocation> found = Definition(Source);

        DefinitionLocation location = Assert.Single(found);
        Assert.Equal("/obj/item", location.Detail);
        Assert.Equal("item", TextAt(Source.Replace("|", string.Empty), location));
    }

    /// <summary>
    /// A type reopened across declarations has several definitions, and all of them are real.
    /// </summary>
    /// <remarks>
    /// Collapsing to one would pick arbitrarily and hide the rest, which matters most in exactly
    /// the codebases that reopen types heavily.
    /// </remarks>
    [Fact]
    public void A_reopened_type_reports_every_declaration()
    {
        IReadOnlyList<DefinitionLocation> found = Definition(
            "/obj/item\n\tvar/hp = 1\n/obj/item\n\tvar/weight = 2\n/proc/f()\n\tvar/x = /obj/it|em\n");

        Assert.Equal(2, found.Count);
        Assert.All(found, l => Assert.Equal("/obj/item", l.Detail));
    }

    [Fact]
    public void A_member_resolves_through_the_receivers_type()
    {
        const string Source =
            "/mob/guy\n\tvar/health = 1\n/proc/f()\n\tvar/mob/guy/g = new\n\tg.heal|th = 2\n";

        DefinitionLocation location = Assert.Single(Definition(Source));

        Assert.Equal("/mob/guy/health", location.Detail);
        Assert.Equal("health", TextAt(Source.Replace("|", string.Empty), location));
    }

    /// <summary>An inherited member resolves to where it is actually declared.</summary>
    [Fact]
    public void An_inherited_member_resolves_to_the_base()
    {
        DefinitionLocation location = Assert.Single(Definition(
            "/mob/base\n\tvar/health = 1\n/mob/base/child\n/proc/f()\n"
            + "\tvar/mob/base/child/c = new\n\tc.heal|th = 2\n"));

        Assert.Equal("/mob/base/health", location.Detail);
    }

    /// <summary>A proc's override chain is all of it, nearest first.</summary>
    [Fact]
    public void A_proc_reports_its_override_chain()
    {
        IReadOnlyList<DefinitionLocation> found = Definition(
            "/mob/base\n\tproc/attack()\n\t\treturn\n"
            + "/mob/base/child\n\tattack()\n\t\treturn\n"
            + "/proc/f()\n\tvar/mob/base/child/c = new\n\tc.atta|ck()\n");

        // The override on the child and the declaration on the base are both definitions.
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void A_bare_name_resolves_against_the_enclosing_type()
    {
        DefinitionLocation location = Assert.Single(Definition(
            "/mob/guy\n\tvar/health = 1\n\tproc/f()\n\t\thea|lth = 2\n"));

        Assert.Equal("/mob/guy/health", location.Detail);
    }

    /// <summary>A relative path resolves by the §4a search, same as everywhere else.</summary>
    [Fact]
    public void A_relative_path_resolves_by_searching_upward()
    {
        DefinitionLocation location = Assert.Single(Definition(
            "/x/sword\n/x/magic/sword\n/x/magic/thing\n\tvar/p = .swo|rd\n"));

        Assert.Equal("/x/magic/sword", location.Detail);
    }

    [Fact]
    public void An_unknown_name_resolves_to_nothing()
    {
        Assert.Empty(Definition("/proc/f()\n\tnothing_at_a|ll = 1\n"));
    }

    [Fact]
    public void A_position_on_no_token_resolves_to_nothing()
    {
        Assert.Empty(Definition("/obj/item\n|\n"));
    }

    /// <summary>
    /// Go-to-type-definition is one hop past definition: on a typed local, the caret lands on the
    /// TYPE rather than on the variable.
    /// </summary>
    [Fact]
    public void A_typed_local_resolves_to_its_type()
    {
        const string SourceWithCaret =
            "/mob/test\n\tvar/hp = 1\n/proc/f()\n\tvar/mob/test/M = new\n\treturn M|.hp\n";

        int caret = SourceWithCaret.IndexOf('|');
        string source = SourceWithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);
        ObjectTree tree = new();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret - 1);

        DefinitionLocation location = Assert.Single(DefinitionService.TypeDefinitionAt(
            tree, document, position.Line, position.Character));

        Assert.Equal("/mob/test", location.Detail);
    }

    /// <summary>
    /// An INFERRED type is not followed. Inference exists so completion can serve a half-written
    /// declaration and knowingly goes past what dm.exe checks; sending a caret there would be
    /// navigation into a guess.
    /// </summary>
    [Fact]
    public void An_inferred_type_is_not_followed()
    {
        const string SourceWithCaret =
            "/mob/test\n\tvar/hp = 1\n/proc/f()\n\tvar/M = new /mob/test\n\treturn M|.hp\n";

        int caret = SourceWithCaret.IndexOf('|');
        string source = SourceWithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);
        ObjectTree tree = new();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret - 1);

        Assert.Empty(DefinitionService.TypeDefinitionAt(
            tree, document, position.Line, position.Character));
    }

    /// <summary>A member's declared type is followed through its receiver.</summary>
    [Fact]
    public void A_typed_member_resolves_to_its_type()
    {
        const string SourceWithCaret =
            "/obj/gun\n\tvar/ammo = 1\n/mob/test\n\tvar/obj/gun/weapon\n"
            + "/proc/f()\n\tvar/mob/test/M = new\n\treturn M.weap|on\n";

        int caret = SourceWithCaret.IndexOf('|');
        string source = SourceWithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);
        ObjectTree tree = new();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret);

        DefinitionLocation location = Assert.Single(DefinitionService.TypeDefinitionAt(
            tree, document, position.Line, position.Character));

        Assert.Equal("/obj/gun", location.Detail);
    }

    /// <summary>A macro use goes to its <c>#define</c>.</summary>
    [Fact]
    public void A_macro_use_resolves_to_its_define()
    {
        MacroTable macros = Macros("#define AMMO_MAX 30\n", "defs.dm");

        DefinitionLocation location = Assert.Single(Definition(
            "/proc/f()\n\tvar/x = AMMO|_MAX\n", macros));

        Assert.Equal("defs.dm", location.File);
        Assert.Equal("#define AMMO_MAX", location.Detail);
        Assert.Equal("#define ".Length, location.NameSpan.Start);
    }

    /// <summary>A function-like macro renders its parameter list in the detail.</summary>
    [Fact]
    public void A_function_like_macro_shows_its_parameters()
    {
        MacroTable macros = Macros("#define DOUBLE(x) ((x) * 2)\n", "defs.dm");

        DefinitionLocation location = Assert.Single(Definition(
            "/proc/f()\n\treturn DOU|BLE(4)\n", macros));

        Assert.Equal("#define DOUBLE(x)", location.Detail);
    }

    /// <summary>
    /// The macro reading wins over the member reading, because the preprocessor replaces the token
    /// before the parser ever sees it — whatever position it sits in.
    /// </summary>
    [Fact]
    public void A_macro_wins_over_a_member_of_the_same_name()
    {
        MacroTable macros = Macros("#define health 5\n", "defs.dm");

        DefinitionLocation location = Assert.Single(Definition(
            "/mob/guy\n\tvar/health = 1\n/proc/f()\n\tvar/mob/guy/g = new\n\tg.heal|th = 2\n",
            macros));

        Assert.Equal("#define health", location.Detail);
    }

    /// <summary>
    /// The built-in seeds and injected <c>-D</c> defines have no source to open — nothing declares
    /// them, the same rule that keeps builtins out of every other definition answer.
    /// </summary>
    [Fact]
    public void A_predefined_or_injected_macro_resolves_to_nothing()
    {
        MacroTable macros = new();
        macros.SeedPredefined();
        macros.Define(CommandLineDefine.Parse("CBT")!);

        Assert.Empty(Definition("/proc/f()\n\tvar/x = TR|UE\n", macros));
        Assert.Empty(Definition("/proc/f()\n\tvar/x = CB|T\n", macros));
    }
}
