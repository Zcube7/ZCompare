// Narrow adaptation of number/date formatting behavior from OfficeCLI's
// ExcelDataFormatter.cs. Copyright 2026 OfficeCLI. SPDX-License-Identifier: Apache-2.0.
using System.Globalization;
using System.Text.RegularExpressions;

namespace ZCompare.Core;

internal enum ExcelTemporalKind
{
    None,
    Date,
    DateTime,
    TimeOfDay,
    Duration
}

internal static partial class ExcelDisplayFormatter
{
    private static readonly IReadOnlyDictionary<uint, ExcelTemporalKind> BuiltInTemporalFormats =
        new Dictionary<uint, ExcelTemporalKind>
        {
            [14] = ExcelTemporalKind.Date,
            [15] = ExcelTemporalKind.Date,
            [16] = ExcelTemporalKind.Date,
            [17] = ExcelTemporalKind.Date,
            [18] = ExcelTemporalKind.TimeOfDay,
            [19] = ExcelTemporalKind.TimeOfDay,
            [20] = ExcelTemporalKind.TimeOfDay,
            [21] = ExcelTemporalKind.TimeOfDay,
            [22] = ExcelTemporalKind.DateTime,
            // Locale-dependent East Asian built-in date/time formats.
            [27] = ExcelTemporalKind.Date,
            [28] = ExcelTemporalKind.Date,
            [29] = ExcelTemporalKind.Date,
            [30] = ExcelTemporalKind.Date,
            [31] = ExcelTemporalKind.Date,
            [32] = ExcelTemporalKind.TimeOfDay,
            [33] = ExcelTemporalKind.TimeOfDay,
            [34] = ExcelTemporalKind.Date,
            [35] = ExcelTemporalKind.Date,
            [36] = ExcelTemporalKind.Date,
            [45] = ExcelTemporalKind.TimeOfDay,
            [46] = ExcelTemporalKind.Duration,
            [47] = ExcelTemporalKind.TimeOfDay,
            [50] = ExcelTemporalKind.Date,
            [51] = ExcelTemporalKind.Date,
            [52] = ExcelTemporalKind.Date,
            [53] = ExcelTemporalKind.Date,
            [54] = ExcelTemporalKind.Date,
            [55] = ExcelTemporalKind.Date,
            [56] = ExcelTemporalKind.Date,
            [57] = ExcelTemporalKind.Date,
            [58] = ExcelTemporalKind.Date
        };

    public static bool IsDateFormat(uint numberFormatId, string formatCode) =>
        GetTemporalKind(numberFormatId, formatCode) != ExcelTemporalKind.None;

    public static ExcelTemporalKind GetTemporalKind(uint numberFormatId, string formatCode)
    {
        if (BuiltInTemporalFormats.TryGetValue(numberFormatId, out var builtInKind))
        {
            return builtInKind;
        }

        if (string.IsNullOrWhiteSpace(formatCode) ||
            string.Equals(formatCode.Trim(), "General", StringComparison.OrdinalIgnoreCase))
        {
            return ExcelTemporalKind.None;
        }

        if (ElapsedTimeRegex().IsMatch(formatCode))
        {
            return ExcelTemporalKind.Duration;
        }

        var cleaned = QuotedOrBracketedRegex().Replace(formatCode, string.Empty);
        cleaned = EscapedCharacterRegex().Replace(cleaned, string.Empty);
        cleaned = FractionalSecondsRegex().Replace(cleaned, string.Empty);
        var hasNumericPlaceholder = cleaned.IndexOfAny(['0', '#']) >= 0;
        if (hasNumericPlaceholder)
        {
            return ExcelTemporalKind.None;
        }

        var tokens = ReadTemporalTokens(cleaned);
        var hasDate = false;
        var hasTime = AmPmRegex().IsMatch(cleaned);
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            switch (token.Character)
            {
                case 'y':
                case 'd':
                case 'e':
                case 'g':
                    hasDate = true;
                    break;
                case 'h':
                case 's':
                    hasTime = true;
                    break;
                case 'm':
                    var previous = index > 0 ? tokens[index - 1].Character : '\0';
                    var next = index + 1 < tokens.Count ? tokens[index + 1].Character : '\0';
                    if (previous == 'h' || next == 's')
                    {
                        hasTime = true;
                    }
                    else
                    {
                        hasDate = true;
                    }
                    break;
            }
        }

