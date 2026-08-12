using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class StyleSemanticComparisonTests : ComparisonTestBase
{
    [Theory]
    [InlineData("<b val=\"0\"/>", "")]
    [InlineData("<b/>", "<b val=\"1\"/>")]
    public async Task EquivalentBoldBooleanMarkupDoesNotProduceFontDifferences(
        string leftBoldMarkup,
        string rightBoldMarkup)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder
                .WithStylesXml(BuildStyles(leftBoldMarkup, AppliedFontCellXf))
                .AddSheet("Sheet1", sheet => sheet.Cell("A1", "same", TestCellType.InlineString, styleIndex: 1)),
            builder => builder
                .WithStylesXml(BuildStyles(rightBoldMarkup, AppliedFontCellXf))
                .AddSheet("Sheet1", sheet => sheet.Cell("A1", "same", TestCellType.InlineString, styleIndex: 1)));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareFonts = true });

        Assert.Equal(ComparisonStatus.Same, result.Status);
        Assert.DoesNotContain(
            AllDifferences(result),
            difference => difference.Kind == DifferenceKind.Font);
    }

    [Fact]
    public async Task ApplyFontFalseIgnoresDirectFontAndInheritsTheBaseStyle()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder
                .WithStylesXml(BuildStyles("<b/>", DirectBoldFontNotAppliedCellXf))
                .AddSheet("Sheet1", sheet => sheet.Cell("A1", "same", TestCellType.InlineString, styleIndex: 1)),
            builder => builder
                .WithStylesXml(BuildStyles("<b/>", AppliedBaseFontCellXf))
                .AddSheet("Sheet1", sheet => sheet.Cell("A1", "same", TestCellType.InlineString, styleIndex: 1)));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareFonts = true });

        Assert.Equal(ComparisonStatus.Same, result.Status);
        Assert.DoesNotContain(
            AllDifferences(result),
            difference => difference.Kind == DifferenceKind.Font);
    }

    [Theory]
    [InlineData(DifferenceKind.Fill, DirectFillNotAppliedCellXf)]
    [InlineData(DifferenceKind.Border, DirectBorderNotAppliedCellXf)]
    [InlineData(DifferenceKind.Alignment, DirectAlignmentNotAppliedCellXf)]
    public async Task ApplyFormattingFlagFalseIgnoresTheDirectProperty(
        DifferenceKind differenceKind,
        string directFormattingNotAppliedCellXf)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder
                .WithStylesXml(BuildStyles(string.Empty, directFormattingNotAppliedCellXf))
                .AddSheet("Sheet1", sheet => sheet.Cell("A1", "1", styleIndex: 1)),
            builder => builder
                .WithStylesXml(BuildStyles(string.Empty, AppliedBaseVisualFormattingCellXf))
                .AddSheet("Sheet1", sheet => sheet.Cell("A1", "1", styleIndex: 1)));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareFormatting = true });

        Assert.Equal(ComparisonStatus.Same, result.Status);
        Assert.DoesNotContain(
            AllDifferences(result),
            difference => difference.Kind == differenceKind);
    }

    private static string BuildStyles(string secondFontMarkup, string cellXf) =>
        $$"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <fonts count="2">
            <font><sz val="11"/><name val="Calibri"/></font>
            <font>{{secondFontMarkup}}<sz val="11"/><name val="Calibri"/></font>
          </fonts>
          <fills count="2">
            <fill><patternFill patternType="none"/></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFFFFF00"/></patternFill></fill>
          </fills>
          <borders count="2">
            <border><left/><right/><top/><bottom/><diagonal/></border>
            <border><left style="thin"/><right/><top/><bottom/><diagonal/></border>
          </borders>
          <cellStyleXfs count="1">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0"/>
          </cellStyleXfs>
          <cellXfs count="2">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            {{cellXf}}
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;

    private const string AppliedFontCellXf =
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>";

    private const string DirectBoldFontNotAppliedCellXf =
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"0\"/>";

    private const string AppliedBaseFontCellXf =
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>";

    private const string DirectFillNotAppliedCellXf =
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"1\" borderId=\"0\" xfId=\"0\" applyFill=\"0\"/>";

    private const string DirectBorderNotAppliedCellXf =
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"0\"/>";

    private const string DirectAlignmentNotAppliedCellXf =
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"0\">" +
        "<alignment horizontal=\"center\" wrapText=\"1\"/></xf>";

    private const string AppliedBaseVisualFormattingCellXf =
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" " +
        "applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"/>";
}
