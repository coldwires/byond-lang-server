using System.Collections.Generic;
using System.Linq;
using Dm.Core.Services;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Services;

/// <summary>
/// Rename: provable edits from the binder's own walk, everything unprovable REPORTED rather than
/// guessed. The uncertain list is the product — a rename that misses a live site does not error,
/// it silently changes what a game does.
/// </summary>
public class RenameServiceTests
{
    /// <summary>
    /// Renames at the caret marked <c>|</c> in the first source; the rest are further files.
    /// </summary>
    private static RenameResult Rename(string newName, string sourceWithCaret, params string[] more)
    {
        int caret = sourceWithCaret.IndexOf('|');
        Assert.True(caret >= 0, "the source must mark the caret with |");

        string source = sourceWithCaret.Remove(caret, 1);
        Document document = new("file0.dm", SourceText.From(source), fromBuffer: true);

        ObjectTree tree = new();
        Builtins.Seed(tree);
        TypeTreeBuilder.AddFile(tree, "file0.dm", document.Parse);

        List<(string, ParseResult)> files = new() { ("file0.dm", document.Parse) };
        List<LexResult> lexes = new() { document.Lex };

        for (int i = 0; i < more.Length; i++)
        {
            LexResult lex = Lexer.Lex(SourceText.From(more[i]));
            files.Add(($"file{i + 1}.dm", DeclarationParser.Parse(lex)));
            lexes.Add(lex);
            TypeTreeBuilder.AddFile(tree, $"file{i + 1}.dm", files[^1].Item2);
        }

        LinePosition position = document.Text.GetLinePosition(caret);

        return RenameService.RenameAt(
            tree, files, document, position.Line, position.Character, newName,
            lexFor: f => lexes[f[4] - '0']);
    }

    [Fact]
    public void A_member_var_renames_its_declaration_and_every_proven_use()
    {
        const string Types = "/mob/guy\n\tvar/hp = 1\n\tproc/heal(amount)\n\t\thp| += amount\n";
        const string Uses =
            "/proc/f()\n\tvar/mob/guy/g = new\n\tg.hp = 5\n\treturn mk().hp + g:hp\n"
            + "/proc/mk()\n\treturn new /mob/guy\n";

        RenameResult result = Rename("health", Types, Uses);

        Assert.Equal(RenameRefusal.None, result.Refusal);
        Assert.Equal("/mob/guy/hp", result.Target);

        // The declaration, the bare write inside heal, and the typed receiver's write.
        Assert.Equal(2, result.Edits.Count(e => e.File == "file0.dm"));
        Assert.Single(result.Edits, e => e.File == "file1.dm");

        // `mk().hp` has no written receiver type and `g:hp` is a colon lookup: both reported,
        // neither touched.
        Assert.Contains(result.Uncertain, u => u.Reason == UncertainReason.UntypedReceiver);
        Assert.Contains(result.Uncertain, u => u.Reason == UncertainReason.ColonAccess);
        Assert.Equal(2, result.Uncertain.Count);
    }

    /// <summary>The override chain renames as a family, matching the index's canonical target.</summary>
    [Fact]
    public void A_proc_rename_includes_its_overrides()
    {
        const string Source =
            "/mob/guy\n\tproc/he|al(amount)\n\t\treturn amount\n"
            + "/mob/guy/child\n\theal(amount)\n\t\treturn ..()\n"
            + "/proc/f()\n\tvar/mob/guy/g = new\n\treturn g.heal(1)\n";

        RenameResult result = Rename("mend", Source);

        Assert.Equal(RenameRefusal.None, result.Refusal);
        Assert.Equal("/mob/guy/heal()", result.Target);

        // The base declaration, the override's declaration, and the call.
        Assert.Equal(3, result.Edits.Count);
    }

    /// <summary>
    /// <c>call("name")</c> dispatches on a string no resolver sees, so a literal carrying the
    /// name as a whole word is reported. <c>attack_verb</c> is a different word.
    /// </summary>
    [Fact]
    public void A_string_literal_is_flagged_on_a_whole_word_only()
    {
        const string Source =
            "/mob/guy\n\tproc/att|ack()\n\t\treturn 1\n"
            + "/proc/f()\n\tvar/mob/guy/g = new\n\tg.attack()\n"
            + "\tcall(g, \"attack\")()\n\tworld << \"attack_verb\"\n";

        RenameResult result = Rename("strike", Source);

        UncertainSite site = Assert.Single(result.Uncertain);
        Assert.Equal(UncertainReason.StringLiteral, site.Reason);
    }

    [Fact]
    public void Builtins_types_locals_and_bad_names_are_refused_with_the_reason()
    {
        Assert.Equal(RenameRefusal.Builtin,
            Rename("place", "/proc/f()\n\tvar/mob/m = new\n\treturn m.lo|c\n").Refusal);

        Assert.Equal(RenameRefusal.Type,
            Rename("gun", "/obj/item\n/proc/f()\n\treturn /obj/it|em\n").Refusal);

        // An untyped local resolves to nothing; a typed one resolves to its TYPE through
        // definition, and the guard keeps that from reading as a type rename.
        Assert.Equal(RenameRefusal.NothingAtPosition,
            Rename("y", "/proc/f()\n\tvar/x| = 1\n\treturn x\n").Refusal);
        Assert.Equal(RenameRefusal.NothingAtPosition,
            Rename("other", "/proc/f()\n\tvar/mob/m| = new\n\treturn m\n").Refusal);

        Assert.Equal(RenameRefusal.InvalidName, Rename("1bad", "/mob/guy\n\tvar/h|p = 1\n").Refusal);
        Assert.Equal(RenameRefusal.InvalidName, Rename("two words", "/mob/guy\n\tvar/h|p = 1\n").Refusal);
        Assert.Equal(RenameRefusal.InvalidName, Rename("var", "/mob/guy\n\tvar/h|p = 1\n").Refusal);
    }

    /// <summary>
    /// <c>proc</c> and <c>verb</c> lex as ordinary identifiers and a var named either compiles —
    /// probed against dm.exe 516.1686, fixture <c>ok/parsing.dm</c> — so they are legal new names.
    /// </summary>
    [Fact]
    public void Proc_is_a_legal_new_name_because_dm_accepts_a_var_named_proc()
    {
        Assert.Equal(RenameRefusal.None, Rename("proc", "/mob/guy\n\tvar/h|p = 1\n").Refusal);
    }

    /// <summary>Edits never overlap and arrive ordered, which is what an applier needs.</summary>
    [Fact]
    public void Edits_are_deduplicated_and_ordered()
    {
        const string Source =
            "/mob/guy\n\tvar/hp = 1\n\tproc/heal()\n\t\thp| = hp + hp\n";

        RenameResult result = Rename("health", Source);

        Assert.Equal(4, result.Edits.Count);

        for (int i = 1; i < result.Edits.Count; i++)
        {
            Assert.True(string.CompareOrdinal(result.Edits[i - 1].File, result.Edits[i].File) <= 0);

            if (result.Edits[i - 1].File == result.Edits[i].File)
                Assert.True(result.Edits[i - 1].Span.Start < result.Edits[i].Span.Start);
        }
    }
}
