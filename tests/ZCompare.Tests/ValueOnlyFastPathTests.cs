using System.IO.Compression;
using System.Text;
using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class ValueOnlyFastPathTests : ComparisonTestBase
{
    [Fact]
    public async Task EmptyPhoneticRunAndEmptyInlineStringsMatchFullPathSemantics()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "RPH_MARKER", TestCellType.InlineString)
                .Cell("A2", "EMPTY_IS_MARKER", TestCellType.InlineString)
                .Cell(
                    "A3",
                    "unused",
                    TestCellType.InlineString,
                    richTextRuns: ["  left", "middle", "right  "])),
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "Body", TestCellType.InlineString)
                .Cell("A2", string.Empty, TestCellType.InlineString)
                .Cell("A3", "  leftmiddleright  ", TestCellType.InlineString)));

        RewriteFirstWorksheet(pair.Left, xml => xml
            .Replace(
                "<is><t xml:space=\"preserve\">RPH_MARKER</t></is>",
                "<is><rPh/><t xml:space=\"preserve\">Body</t></is>",
                StringComparison.Ordinal)
            .Replace(
                "<is><t xml:space=\"preserve\">EMPTY_IS_MARKER</t></is>",
                "<is/>",
                StringComparison.Ordinal));
        RewriteFirstWorksheet(pair.Right, xml => xml.Replace(
            "<is><t xml:space=\"preserve\"></t></is>",
            "<is><t/></is>",
            StringComparison.Ordinal));

        var comparer = CreateComparer();
        var valueOnly = await comparer.CompareAsync(pair.Left, pair.Right);
        var fullCellPath = await comparer.CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareComments = true });

        Assert.Equal(ComparisonStatus.Same, valueOnly.Status);
        Assert.Equal(ComparisonStatus.Same, fullCellPath.Status);
        Assert.Empty(AllDifferences(valueOnly));
        Assert.Empty(AllDifferences(fullCellPath));

        var leftPreview = await CreateReader().LoadWorksheetPreviewAsync(pair.Left, "Sheet1");
        var rightPreview = await CreateReader().LoadWorksheetPreviewAsync(pair.Right, "Sheet1");
        Assert.Equal("Body", leftPreview.Cells["A1"].RawValue);
        AssertEmptyText(leftPreview.Cells["A2"]);
        AssertEmptyText(rightPreview.Cells["A2"]);
        Assert.Equal("  leftmiddleright  ", leftPreview.Cells["A3"].RawValue);
    }

    [Fact]
    public async Task EmptyInlineSnapshotFromValueOnlyPathMatchesPublicFullReader()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet =>
                sheet.Cell("A1", "EMPTY_IS_MARKER", TestCellType.InlineString)),
            builder => builder.AddSheet("Sheet1", sheet =>
                sheet.Cell("A1", "non-empty", TestCellType.InlineString)));
        RewriteFirstWorksheet(pair.Left, xml => xml.Replace(
            "<is><t xml:space=\"preserve\">EMPTY_IS_MARKER</t></is>",
            "<is/>",
            StringComparison.Ordinal));

        var valueOnlyResult = await CreateComparer().CompareAsync(pair.Left, pair.Right);
        var valueOnlySnapshot = Assert.IsType<CellSnapshot>(
            DifferenceAt(valueOnlyResult, DifferenceKind.Value, "A1").Left);
        var fullSnapshot = (await CreateReader().LoadWorksheetPreviewAsync(pair.Left, "Sheet1"))
            .Cells["A1"];

        AssertEmptyText(valueOnlySnapshot);
        AssertEmptyText(fullSnapshot);
        AssertValueAndCacheEqual(fullSnapshot, valueOnlySnapshot);
    }

    [Fact]
    public async Task FormulaCacheStatesMatchBetweenValueOnlyAndFullCellPaths()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", null, formula: "1")
                .Cell("A2", string.Empty, formula: "1")
                .Cell("A3", string.Empty, TestCellType.FormulaString, formula: "\"\"")
                .Cell("A4", "2", formula: "1+1")),
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("A1", "9", formula: "1")
                .Cell("A2", "9", formula: "1")
                .Cell("A3", "different", TestCellType.FormulaString, formula: "\"different\"")
                .Cell("A4", "3", formula: "1+2")));
        RewriteFirstWorksheet(pair.Left, xml => xml.Replace(
            "<v></v>",
            "<v/>",
            StringComparison.Ordinal));

        var comparer = CreateComparer();
        var valueOnly = await comparer.CompareAsync(pair.Left, pair.Right);
        var fullCellPath = await comparer.CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareComments = true });
        var fullPreview = await CreateReader().LoadWorksheetPreviewAsync(pair.Left, "Sheet1");
        var expectedStates = new Dictionary<string, FormulaCacheState>(StringComparer.OrdinalIgnoreCase)
        {
            ["A1"] = FormulaCacheState.Missing,
            ["A2"] = FormulaCacheState.Empty,
            ["A3"] = FormulaCacheState.ValidEmptyString,
            ["A4"] = FormulaCacheState.Present,
        };

        foreach (var (reference, expectedState) in expectedStates)
        {
            var valueOnlySnapshot = Assert.IsType<CellSnapshot>(
                DifferenceAt(valueOnly, DifferenceKind.FormulaResult, reference).Left);
            var fullCellSnapshot = Assert.IsType<CellSnapshot>(
                DifferenceAt(fullCellPath, DifferenceKind.FormulaResult, reference).Left);
            var fullPreviewSnapshot = fullPreview.Cells[reference];

            Assert.Equal(expectedState, valueOnlySnapshot.FormulaCacheState);
            Assert.Equal(expectedState, fullCellSnapshot.FormulaCacheState);
            Assert.Equal(expectedState, fullPreviewSnapshot.FormulaCacheState);
            AssertValueAndCacheEqual(fullCellSnapshot, valueOnlySnapshot);
            AssertValueAndCacheEqual(fullPreviewSnapshot, valueOnlySnapshot);
        }

        Assert.Contains(
            AllDifferences(valueOnly),
            difference => difference.Kind == DifferenceKind.Warning && difference.CellReference == "A1");
        Assert.Contains(
            AllDifferences(valueOnly),
            difference => difference.Kind == DifferenceKind.Warning && difference.CellReference == "A2");
        Assert.DoesNotContain(
            AllDifferences(valueOnly),
            difference => difference.Kind == DifferenceKind.Warning && difference.CellReference == "A3");
    }

    private static void AssertEmptyText(CellSnapshot cell)
    {
        Assert.Equal(CellValueKind.Text, cell.ValueKind);
        Assert.Equal(string.Empty, cell.RawValue);
        Assert.Equal(string.Empty, cell.NormalizedValue);
        Assert.Equal(string.Empty, cell.DisplayValue);
    }

    private static void AssertValueAndCacheEqual(CellSnapshot expected, CellSnapshot actual)
    {
        Assert.Equal(expected.CellReference, actual.CellReference);
        Assert.Equal(expected.ValueKind, actual.ValueKind);
        Assert.Equal(expected.RawValue, actual.RawValue);
        // The value-only path deliberately defers canonical parsing for a non-empty
        // number. Its raw cached value is the semantic source and must stay identical;
        // all other kinds should also have the same normalized representation.
        if (expected.ValueKind != CellValueKind.Number || string.IsNullOrEmpty(expected.RawValue))
        {
            Assert.Equal(expected.NormalizedValue, actual.NormalizedValue);
        }
        Assert.Equal(expected.DisplayValue, actual.DisplayValue);
        Assert.Equal(expected.FormulaKind, actual.FormulaKind);
        Assert.Equal(expected.FormulaCacheState, actual.FormulaCacheState);
    }

    private static void RewriteFirstWorksheet(string path, Func<string, string> rewrite)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false);
        const string entryName = "xl/worksheets/sheet1.xml";
        var entry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry(entryName));
        string original;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            original = reader.ReadToEnd();
        }

        var updated = rewrite(original);
        Assert.NotEqual(original, updated);
        entry.Delete();
        var replacement = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        replacement.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var writer = new StreamWriter(
            replacement.Open(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(updated);
    }
}
