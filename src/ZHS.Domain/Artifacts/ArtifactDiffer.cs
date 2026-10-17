namespace ZHS.Domain.Artifacts;

public enum DiffLineKind
{
    Unchanged,
    Added,
    Removed,
}

public sealed record DiffLine(DiffLineKind Kind, string Text)
{
    public string Display => Kind switch
    {
        DiffLineKind.Added => "+ " + Text,
        DiffLineKind.Removed => "- " + Text,
        _ => "  " + Text,
    };
}

/// <summary>
/// 行级 LCS diff（M4 基本差异查看）。O(n·m) 动态规划，超过行数上限时截断并给出提示行
/// （大文档的侧栏预览场景足够；结构化 diff 留给专业编辑器阶段）。
/// </summary>
public static class ArtifactDiffer
{
    public const int MaxLines = 1500;

    public static IReadOnlyList<DiffLine> Diff(string left, string right)
    {
        var leftLines = Split(left);
        var rightLines = Split(right);
        if (leftLines.Count > MaxLines || rightLines.Count > MaxLines)
        {
            var truncated = new List<DiffLine> { new(DiffLineKind.Removed, $"⚠ 文档超过 {MaxLines} 行，diff 已截断") };
            truncated.AddRange(DiffCore(leftLines.Take(MaxLines).ToList(), rightLines.Take(MaxLines).ToList()));
            return truncated;
        }

        return DiffCore(leftLines, rightLines);
    }

    private static List<string> Split(string content)
        => content.Replace("\r\n", "\n").Split('\n').ToList();

    private static IReadOnlyList<DiffLine> DiffCore(List<string> left, List<string> right)
    {
        var rows = left.Count + 1;
        var columns = right.Count + 1;
        var table = new int[rows, columns];
        for (var i = rows - 2; i >= 0; i--)
        {
            for (var j = columns - 2; j >= 0; j--)
            {
                table[i, j] = left[i] == right[j]
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var result = new List<DiffLine>(left.Count + right.Count);
        var x = 0;
        var y = 0;
        while (x < left.Count && y < right.Count)
        {
            if (left[x] == right[y])
            {
                result.Add(new DiffLine(DiffLineKind.Unchanged, left[x]));
                x++;
                y++;
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                result.Add(new DiffLine(DiffLineKind.Removed, left[x]));
                x++;
            }
            else
            {
                result.Add(new DiffLine(DiffLineKind.Added, right[y]));
                y++;
            }
        }

        while (x < left.Count)
        {
            result.Add(new DiffLine(DiffLineKind.Removed, left[x++]));
        }

        while (y < right.Count)
        {
            result.Add(new DiffLine(DiffLineKind.Added, right[y++]));
        }

        return result;
    }
}
