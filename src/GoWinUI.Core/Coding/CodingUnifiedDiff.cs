using System.Globalization;
using System.Text;

namespace GoWinUI.Core.Coding;

/// <summary>A bounded, line-based unified patch of the exact UTF-8 bytes involved in one file action.</summary>
internal static class CodingUnifiedDiff
{
    private const int ContextLines = 3;
    private const int MaximumSearchWork = 1_000_000;
    internal const int MaximumCharacters = 256_000;

    internal sealed record Result(string Text, bool Truncated, int AddedLines, int RemovedLines);
    private sealed record Operation(char Kind, string Line);

    internal static Result Create(string path, byte[]? original, byte[]? updated, CancellationToken cancellationToken)
    {
        var encoding = new UTF8Encoding(false, true);
        // Preserve BOM and line endings: the patch describes bytes, not the normalized tool input.
        var before = Lines(original is null ? string.Empty : encoding.GetString(original));
        var after = Lines(updated is null ? string.Empty : encoding.GetString(updated));
        var operations = Compare(before, after, cancellationToken);
        var additions = operations.Count(static operation => operation.Kind == '+');
        var removals = operations.Count(static operation => operation.Kind == '-');
        if (additions == 0 && removals == 0 && original is not null && updated is not null) return new(string.Empty, false, 0, 0);

        var oldPath = QuotePath("a/" + path);
        var newPath = QuotePath("b/" + path);
        var output = new StringBuilder().Append("diff --git ").Append(oldPath).Append(' ').Append(newPath).Append('\n');
        if (original is null) output.Append("new file mode 100644\n");
        else if (updated is null) output.Append("deleted file mode 100644\n");
        output.Append("--- ").Append(original is null ? "/dev/null" : oldPath).Append('\n')
            .Append("+++ ").Append(updated is null ? "/dev/null" : newPath).Append('\n');
        if (operations.Count == 0) return new(output.ToString(), false, 0, 0);

        var oldLine = 1;
        var newLine = 1;
        var position = 0;
        while (position < operations.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var change = position;
            while (change < operations.Count && operations[change].Kind == ' ') change++;
            if (change == operations.Count) break;
            var start = Math.Max(position, change - ContextLines);
            while (position < start) Advance(operations[position++], ref oldLine, ref newLine);
            var end = change;
            var lastChange = change;
            while (end < operations.Count)
            {
                if (operations[end].Kind != ' ') lastChange = end;
                else if (end - lastChange > ContextLines * 2) break;
                end++;
            }
            end = Math.Min(operations.Count, lastChange + ContextLines + 1);
            var oldCount = 0;
            var newCount = 0;
            for (var index = start; index < end; index++)
            {
                if (operations[index].Kind != '+') oldCount++;
                if (operations[index].Kind != '-') newCount++;
            }
            var hunk = new StringBuilder().Append("@@ -").Append(Range(oldLine, oldCount))
                .Append(" +").Append(Range(newLine, newCount)).Append(" @@\n");
            for (var index = start; index < end; index++)
            {
                var operation = operations[index];
                hunk.Append(operation.Kind).Append(operation.Line);
                if (!operation.Line.EndsWith('\n')) hunk.Append("\n\\ No newline at end of file\n");
                if (output.Length + hunk.Length > MaximumCharacters)
                    return new(output.ToString(), true, additions, removals);
            }
            output.Append(hunk);
            while (position < end) Advance(operations[position++], ref oldLine, ref newLine);
        }
        return new(output.ToString(), false, additions, removals);
    }

    private static string Range(int start, int count) => count == 1
        ? start.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{(count == 0 ? start - 1 : start)},{count}");

    private static void Advance(Operation operation, ref int oldLine, ref int newLine)
    {
        if (operation.Kind != '+') oldLine++;
        if (operation.Kind != '-') newLine++;
    }

    private static List<string> Lines(string text)
    {
        var result = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
            if (text[index] == '\n') { result.Add(text[start..(index + 1)]); start = index + 1; }
        if (start < text.Length) result.Add(text[start..]);
        return result;
    }