        return (hasDate, hasTime) switch
        {
            (true, true) => ExcelTemporalKind.DateTime,
            (true, false) => ExcelTemporalKind.Date,
            (false, true) => ExcelTemporalKind.TimeOfDay,
            _ => ExcelTemporalKind.None
        };
    }

    public static string FormatNumber(
        string rawValue,
        ExactNumber number,
        uint numberFormatId,
        string formatCode,
        bool uses1904DateSystem)
    {
        var temporalKind = GetTemporalKind(numberFormatId, formatCode);
        if (temporalKind != ExcelTemporalKind.None && number.TryToDouble(out var serial))
        {
            return FormatTemporal(serial, formatCode, uses1904DateSystem, temporalKind);
        }

        if (formatCode.Contains('%') && number.TryToDouble(out var percentage))
        {
            var decimals = CountPercentageDecimals(formatCode);
            return (percentage * 100d).ToString($"F{decimals}", CultureInfo.InvariantCulture) + "%";
        }

        return rawValue;
    }

    private static string FormatTemporal(
        double serial,
        string formatCode,
        bool uses1904DateSystem,
        ExcelTemporalKind temporalKind)
    {
        if (temporalKind == ExcelTemporalKind.TimeOfDay)
        {
            return FormatTimeOfDay(serial);
        }
        if (temporalKind == ExcelTemporalKind.Duration)
        {
            return FormatDuration(serial, formatCode);
        }

        if (!uses1904DateSystem && serial >= 60d && serial < 61d)
        {
            var fraction = serial - 60d;
            return temporalKind == ExcelTemporalKind.Date || fraction == 0d
                ? "1900-02-29"
                : $"1900-02-29 {FormatTimeOfDay(fraction)}";
        }

        try
        {
            DateTime date;
            if (uses1904DateSystem)
            {
                date = new DateTime(1904, 1, 1).AddDays(serial);
            }
            else
            {
                var adjusted = serial > 60d ? serial - 1d : serial;
                date = new DateTime(1899, 12, 31).AddDays(adjusted);
            }

            return temporalKind switch
            {
                ExcelTemporalKind.DateTime => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                _ => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return serial.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    private static string FormatTimeOfDay(double serial)
    {
        try
        {
            var fraction = serial - Math.Floor(serial);
            var time = TimeSpan.FromDays(fraction);
            return $"{time.Hours:00}:{time.Minutes:00}:{time.Seconds:00}";
        }
        catch (OverflowException)
        {
            return serial.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    private static string FormatDuration(double serial, string formatCode)
    {
        try
        {
            var sign = serial < 0d ? "-" : string.Empty;
            var duration = TimeSpan.FromDays(Math.Abs(serial));
            var match = ElapsedTimeRegex().Match(formatCode);
            var unit = match.Success ? char.ToLowerInvariant(match.Groups["unit"].Value[0]) : 'h';
            return unit switch
            {
                'm' => $"{sign}{Math.Floor(duration.TotalMinutes):0}:{duration.Seconds:00}",
                's' => $"{sign}{Math.Floor(duration.TotalSeconds):0}",
                _ => $"{sign}{Math.Floor(duration.TotalHours):0}:{duration.Minutes:00}:{duration.Seconds:00}"
            };
        }
        catch (OverflowException)
        {
            return serial.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    private static List<TemporalToken> ReadTemporalTokens(string formatCode)
    {
        var result = new List<TemporalToken>();
        var index = 0;
        while (index < formatCode.Length)
        {
            if (!char.IsAsciiLetter(formatCode[index]))
            {
                index++;
                continue;
            }

            var wordStart = index;
            while (index < formatCode.Length && char.IsAsciiLetter(formatCode[index]))
            {
                index++;
            }
            var word = formatCode.AsSpan(wordStart, index - wordStart);
            if (!IsTemporalWord(word))
            {
                continue;
            }

            var tokenStart = 0;
            while (tokenStart < word.Length)
            {
                var character = char.ToLowerInvariant(word[tokenStart]);
                var tokenEnd = tokenStart + 1;
                while (tokenEnd < word.Length &&
                    char.ToLowerInvariant(word[tokenEnd]) == character)
                {
                    tokenEnd++;
                }
                result.Add(new TemporalToken(character));
                tokenStart = tokenEnd;
            }
        }
        return result;
    }

    private static bool IsTemporalWord(ReadOnlySpan<char> word)
    {
        foreach (var character in word)
        {
            if (char.ToLowerInvariant(character) is not ('y' or 'm' or 'd' or 'h' or 's' or 'e' or 'g'))
            {
                return false;
            }
        }
        return word.Length > 0;
    }

    private static int CountPercentageDecimals(string formatCode)
    {
        var percentIndex = formatCode.IndexOf('%');
        var decimalIndex = formatCode.LastIndexOf('.', percentIndex >= 0 ? percentIndex : formatCode.Length - 1);
        if (decimalIndex < 0)
        {
            return 0;
        }

        return formatCode[(decimalIndex + 1)..(percentIndex >= 0 ? percentIndex : formatCode.Length)]
            .Count(static character => character is '0' or '#');
    }

    [GeneratedRegex("\"[^\"]*\"|\\[[^\\]]*\\]")]
    private static partial Regex QuotedOrBracketedRegex();

    [GeneratedRegex("\\\\.|_.")]
    private static partial Regex EscapedCharacterRegex();

    [GeneratedRegex("(?<=s)\\.0+", RegexOptions.IgnoreCase)]
    private static partial Regex FractionalSecondsRegex();

    [GeneratedRegex("\\[(?<unit>h+|m+|s+)\\]", RegexOptions.IgnoreCase)]
    private static partial Regex ElapsedTimeRegex();

    [GeneratedRegex("AM/PM|A/P", RegexOptions.IgnoreCase)]
    private static partial Regex AmPmRegex();

    private readonly record struct TemporalToken(char Character);
}
