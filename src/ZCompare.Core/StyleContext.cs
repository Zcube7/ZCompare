using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace ZCompare.Core;

internal sealed class StyleContext
{
    private readonly IReadOnlyList<CellFormat> _cellFormats;
    private readonly IReadOnlyList<CellFormat> _baseFormats;
    private readonly IReadOnlyList<Font> _fonts;
    private readonly IReadOnlyList<Fill> _fills;
    private readonly IReadOnlyList<Border> _borders;
    private readonly IReadOnlyDictionary<uint, string> _numberFormats;
    private readonly IReadOnlyDictionary<uint, string> _themeColors;
    private readonly Dictionary<uint, ResolvedCellFormat> _resolvedFormats = [];

    private StyleContext(
        IReadOnlyList<CellFormat> cellFormats,
        IReadOnlyList<CellFormat> baseFormats,
        IReadOnlyList<Font> fonts,
        IReadOnlyList<Fill> fills,
        IReadOnlyList<Border> borders,
        IReadOnlyDictionary<uint, string> numberFormats,
        IReadOnlyDictionary<uint, string> themeColors)
    {
        _cellFormats = cellFormats;
        _baseFormats = baseFormats;
        _fonts = fonts;
        _fills = fills;
        _borders = borders;
        _numberFormats = numberFormats;
        _themeColors = themeColors;
    }

    public static StyleContext Create(WorkbookPart workbookPart)
    {
        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        if (stylesheet is null)
        {
            return new StyleContext([], [], [], [], [], BuiltInFormats, ReadThemeColors(workbookPart.ThemePart));
        }

        var numberFormats = new Dictionary<uint, string>(BuiltInFormats);
        foreach (var format in stylesheet.NumberingFormats?.Elements<NumberingFormat>() ?? [])
        {
            if (format.NumberFormatId?.Value is { } id)
            {
                numberFormats[id] = format.FormatCode?.Value ?? string.Empty;
            }
        }

        return new StyleContext(
            stylesheet.CellFormats?.Elements<CellFormat>().ToArray() ?? [],
            stylesheet.CellStyleFormats?.Elements<CellFormat>().ToArray() ?? [],
            stylesheet.Fonts?.Elements<Font>().ToArray() ?? [],
            stylesheet.Fills?.Elements<Fill>().ToArray() ?? [],
            stylesheet.Borders?.Elements<Border>().ToArray() ?? [],
            numberFormats,
            ReadThemeColors(workbookPart.ThemePart));
    }

    public ResolvedCellFormat GetFormat(uint styleIndex)
    {
        if (_resolvedFormats.TryGetValue(styleIndex, out var cached))
        {
            return cached;
        }

        var cellFormat = styleIndex < _cellFormats.Count ? _cellFormats[(int)styleIndex] : null;
        var baseIndex = ReadUIntAttribute(cellFormat, "xfId");
        var baseFormat = baseIndex < _baseFormats.Count ? _baseFormats[(int)baseIndex] : null;
        var numberFormatId = ResolveComponentId(
            cellFormat,
            baseFormat,
            "numFmtId",
            "applyNumberFormat");
        var fontId = ResolveComponentId(cellFormat, baseFormat, "fontId", "applyFont");
        var fillId = ResolveComponentId(cellFormat, baseFormat, "fillId", "applyFill");
        var borderId = ResolveComponentId(cellFormat, baseFormat, "borderId", "applyBorder");

        var font = fontId < _fonts.Count ? _fonts[(int)fontId] : null;
        var fill = fillId < _fills.Count ? _fills[(int)fillId] : null;
        var border = borderId < _borders.Count ? _borders[(int)borderId] : null;
        var alignment = ReadNullableBooleanAttribute(cellFormat, "applyAlignment") == false
            ? baseFormat?.Alignment
            : cellFormat?.Alignment ?? baseFormat?.Alignment;
        var numberFormatCode = _numberFormats.TryGetValue(numberFormatId, out var code) ? code : "General";

        var snapshot = new CellFormatSnapshot(
            numberFormatCode,
            CanonicalElement(font),
            CanonicalElement(fill),
            CanonicalElement(border),
            CanonicalElement(alignment),
            ResolveColor(font?.Color) ?? "FF000000",
            ResolveFillColor(fill) ?? "FFFFFFFF",
            font?.FontName?.Val?.Value,
            font?.FontSize?.Val?.Value,
            ReadBooleanStyleProperty(font?.Bold),
            ReadBooleanStyleProperty(font?.Italic),
            alignment?.Horizontal?.Value.ToString(),
            alignment?.Vertical?.Value.ToString(),
            alignment?.WrapText?.Value ?? false);
        var resolved = new ResolvedCellFormat(numberFormatId, snapshot);
        _resolvedFormats[styleIndex] = resolved;
        return resolved;
    }

