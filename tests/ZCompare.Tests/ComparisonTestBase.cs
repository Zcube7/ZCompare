using System.Security.Cryptography;
using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public abstract class ComparisonTestBase
{
    protected static IWorkbookReader CreateReader() => new OpenXmlWorkbookReader();

    protected static IWorkbookComparer CreateComparer() => new WorkbookComparer(CreateReader());

    protected static IFolderComparer CreateFolderComparer() => new FolderComparer(CreateComparer());

    protected static IReadOnlyList<Difference> AllDifferences(WorkbookCompareResult result) =>
        result.WorkbookDifferences
            .Concat(result.Worksheets.SelectMany(static worksheet => worksheet.Differences))
            .ToArray();

    protected static Difference DifferenceAt(
        WorkbookCompareResult result,
        DifferenceKind kind,
        string cellReference) =>
        Assert.Single(AllDifferences(result), difference =>
            difference.Kind == kind &&
            string.Equals(difference.CellReference, cellReference, StringComparison.OrdinalIgnoreCase));

    protected static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    protected static (string Left, string Right) SavePair(
        TemporaryDirectory temporaryDirectory,
        Action<TestWorkbookBuilder> left,
        Action<TestWorkbookBuilder> right)
    {
        var leftBuilder = new TestWorkbookBuilder();
        var rightBuilder = new TestWorkbookBuilder();
        left(leftBuilder);
        right(rightBuilder);
        return (
            leftBuilder.Save(temporaryDirectory.File("left.xlsx")),
            rightBuilder.Save(temporaryDirectory.File("right.xlsx")));
    }
}
