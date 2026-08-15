using System.Collections.Generic;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Symbols;

/// <summary>
/// <see cref="ObjectTree.FindOverriddenProc"/> is public API, so these pin its answers on the
/// three shapes DM actually writes: a bare override of a builtin, an override of a project proc,
/// and a fresh declaration that overrides nothing.
/// </summary>
public class ObjectTreeOverrideTests
{
    private static ObjectTree Build(string source, bool withBuiltins = true)
    {
        ObjectTree tree = withBuiltins ? Builtins.CreateTree() : new ObjectTree();

        TypeTreeBuilder.AddFile(
            tree, "test.dm", DeclarationParser.Parse(Lexer.Lex(SourceText.From(source))));

        return tree;
    }

    /// <summary>
    /// The one the doc comment calls the most common override of all: a project redefining a
    /// builtin on the type that already carries it, with no <c>proc/</c> segment.
    /// </summary>
    [Fact]
    public void A_bare_override_of_a_builtin_is_an_override()
    {
        ObjectTree tree = Build("/mob\n\tLogin()\n\t\treturn 1\n");

        (TypePath Owner, bool IsBuiltin)? found =
            tree.FindOverriddenProc(TypePath.Parse("/mob"), "Login");

        Assert.NotNull(found);
        Assert.True(found!.Value.IsBuiltin);
        Assert.Equal("/mob", found.Value.Owner.Text);
    }

    /// <summary>An override of a project proc names the ancestor that declared it.</summary>
    [Fact]
    public void An_override_of_a_project_proc_names_the_declaring_ancestor()
    {
        ObjectTree tree = Build(
            "/datum/base\n\tproc/greet()\n\t\treturn 1\n/datum/base/child\n\tgreet()\n\t\treturn 2\n");

        (TypePath Owner, bool IsBuiltin)? found =
            tree.FindOverriddenProc(TypePath.Parse("/datum/base/child"), "greet");

        Assert.NotNull(found);
        Assert.False(found!.Value.IsBuiltin);
        Assert.Equal("/datum/base", found.Value.Owner.Text);
    }

    /// <summary>A fresh declaration overrides nothing, which is what dm.exe's no_parent says.</summary>
    [Fact]
    public void A_fresh_declaration_overrides_nothing()
    {
        ObjectTree tree = Build("/datum/solo\n\tproc/fresh()\n\t\treturn 1\n");

        Assert.Null(tree.FindOverriddenProc(TypePath.Parse("/datum/solo"), "fresh"));
    }

    /// <summary>An unknown type answers null rather than throwing.</summary>
    [Fact]
    public void An_unknown_type_is_null()
        => Assert.Null(Build("/datum/a\n").FindOverriddenProc(TypePath.Parse("/no/such"), "f"));
}
