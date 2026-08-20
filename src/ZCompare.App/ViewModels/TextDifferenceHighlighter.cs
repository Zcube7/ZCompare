using System.Text;

namespace ZCompare.App.ViewModels;

internal sealed record TextDifferenceSegment(string Text, bool IsDifferent);

internal sealed record DetailTextSegment(
    string DisplayText,
    string ClipboardText,
    bool IsDifferent);

internal sealed record CellDetailsContent(
    string ClipboardText,
    IReadOnlyList<DetailTextSegment> Segments);

internal static class TextDifferenceHighlighter
{
    private const int MaximumEditDistance = 256;
    private const int MaximumDetailedLength = 20_000;

    public static IReadOnlyList<TextDifferenceSegment> CreateSegments(
        string text,
        string oppositeText,
        bool highlightDifferences)
    {
        if (text.Length == 0)
        {
            return [];
        }

        if (!highlightDifferences || string.Equals(text, oppositeText, StringComparison.Ordinal))
        {
            return [new TextDifferenceSegment(text, false)];
        }

        var prefixLength = CommonPrefixLength(text, oppositeText);
        var suffixLength = CommonSuffixLength(text, oppositeText, prefixLength);
        var textMiddle = text.AsSpan(prefixLength, text.Length - prefixLength - suffixLength);
        var oppositeMiddle = oppositeText.AsSpan(
            prefixLength,
            oppositeText.Length - prefixLength - suffixLength);
        var segments = new List<TextDifferenceSegment>(5);

        AddSegment(segments, text[..prefixLength], false);
        if (textMiddle.Length > 0)
        {
            if (textMiddle.Length + oppositeMiddle.Length <= MaximumDetailedLength &&
                !ContainsSurrogate(textMiddle) &&
                !ContainsSurrogate(oppositeMiddle) &&
                TryCreateDetailedSegments(textMiddle.ToString(), oppositeMiddle.ToString(), out var detailed))
            {
                foreach (var segment in detailed)
                {
                    AddSegment(segments, segment.Text, segment.IsDifferent);
                }
            }
            else
            {
                AddSegment(segments, textMiddle.ToString(), true);
            }
        }

        if (suffixLength > 0)
        {
            AddSegment(segments, text[^suffixLength..], false);
        }

        return segments;
    }

    private static bool TryCreateDetailedSegments(
        string text,
        string oppositeText,
        out IReadOnlyList<TextDifferenceSegment> segments)
    {
        var sourceLength = text.Length;
        var targetLength = oppositeText.Length;
        var maximum = sourceLength + targetLength;
        var previous = new Dictionary<int, int> { [1] = 0 };
        var trace = new List<Dictionary<int, int>>(
            Math.Min(maximum, MaximumEditDistance) + 1);

        for (var distance = 0; distance <= Math.Min(maximum, MaximumEditDistance); distance++)
        {
            var current = new Dictionary<int, int>((distance * 2) + 1);
            for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
            {
                var moveDown = diagonal == -distance ||
                    (diagonal != distance && Get(previous, diagonal - 1) < Get(previous, diagonal + 1));
                var x = moveDown
                    ? Get(previous, diagonal + 1)
                    : Get(previous, diagonal - 1) + 1;
                var y = x - diagonal;
                while (x < sourceLength && y < targetLength && text[x] == oppositeText[y])
                {
                    x++;
                    y++;
                }

                current[diagonal] = x;
                if (x < sourceLength || y < targetLength)
                {
                    continue;
                }

                trace.Add(current);
                segments = Backtrack(text, oppositeText, trace);
                return true;
            }

            trace.Add(current);
            previous = current;
        }

        segments = [];
        return false;
    }

    private static IReadOnlyList<TextDifferenceSegment> Backtrack(
        string text,
        string oppositeText,
        IReadOnlyList<Dictionary<int, int>> trace)
    {
        var edits = new List<(char Character, EditKind Kind)>(text.Length + oppositeText.Length);
        var x = text.Length;
        var y = oppositeText.Length;

        for (var distance = trace.Count - 1; distance > 0; distance--)
        {
            var previous = trace[distance - 1];
            var diagonal = x - y;
            var moveDown = diagonal == -distance ||
                (diagonal != distance && Get(previous, diagonal - 1) < Get(previous, diagonal + 1));
            var previousDiagonal = moveDown ? diagonal + 1 : diagonal - 1;
            var previousX = Get(previous, previousDiagonal);
            var previousY = previousX - previousDiagonal;

            while (x > previousX && y > previousY)
            {
                edits.Add((text[x - 1], EditKind.Equal));
                x--;
                y--;
            }

            if (moveDown)
            {
                edits.Add((oppositeText[y - 1], EditKind.Inserted));
                y--;
            }
            else
            {
                edits.Add((text[x - 1], EditKind.Deleted));
                x--;
            }
        }

        while (x > 0 && y > 0)
        {
            edits.Add((text[x - 1], EditKind.Equal));
            x--;
            y--;
        }

        while (x > 0)
        {
            edits.Add((text[--x], EditKind.Deleted));
        }

        while (y > 0)
        {
            edits.Add((oppositeText[--y], EditKind.Inserted));
        }

        edits.Reverse();
        var segments = new List<TextDifferenceSegment>();
        var builder = new StringBuilder();
        bool? currentDifference = null;
        foreach (var edit in edits)
        {
            if (edit.Kind == EditKind.Inserted)
            {
                continue;
            }

            var isDifferent = edit.Kind == EditKind.Deleted;
            if (currentDifference != isDifferent)
            {
                AddSegment(segments, builder.ToString(), currentDifference == true);
                builder.Clear();
                currentDifference = isDifferent;
            }

            builder.Append(edit.Character);
        }

        AddSegment(segments, builder.ToString(), currentDifference == true);
        return segments;
    }

    private static int CommonPrefixLength(string left, string right)
    {
        var maximum = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < maximum && left[index] == right[index])
        {
            index++;
        }

        return index > 0 &&
            index < left.Length &&
            index < right.Length &&
            char.IsHighSurrogate(left[index - 1])
                ? index - 1
                : index;
    }

    private static int CommonSuffixLength(string left, string right, int prefixLength)
    {
        var maximum = Math.Min(left.Length, right.Length) - prefixLength;
        var length = 0;
        while (length < maximum && left[^(length + 1)] == right[^(length + 1)])
        {
            length++;
        }

        var leftStart = left.Length - length;
        var rightStart = right.Length - length;
        return length > 0 &&
            leftStart < left.Length &&
            rightStart < right.Length &&
            (char.IsLowSurrogate(left[leftStart]) || char.IsLowSurrogate(right[rightStart]))
                ? length - 1
                : length;
    }

    private static bool ContainsSurrogate(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (char.IsSurrogate(character))
            {
                return true;
            }
        }

        return false;
    }

    private static int Get(IReadOnlyDictionary<int, int> values, int key) =>
        values.TryGetValue(key, out var value) ? value : -1;

    private static void AddSegment(
        ICollection<TextDifferenceSegment> segments,
        string text,
        bool isDifferent)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (segments is List<TextDifferenceSegment> list &&
            list.Count > 0 &&
            list[^1].IsDifferent == isDifferent)
        {
            list[^1] = list[^1] with { Text = list[^1].Text + text };
            return;
        }

        segments.Add(new TextDifferenceSegment(text, isDifferent));
    }

    private enum EditKind
    {
        Equal,
        Deleted,
        Inserted,
    }
}
