using System.Text;

namespace Dm.Core.Tests;

/// <summary>
/// Compares generated text against a checked-in expected file.
/// </summary>
/// <remarks>
/// Used by the lexer, parser, and object-tree tests, where the natural fixture layout is a
/// <c>.dm</c> input beside its expected output. Set <c>DM_UPDATE_SNAPSHOTS=1</c> to rewrite the
/// expected files instead of failing, then read the diff in git before committing it.
///
/// Rolled by hand rather than pulling in a snapshot library: the output formats are ours to design
/// and have to stay readable as git diffs, and nothing here needs a diff-tool launcher.
/// </remarks>
public static class Snapshot
{
    private static bool UpdateRequested =>
        Environment.GetEnvironmentVariable("DM_UPDATE_SNAPSHOTS") is "1" or "true";

    public static void Matches(string actual, string expectedPath)
    {
        string normalized = Normalize(actual);

        if (UpdateRequested)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, normalized, new UTF8Encoding(false));
            return;
        }

        if (!File.Exists(expectedPath))
        {
            throw new SnapshotException(
                $"No snapshot at {expectedPath}.{Environment.NewLine}" +
                $"Re-run with DM_UPDATE_SNAPSHOTS=1 to create it.{Environment.NewLine}" +
                $"Actual output:{Environment.NewLine}{normalized}");
        }

        string expected = Normalize(File.ReadAllText(expectedPath));

        if (expected == normalized)
            return;

        throw new SnapshotException(Describe(expectedPath, expected, normalized));
    }

    private static string Normalize(string text)
        => text.Replace("\r\n", "\n").TrimEnd() + "\n";

    private static string Describe(string expectedPath, string expected, string actual)
    {
        string[] expectedLines = expected.Split('\n');
        string[] actualLines = actual.Split('\n');

        int line = 0;
        while (line < expectedLines.Length &&
               line < actualLines.Length &&
               expectedLines[line] == actualLines[line])
        {
            line++;
        }

        StringBuilder message = new();
        message.AppendLine($"Snapshot mismatch: {expectedPath}");
        message.AppendLine($"First difference at line {line + 1}.");
        message.AppendLine($"  expected: {Show(expectedLines, line)}");
        message.AppendLine($"  actual:   {Show(actualLines, line)}");
        message.AppendLine();
        message.AppendLine("Re-run with DM_UPDATE_SNAPSHOTS=1 to accept the new output.");
        return message.ToString();
    }

    private static string Show(string[] lines, int index)
        => index < lines.Length ? $"\"{lines[index]}\"" : "<end of file>";
}

public sealed class SnapshotException : Exception
{
    public SnapshotException(string message) : base(message)
    {
    }
}