    private string? ResolveFillColor(Fill? fill)
    {
        if (fill?.PatternFill is not { } pattern)
        {
            return null;
        }

        return ResolveColor(pattern.ForegroundColor) ?? ResolveColor(pattern.BackgroundColor);
    }

    private string CanonicalElement(OpenXmlElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        AppendCanonical(builder, element);
        return builder.ToString();
    }

    private void AppendCanonical(StringBuilder builder, OpenXmlElement element)
    {
        builder.Append('<').Append(element.LocalName);
        var resolvedColor = IsColorElement(element) ? ResolveColor(element) : null;
        if (resolvedColor is not null)
        {
            builder.Append(" color=").Append(resolvedColor);
        }

        foreach (var attribute in element.GetAttributes()
            .Where(attribute => resolvedColor is null || attribute.LocalName is not ("rgb" or "theme" or "indexed" or "tint" or "auto"))
            .Where(attribute => !IsBooleanStyleProperty(element) || attribute.LocalName != "val")
            .OrderBy(static attribute => attribute.LocalName, StringComparer.Ordinal)
            .ThenBy(static attribute => attribute.NamespaceUri, StringComparer.Ordinal))
        {
            builder.Append(' ').Append(attribute.LocalName).Append('=').Append(attribute.Value);
        }
        builder.Append('>');

        foreach (var child in element.ChildElements)
        {
            if (IsBooleanStyleProperty(child) && !ReadBooleanStyleProperty(child))
            {
                continue;
            }
            AppendCanonical(builder, child);
        }
        if (!string.IsNullOrEmpty(element.InnerText) && element.ChildElements.Count == 0)
        {
            builder.Append(element.InnerText);
        }
        builder.Append("</").Append(element.LocalName).Append('>');
    }

    private string? ResolveColor(OpenXmlElement? color)
    {
        if (color is null)
        {
            return null;
        }

        var attributes = color.GetAttributes().ToDictionary(
            static attribute => attribute.LocalName,
            static attribute => attribute.Value,
            StringComparer.OrdinalIgnoreCase);
        string? rgb = null;
        if (attributes.TryGetValue("rgb", out var direct) && direct is not null)
        {
            rgb = TryNormalizeArgb(direct);
        }
        else if (attributes.TryGetValue("theme", out var themeText) &&
            uint.TryParse(themeText, NumberStyles.None, CultureInfo.InvariantCulture, out var themeIndex) &&
            _themeColors.TryGetValue(themeIndex, out var themeColor))
        {
            rgb = themeColor;
        }
        else if (attributes.TryGetValue("indexed", out var indexedText) &&
            uint.TryParse(indexedText, NumberStyles.None, CultureInfo.InvariantCulture, out var indexed))
        {
            rgb = IndexedColor(indexed);
        }
        else if (attributes.TryGetValue("auto", out var automatic) && automatic is "1" or "true")
        {
            rgb = "FF000000";
        }

        if (rgb is not null &&
            attributes.TryGetValue("tint", out var tintText) &&
            double.TryParse(tintText, NumberStyles.Float, CultureInfo.InvariantCulture, out var tint))
        {
            rgb = ApplyTint(rgb, tint);
        }
        return rgb;
    }

    private static bool IsColorElement(OpenXmlElement element) =>
        element.LocalName.EndsWith("Color", StringComparison.OrdinalIgnoreCase) ||
        element.LocalName is "color" or "fgColor" or "bgColor";

    private static uint ResolveComponentId(
        CellFormat? direct,
        CellFormat? inherited,
        string attributeName,
        string applyAttributeName)
    {
        if (ReadNullableBooleanAttribute(direct, applyAttributeName) == false)
        {
            return ReadNullableUIntAttribute(inherited, attributeName) ?? 0u;
        }
        var directValue = ReadNullableUIntAttribute(direct, attributeName);
        return directValue ?? ReadNullableUIntAttribute(inherited, attributeName) ?? 0u;
    }

    private static bool IsBooleanStyleProperty(OpenXmlElement element) =>
        element.LocalName is "b" or "i" or "strike" or "outline" or "shadow" or "condense" or "extend";

    private static bool ReadBooleanStyleProperty(OpenXmlElement? element)
    {
        if (element is null)
        {
            return false;
        }
        return ReadNullableBooleanAttribute(element, "val") ?? true;
    }

