using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class FormulaComparisonTests : ComparisonTestBase
{
    [Fact]
    public async Task ByteIdenticalWorkbookWithoutFormulasIgnoresCalculationFlags()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = new TestWorkbookBuilder()
            .WithCalculation(fullCalculationOnLoad: true)
            .AddSheet("Sheet1", sheet => sheet.Cell("A1", "42"))
            .Save(temporaryDirectory.File("left.xlsx"));
        var right = temporaryDirectory.File("right.xlsx");
        File.Copy(left, right);

        var result = await CreateComparer().CompareAsync(left, right);

        Assert.True(result.ByteIdentical);
        Assert.Equal(ComparisonStatus.Same, result.Status);
        Assert.Equal(0, result.DifferenceCount);
        Assert.Empty(result.Warnings);
        Assert.Empty(AllDifferences(result));
    }

    [Fact]
    public async Task ByteIdenticalWorkbookWithMissingFormulaCacheStillReturnsLocatableWarning()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = new TestWorkbookBuilder()
            .WithCalculation()
            .AddSheet("Sheet1", sheet => sheet.Cell("C7", null, formula: "1+1"))
            .Save(temporaryDirectory.File("left.xlsx"));
        var right = temporaryDirectory.File("right.xlsx");
        File.Copy(left, right);

        var result = await CreateComparer().CompareAsync(left, right);
        var cacheWarning = DifferenceAt(result, DifferenceKind.Warning, "C7");

        Assert.True(result.ByteIdentical);
        Assert.Equal(ComparisonStatus.Warning, result.Status);
        Assert.Equal("Sheet1", cacheWarning.WorksheetName);
        Assert.Contains("缓存", cacheWarning.Description, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, warning => warning.Contains("手动计算", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FormulaTextDifferenceObeysOptionWhileCachedValueAlwaysMatches()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", "2", formula: "1+1")),
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", "2", formula: "4/2")));
        var comparer = CreateComparer();

        var enabled = await comparer.CompareAsync(pair.Left, pair.Right, new ComparisonOptions { CompareFormulas = true });
        var disabled = await comparer.CompareAsync(pair.Left, pair.Right, new ComparisonOptions { CompareFormulas = false });

        DifferenceAt(enabled, DifferenceKind.Formula, "A1");
        Assert.Equal(ComparisonStatus.Same, disabled.Status);
        Assert.Empty(AllDifferences(disabled));
    }

    [Fact]
    public async Task CachedFormulaResultDifferenceIsAlwaysReported()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", "2", formula: "1+1")),
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", "3", formula: "1+1")));
        var comparer = CreateComparer();

        var enabled = await comparer.CompareAsync(pair.Left, pair.Right, new ComparisonOptions { CompareFormulas = true });
        var disabled = await comparer.CompareAsync(pair.Left, pair.Right, new ComparisonOptions { CompareFormulas = false });

        DifferenceAt(enabled, DifferenceKind.FormulaResult, "A1");
        DifferenceAt(disabled, DifferenceKind.FormulaResult, "A1");
    }

    [Fact]
    public async Task FormulaAndConstantWithSameSavedValueAreSameOnlyWhenFormulaTextIgnored()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", "2", formula: "1+1")),
            builder => builder.AddSheet("Sheet1", sheet => sheet.Cell("A1", "2")));
        var comparer = CreateComparer();

        var enabled = await comparer.CompareAsync(pair.Left, pair.Right, new ComparisonOptions { CompareFormulas = true });
        var disabled = await comparer.CompareAsync(pair.Left, pair.Right, new ComparisonOptions { CompareFormulas = false });

        DifferenceAt(enabled, DifferenceKind.Formula, "A1");
        Assert.Equal(ComparisonStatus.Same, disabled.Status);
    }

    [Fact]
    public async Task SharedFormulaIsResolvedForEveryCell()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("B1", "2", formula: "A1+1", formulaKind: "shared", formulaReference: "B1:B2", sharedIndex: 0)
                .Cell("B2", "3", formula: string.Empty, formulaKind: "shared", sharedIndex: 0)),
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("B1", "2", formula: "A1+2", formulaKind: "shared", formulaReference: "B1:B2", sharedIndex: 0)
                .Cell("B2", "3", formula: string.Empty, formulaKind: "shared", sharedIndex: 0)));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareFormulas = true });

        var formulaDifferences = AllDifferences(result)
            .Where(difference => difference.Kind == DifferenceKind.Formula)
            .ToArray();
        Assert.Contains(formulaDifferences, difference => difference.CellReference == "B1");
        Assert.Contains(formulaDifferences, difference => difference.CellReference == "B2");
    }

    [Fact]
    public async Task SharedFormulaTranslationPreservesStringLiteralsAndStructuredReferences()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = Path.Combine(temporaryDirectory.Path, "shared-formula.xlsx");
        new TestWorkbookBuilder()
            .AddSheet("Sheet1", sheet => sheet
                .Cell(
                    "B1",
                    "1",
                    formula: "IF(A1=\"A1\",Table1[[#This Row],[A1]],A1)",
                    formulaKind: "shared",
                    formulaReference: "B1:B2",
                    sharedIndex: 0)
                .Cell("B2", "1", formula: string.Empty, formulaKind: "shared", sharedIndex: 0))
            .Save(path);

        var cells = new List<CellSnapshot>();
        await foreach (var cell in new OpenXmlWorkbookReader().ReadCellsAsync(path, "Sheet1"))
        {
            cells.Add(cell);
        }

        Assert.Equal(
            "IF(A2=\"A1\",Table1[[#This Row],[A1]],A2)",
            Assert.Single(cells, cell => cell.CellReference == "B2").Formula);
    }

    [Theory]
    [InlineData("LOG10(A1)+A1_RATE", "LOG10(B2)+A1_RATE")]
    [InlineData("SUM(A:A)+ROWS(1:1)", "SUM(B:B)+ROWS(2:2)")]
    [InlineData("Sheet1!A1+Table1[A1]", "Sheet1!B2+Table1[A1]")]
    public async Task SharedFormulaTranslationDistinguishesReferencesFromNamesAndRanges(
        string anchorFormula,
        string expectedTranslatedFormula)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = new TestWorkbookBuilder()
            .AddSheet("Sheet1", sheet => sheet
                .Cell(
                    "B1",
                    "1",
                    formula: anchorFormula,
                    formulaKind: "shared",
                    formulaReference: "B1:C2",
                    sharedIndex: 0)
                .Cell("C2", "1", formula: string.Empty, formulaKind: "shared", sharedIndex: 0))
            .Save(temporaryDirectory.File("shared-formula.xlsx"));

        var cells = new List<CellSnapshot>();
        await foreach (var cell in CreateReader().ReadCellsAsync(path, "Sheet1"))
        {
            cells.Add(cell);
        }

        Assert.Equal(
            expectedTranslatedFormula,
            Assert.Single(cells, cell => cell.CellReference == "C2").Formula);
    }

    [Fact]
    public async Task EmptyNumericFormulaCacheWarnsButEmptyStringFormulaCacheIsValid()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = new TestWorkbookBuilder()
            .AddSheet("Sheet1", sheet => sheet
                .Cell("A1", string.Empty, formula: "1+1")
                .Cell("A2", string.Empty, TestCellType.FormulaString, formula: "\"\""))
            .Save(temporaryDirectory.File("left.xlsx"));
        var right = temporaryDirectory.File("right.xlsx");
        File.Copy(left, right);

        var result = await CreateComparer().CompareAsync(left, right);
        var warnings = AllDifferences(result)
            .Where(difference => difference.Kind == DifferenceKind.Warning)
            .ToArray();

        Assert.Contains(warnings, difference => difference.CellReference == "A1");
        Assert.DoesNotContain(warnings, difference => difference.CellReference == "A2");
    }

    [Fact]
    public async Task ArrayFormulaComparesAnchorAndRange()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("B1", "1", formula: "ROW(A1:A2)", formulaKind: "array", formulaReference: "B1:B2")
                .Cell("B2", "2")),
            builder => builder.AddSheet("Sheet1", sheet => sheet
                .Cell("B1", "1", formula: "ROW(A1:A3)", formulaKind: "array", formulaReference: "B1:B3")
                .Cell("B2", "2")));

        var result = await CreateComparer().CompareAsync(
            pair.Left,
            pair.Right,
            new ComparisonOptions { CompareFormulas = true });

        DifferenceAt(result, DifferenceKind.Formula, "B1");
    }

    [Fact]
    public async Task MissingCacheAndManualCalculationProduceWarningInsteadOfSame()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var pair = SavePair(
            temporaryDirectory,
            builder => builder.WithCalculation().AddSheet("Sheet1", sheet => sheet.Cell("A1", null, formula: "1+1")),
            builder => builder
                .WithCalculation()
                .AddSheet("Sheet1", sheet => sheet.Cell("A1", null, formula: "1+1"))
                .AddUnreferencedPart("ignored/noise.txt", "force semantic comparison"));

        var result = await CreateComparer().CompareAsync(pair.Left, pair.Right);

        Assert.NotEqual(ComparisonStatus.Same, result.Status);
        Assert.True(
            result.Warnings.Count > 0 ||
            AllDifferences(result).Any(difference => difference.Kind == DifferenceKind.Warning));
    }
}
