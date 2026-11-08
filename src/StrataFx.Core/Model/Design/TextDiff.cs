namespace StrataFx.Model.Design;

public enum TextDiffKind
{
    Same,
    Added,
    Removed,
}

/// <summary>One line of a line-by-line diff.</summary>
public readonly record struct TextDiffLine(TextDiffKind Kind, string Text);

/// <summary>A small line diff (longest common subsequence) for showing what a save will write. Meant for model files, not large texts.</summary>
public static class TextDiff
{
    public static IReadOnlyList<TextDiffLine> Lines(string? before, string? after)
    {
        var a = Split(before);
        var b = Split(after);
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var lines = new List<TextDiffLine>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { lines.Add(new(TextDiffKind.Same, a[x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) lines.Add(new(TextDiffKind.Removed, a[x++]));
            else lines.Add(new(TextDiffKind.Added, b[y++]));
        }
        while (x < a.Length) lines.Add(new(TextDiffKind.Removed, a[x++]));
        while (y < b.Length) lines.Add(new(TextDiffKind.Added, b[y++]));
        return lines;
    }

    /// <summary>
    /// The changed lines with <paramref name="context"/> unchanged lines around them; null entries mark skipped lines.
    /// </summary>
    public static IReadOnlyList<TextDiffLine?> Hunks(IReadOnlyList<TextDiffLine> lines, int context = 2)
    {
        var keep = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Kind != TextDiffKind.Same)
                for (var j = Math.Max(0, i - context); j <= Math.Min(lines.Count - 1, i + context); j++) keep[j] = true;

        var result = new List<TextDiffLine?>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (keep[i]) result.Add(lines[i]);
            else if (result.Count == 0 || result[^1] is not null) result.Add(null);
        }
        return result;
    }

    private static string[] Split(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
}