    private static bool? ReadNullableBooleanAttribute(OpenXmlElement? element, string attributeName)
    {
        var value = element?.GetAttributes()
            .FirstOrDefault(attribute => string.Equals(attribute.LocalName, attributeName, StringComparison.Ordinal))
            .Value;
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "1" or "true" or "on" => true,
            "0" or "false" or "off" => false,
            _ => null
        };
    }

    private static uint ReadUIntAttribute(OpenXmlElement? element, string attributeName) =>
        ReadNullableUIntAttribute(element, attributeName) ?? 0u;

    private static uint? ReadNullableUIntAttribute(OpenXmlElement? element, string attributeName)
    {
        var value = element?.GetAttributes()
            .FirstOrDefault(attribute => string.Equals(attribute.LocalName, attributeName, StringComparison.Ordinal))
            .Value;
        return uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static IReadOnlyDictionary<uint, string> ReadThemeColors(ThemePart? themePart)
    {
        var result = new Dictionary<uint, string>();
        if (themePart is null)
        {
            return result;
        }

        using var stream = themePart.GetStream(FileMode.Open, FileAccess.Read);
        var document = XDocument.Load(stream, LoadOptions.None);
        var scheme = document.Descendants().FirstOrDefault(static element => element.Name.LocalName == "clrScheme");
        if (scheme is null)
        {
            return result;
        }

        var indexes = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["lt1"] = 0,
            ["dk1"] = 1,
            ["lt2"] = 2,
            ["dk2"] = 3,
            ["accent1"] = 4,
            ["accent2"] = 5,
            ["accent3"] = 6,
            ["accent4"] = 7,
            ["accent5"] = 8,
            ["accent6"] = 9,
            ["hlink"] = 10,
            ["folHlink"] = 11
        };
        foreach (var color in scheme.Elements())
        {
            if (!indexes.TryGetValue(color.Name.LocalName, out var index))
            {
                continue;
            }
            var model = color.Elements().FirstOrDefault();
            var value = model?.Name.LocalName == "sysClr"
                ? (string?)model.Attribute("lastClr") ?? (string?)model.Attribute("val")
                : (string?)model?.Attribute("val") ?? (string?)model?.Attribute("lastClr");
            var normalized = TryNormalizeArgb(value);
            if (normalized is not null)
            {
                result[index] = normalized;
            }
        }
        return result;
    }

    private static string? TryNormalizeArgb(string? rgb)
    {
        if (rgb is null || (rgb.Length is not (6 or 8)) || rgb.Any(static character => !Uri.IsHexDigit(character)))
        {
            return null;
        }
        return rgb.Length == 6 ? "FF" + rgb.ToUpperInvariant() : rgb.ToUpperInvariant();
    }

    private static string? IndexedColor(uint index) => index switch
    {
        0 => "FF000000",
        1 => "FFFFFFFF",
        2 => "FFFF0000",
        3 => "FF00FF00",
        4 => "FF0000FF",
        5 => "FFFFFF00",
        6 => "FFFF00FF",
        7 => "FF00FFFF",
        8 => "FF000000",
        9 => "FFFFFFFF",
        10 => "FFFF0000",
        11 => "FF00FF00",
        12 => "FF0000FF",
        13 => "FFFFFF00",
        14 => "FFFF00FF",
        15 => "FF00FFFF",
        64 => "FF000000",
        _ => null
    };

    private static string ApplyTint(string argb, double tint)
    {
        if (argb.Length != 8 || tint is < -1d or > 1d)
        {
            return argb;
        }
        if (!byte.TryParse(argb.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) ||
            !byte.TryParse(argb.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) ||
            !byte.TryParse(argb.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
        {
            return argb;
        }
        static byte Tint(byte component, double amount) => (byte)Math.Clamp(
            Math.Round(amount < 0d ? component * (1d + amount) : component + ((255d - component) * amount)),
            0d,
            255d);
        return $"{argb[..2]}{Tint(red, tint):X2}{Tint(green, tint):X2}{Tint(blue, tint):X2}";
    }

    private static readonly IReadOnlyDictionary<uint, string> BuiltInFormats = new Dictionary<uint, string>
    {
        [0] = "General",
        [1] = "0",
        [2] = "0.00",
        [3] = "#,##0",
        [4] = "#,##0.00",
        [9] = "0%",
        [10] = "0.00%",
        [11] = "0.00E+00",
        [12] = "# ?/?",
        [13] = "# ??/??",
        [14] = "m/d/yyyy",
        [15] = "d-mmm-yy",
        [16] = "d-mmm",
        [17] = "mmm-yy",
        [18] = "h:mm AM/PM",
        [19] = "h:mm:ss AM/PM",
        [20] = "h:mm",
        [21] = "h:mm:ss",
        [22] = "m/d/yyyy h:mm",
        [45] = "mm:ss",
        [46] = "[h]:mm:ss",
        [47] = "mmss.0"
    };
}

internal sealed record ResolvedCellFormat(uint NumberFormatId, CellFormatSnapshot Snapshot);