    private static List<Operation> Compare(List<string> before, List<string> after, CancellationToken cancellationToken)
    {
        var result = new List<Operation>();
        var prefix = 0;
        while (prefix < before.Count && prefix < after.Count && before[prefix] == after[prefix])
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new(' ', before[prefix++]));
        }
        var oldEnd = before.Count;
        var newEnd = after.Count;
        while (oldEnd > prefix && newEnd > prefix && before[oldEnd - 1] == after[newEnd - 1]) { oldEnd--; newEnd--; }
        // Bounded Myers search; highly different large files fall back to one exact
        // replacement block instead of quadratic memory/time or an invented patch.
        var middle = Myers(before, after, prefix, oldEnd, newEnd, cancellationToken);
        if (middle is null)
        {
            for (var index = prefix; index < oldEnd; index++) result.Add(new('-', before[index]));
            for (var index = prefix; index < newEnd; index++) result.Add(new('+', after[index]));
        }
        else result.AddRange(middle);
        for (var index = oldEnd; index < before.Count; index++) result.Add(new(' ', before[index]));
        return result;
    }

    private static List<Operation>? Myers(List<string> before, List<string> after, int start, int oldEnd, int newEnd,
        CancellationToken cancellationToken)
    {
        var oldCount = oldEnd - start;
        var newCount = newEnd - start;
        if (oldCount == 0) return after.GetRange(start, newCount).Select(static line => new Operation('+', line)).ToList();
        if (newCount == 0) return before.GetRange(start, oldCount).Select(static line => new Operation('-', line)).ToList();
        var trace = new List<Dictionary<int, int>>();
        var previous = new Dictionary<int, int> { [1] = 0 };
        var work = 0;
        for (var distance = 0; distance <= oldCount + newCount; distance++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = new Dictionary<int, int>();
            for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
            {
                if (++work > MaximumSearchWork) return null;
                var x = diagonal == -distance || (diagonal != distance && previous.GetValueOrDefault(diagonal - 1) < previous.GetValueOrDefault(diagonal + 1))
                    ? previous.GetValueOrDefault(diagonal + 1) : previous.GetValueOrDefault(diagonal - 1) + 1;
                var y = x - diagonal;
                while (x < oldCount && y < newCount && before[start + x] == after[start + y])
                {
                    if (++work > MaximumSearchWork) return null;
                    x++;
                    y++;
                }
                next[diagonal] = x;
                if (x >= oldCount && y >= newCount)
                {
                    var result = new List<Operation>();
                    for (var depth = distance; depth > 0; depth--)
                    {
                        var history = trace[depth - 1];
                        var k = x - y;
                        var priorDiagonal = k == -depth || (k != depth && history.GetValueOrDefault(k - 1) < history.GetValueOrDefault(k + 1)) ? k + 1 : k - 1;
                        var priorX = history.GetValueOrDefault(priorDiagonal);
                        var priorY = priorX - priorDiagonal;
                        while (x > priorX && y > priorY) { result.Add(new(' ', before[start + --x])); y--; }
                        if (x == priorX) result.Add(new('+', after[start + --y]));
                        else result.Add(new('-', before[start + --x]));
                    }
                    while (x > 0 && y > 0) { result.Add(new(' ', before[start + --x])); y--; }
                    result.Reverse();
                    return result;
                }
            }
            trace.Add(next);
            previous = next;
        }
        return null;
    }

    private static string QuotePath(string path)
    {
        // Git's quoted path syntax accepts UTF-8 bytes as three-digit octal escapes.
        if (path.All(static value => value is > ' ' and < '\u007f' && value is not ('"' or '\\'))) return path;
        var result = new StringBuilder("\"");
        foreach (var value in Encoding.UTF8.GetBytes(path))
        {
            if (value is >= 32 and < 127 && value is not ((byte)'"' or (byte)'\\')) result.Append((char)value);
            else result.Append('\\').Append(Convert.ToString(value, 8).PadLeft(3, '0'));
        }
        return result.Append('"').ToString();
    }
}
