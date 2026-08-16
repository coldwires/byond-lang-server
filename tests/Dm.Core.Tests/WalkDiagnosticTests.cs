using Dm.Core.Diagnostics;

namespace Dm.Core.Tests;

/// <summary>
/// The include walk's own diagnostics, which no <c>ParseResult</c> can hold.
/// </summary>
/// <remarks>
/// They belong to the walk rather than to a file's syntax, so a shell assembling
/// <c>parse.Diagnostics + Binder.Bind</c> misses them however complete both halves are. That is
/// what every shell did until 2026-08-16, while <c>dmc diagdiff</c> counted them — so the
/// zero-invented figure was measured over a set the editors never saw.
/// </remarks>
public class WalkDiagnosticTests
{
    /// <summary>
    /// A two-file project where <c>b.dm</c> is the file under test. Two files rather than one
    /// because attribution is most of what these tests are about: a diagnostic has to land on the
    /// file that raised it and not on its neighbour.
    /// </summary>
    private static Workspace TwoFileProject(TempDirectory temp, string body)
    {
        temp.Write("game.dme", "#include \"a.dm\"\n#include \"b.dm\"\n");
        temp.Write("a.dm", "/mob\n\tvar/hp = 1\n");
        temp.Write("b.dm", body);

        return Workspace.Open(Path.Combine(temp.Path, "game.dme"));
    }

    [Fact]
    public void A_warn_echo_reaches_the_file_that_wrote_it()
    {
        using TempDirectory temp = new();
        using Workspace ws = TwoFileProject(temp, "#warn something to say\n/proc/f()\n\treturn 1\n");

        IReadOnlyList<Diagnostic> walk = ws.GetWalkDiagnostics(Path.Combine(temp.Path, "b.dm"));

        Assert.Contains(walk, d => d.Message.Contains("something to say"));
    }

    /// <summary>
    /// The attribution is the point of the call: a walk diagnostic used to land on the .dme at
    /// line 0, which made every one of them a guaranteed miss against dm.exe.
    /// </summary>
    [Fact]
    public void It_is_attributed_to_its_own_file_and_not_to_its_neighbour()
    {
        using TempDirectory temp = new();
        using Workspace ws = TwoFileProject(temp, "#warn only in b\n");

        Assert.Empty(ws.GetWalkDiagnostics(Path.Combine(temp.Path, "a.dm")));
        Assert.NotEmpty(ws.GetWalkDiagnostics(Path.Combine(temp.Path, "b.dm")));
    }

    /// <summary>
    /// The span has to index the file it is reported against, or a shell converting it through
    /// that document's text puts the squiggle on the wrong line.
    /// </summary>
    [Fact]
    public void The_span_indexes_the_file_it_is_reported_against()
    {
        using TempDirectory temp = new();
        using Workspace ws = TwoFileProject(temp, "/proc/f()\n\treturn 1\n#warn on the third line\n");

        Diagnostic walk = Assert.Single(
            ws.GetWalkDiagnostics(Path.Combine(temp.Path, "b.dm")),
            d => d.Message.Contains("on the third line"));

        Dm.Core.Text.SourceText text = ws.GetFileText(Path.Combine(temp.Path, "b.dm"))!;

        Assert.Equal(2, text.GetLinePosition(walk.Span.Start).Line);
    }

    [Fact]
    public void A_clean_project_answers_empty()
    {
        using TempDirectory temp = new();
        using Workspace ws = TwoFileProject(temp, "/proc/f()\n\treturn 1\n");

        Assert.Empty(ws.GetWalkDiagnostics(Path.Combine(temp.Path, "b.dm")));
    }

    /// <summary>
    /// A rebuild has to re-collect them. They are dropped with the tree, so a stale list would
    /// outlive the edit that fixed the warning — which is worse than never showing it.
    /// </summary>
    [Fact]
    public void An_edit_that_removes_the_directive_removes_the_diagnostic()
    {
        using TempDirectory temp = new();
        using Workspace ws = TwoFileProject(temp, "#warn still here\n");

        string b = Path.Combine(temp.Path, "b.dm");
        Assert.NotEmpty(ws.GetWalkDiagnostics(b));

        ws.SetBuffer(b, "/proc/f()\n\treturn 1\n");

        Assert.Empty(ws.GetWalkDiagnostics(b));
    }

    /// <summary>
    /// A LEXER diagnostic must not be reported twice.
    /// </summary>
    /// <remarks>
    /// This is the trap in surfacing the walk's list at all, and it only exists because of what
    /// landed on 2026-08-16: the lexer's diagnostics now reach the parse AND the walk keeps its own
    /// copy of them (<c>IncludeGraph</c> adds <c>lex.Diagnostics</c> as it walks). A shell adding
    /// <c>parse.Diagnostics</c> and the walk's list together would double every unterminated string
    /// in the project. Whatever the split, the sum has to be one per site.
    /// </remarks>
    [Fact]
    public void A_lexer_diagnostic_is_not_reported_twice()
    {
        using TempDirectory temp = new();
        using Workspace ws = TwoFileProject(temp, "/proc/f()\n\tvar/s = \"unterminated\n");

        string b = Path.Combine(temp.Path, "b.dm");

        List<Diagnostic> all = new(ws.GetDocument(b).Parse.Diagnostics);
        all.AddRange(ws.GetWalkDiagnostics(b));

        Assert.Equal(
            1,
            all.Count(d => d.Message.Contains("unterminated", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// A standalone workspace has no include walk at all, so there is nothing to answer with and
    /// asking is not an error.
    /// </summary>
    [Fact]
    public void A_standalone_workspace_answers_empty()
    {
        using TempDirectory temp = new();
        temp.Write("loose.dm", "#warn nothing walks this\n");

        using Workspace ws = Workspace.OpenStandalone(temp.Path);

        Assert.Empty(ws.GetWalkDiagnostics(Path.Combine(temp.Path, "loose.dm")));
    }
}
