using System.Diagnostics;
using System.Text.Json;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class CliIntegrationTests
{
    [Theory]
    [InlineData("-v")]
    [InlineData("--version")]
    [InlineData("version")]
    public async Task VersionReturnsProductVersion(string argument)
    {
        var result = await RunCliAsync(argument);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ZCompare 0.1.0", result.StandardOutput.Trim());
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    [InlineData("help")]
    public async Task HelpReturnsSuccessExitCode(string argument)
    {
        var result = await RunCliAsync(argument);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("用法", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingOrUnknownCommandReturnsErrorExitCode()
    {
        var missing = await RunCliAsync();
        var unknown = await RunCliAsync("unknown", "left", "right");

        Assert.Equal(2, missing.ExitCode);
        Assert.Contains("缺少命令", missing.StandardError, StringComparison.Ordinal);
        Assert.Equal(2, unknown.ExitCode);
        Assert.Contains("file 或 folder", unknown.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("same", 0)]
    [InlineData("different", 1)]
    public async Task FileComparisonUsesDocumentedSameAndDifferentExitCodes(
        string scenario,
        int expectedExitCode)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = new TestWorkbookBuilder()
            .AddSheet("Sheet1", sheet => sheet.Cell("A1", "left"))
            .Save(temporaryDirectory.File("left.xlsx"));
        var rightValue = scenario == "same" ? "left" : "right";
        var right = new TestWorkbookBuilder()
            .AddSheet("Sheet1", sheet => sheet.Cell("A1", rightValue))
            .Save(temporaryDirectory.File("right.xlsx"));

        var result = await RunCliAsync("file", left, right);

        Assert.Equal(expectedExitCode, result.ExitCode);
        Assert.Contains(expectedExitCode == 0 ? "状态：Same" : "状态：Different", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidReportFormatReturnsErrorWithoutCreatingReport()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var reportPath = temporaryDirectory.File("report.txt");

        var result = await RunCliAsync(
            "file",
            temporaryDirectory.File("left.xlsx"),
            temporaryDirectory.File("right.xlsx"),
            "--report",
            reportPath,
            "--report-format",
            "yaml");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("json 或 xlsx", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(reportPath));
    }

    [Fact]
    public async Task FatalFileErrorStillWritesRequestedJsonReport()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var reportPath = temporaryDirectory.File("fatal.json");

        var result = await RunCliAsync(
            "file",
            temporaryDirectory.File("missing-left.xlsx"),
            temporaryDirectory.File("missing-right.xlsx"),
            "--report",
            reportPath);

        Assert.Equal(2, result.ExitCode);
        Assert.True(File.Exists(reportPath));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        Assert.Equal("Error", document.RootElement.GetProperty("Status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("Error").GetString()));
    }

    [Fact]
    public async Task FolderErrorsUseIncompleteExitCodeAndRemainInReport()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = temporaryDirectory.Directory("left");
        var right = temporaryDirectory.Directory("right");
        File.WriteAllText(Path.Combine(left, "broken.xlsx"), "broken-left");
        File.WriteAllText(Path.Combine(right, "broken.xlsx"), "broken-right");
        var reportPath = temporaryDirectory.File("folder-errors.json");

        var result = await RunCliAsync("folder", left, right, "--report", reportPath);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("错误文件：1", result.StandardOutput, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        Assert.Equal(1, document.RootElement.GetProperty("ErrorFileCount").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("DifferentFileCount").GetInt32());
    }

    [Fact]
    public async Task FolderDifferencesAndErrorsUseDistinctExitCode()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = temporaryDirectory.Directory("left");
        var right = temporaryDirectory.Directory("right");
        File.WriteAllText(Path.Combine(left, "broken.xlsx"), "broken-left");
        File.WriteAllText(Path.Combine(right, "broken.xlsx"), "broken-right");
        new TestWorkbookBuilder().AddSheet("Sheet1", sheet => sheet.Cell("A1", "left"))
            .Save(Path.Combine(left, "changed.xlsx"));
        new TestWorkbookBuilder().AddSheet("Sheet1", sheet => sheet.Cell("A1", "right"))
            .Save(Path.Combine(right, "changed.xlsx"));

        var result = await RunCliAsync("folder", left, right);

        Assert.Equal(5, result.ExitCode);
        Assert.Contains("差异文件：1", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("错误文件：1", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FolderPatternAndNoSubdirectoriesLimitComparedFiles()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = temporaryDirectory.Directory("folder-left");
        var right = temporaryDirectory.Directory("folder-right");
        var template = new TestWorkbookBuilder()
            .AddSheet("Sheet1", sheet => sheet.Cell("A1", "same"))
            .Save(temporaryDirectory.File("template.xlsx"));
        File.Copy(template, Path.Combine(left, "data-top.xlsx"));
        File.Copy(template, Path.Combine(right, "data-top.xlsx"));
        File.Copy(template, Path.Combine(left, "other.xlsx"));
        Directory.CreateDirectory(Path.Combine(left, "nested"));
        Directory.CreateDirectory(Path.Combine(right, "nested"));
        new TestWorkbookBuilder().AddSheet("Sheet1", sheet => sheet.Cell("A1", "left"))
            .Save(Path.Combine(left, "nested", "data-nested.xlsx"));
        new TestWorkbookBuilder().AddSheet("Sheet1", sheet => sheet.Cell("A1", "right"))
            .Save(Path.Combine(right, "nested", "data-nested.xlsx"));

        var result = await RunCliAsync(
            "folder",
            left,
            right,
            "--no-subdirectories",
            "--pattern",
            "data-*.xlsx");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("状态：Same", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("文件：1", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeyColumnArgumentsExecuteAKeyAlignedComparison()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = new TestWorkbookBuilder()
            .AddSheet("Data", sheet =>
            {
                sheet.Cell("A1", "ID", TestCellType.InlineString);
                sheet.Cell("B1", "Value", TestCellType.InlineString);
                sheet.Cell("A2", "1", TestCellType.InlineString);
                sheet.Cell("B2", "alpha", TestCellType.InlineString);
            })
            .Save(temporaryDirectory.File("key-left.xlsx"));
        var right = new TestWorkbookBuilder()
            .AddSheet("Data", sheet =>
            {
                sheet.Cell("A1", "ID", TestCellType.InlineString);
                sheet.Cell("B1", "Value", TestCellType.InlineString);
                sheet.Cell("A2", "9", TestCellType.InlineString);
                sheet.Cell("B2", "inserted", TestCellType.InlineString);
                sheet.Cell("A3", "1", TestCellType.InlineString);
                sheet.Cell("B3", "alpha", TestCellType.InlineString);
            })
            .Save(temporaryDirectory.File("key-right.xlsx"));

        var result = await RunCliAsync(
            "file",
            left,
            right,
            "--row-mode",
            "key",
            "--key",
            "Data:1:A");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("状态：Different", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("比较失败", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualWorksheetPairArgumentsCanPairDifferentNames()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = new TestWorkbookBuilder()
            .AddSheet("LeftData", sheet => sheet.Cell("A1", "same", TestCellType.InlineString))
            .Save(temporaryDirectory.File("pair-left.xlsx"));
        var right = new TestWorkbookBuilder()
            .AddSheet("RightData", sheet => sheet.Cell("A1", "same", TestCellType.InlineString))
            .Save(temporaryDirectory.File("pair-right.xlsx"));

        var result = await RunCliAsync(
            "file",
            left,
            right,
            "--sheet-pairing",
            "manual",
            "--pair",
            "LeftData=RightData");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("状态：Same", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ColumnMapArgumentsCanReorderColumnsAcrossDifferentWorksheetNames()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = new TestWorkbookBuilder()
            .AddSheet("LeftData", sheet => sheet
                .Cell("A1", "one", TestCellType.InlineString)
                .Cell("B1", "two", TestCellType.InlineString))
            .Save(temporaryDirectory.File("map-left.xlsx"));
        var right = new TestWorkbookBuilder()
            .AddSheet("RightData", sheet => sheet
                .Cell("A1", "two", TestCellType.InlineString)
                .Cell("B1", "one", TestCellType.InlineString))
            .Save(temporaryDirectory.File("map-right.xlsx"));

        var result = await RunCliAsync(
            "file",
            left,
            right,
            "--sheet-pairing",
            "index",
            "--map",
            "LeftData:RightData:A=B,B=A");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("状态：Same", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("比较失败", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--row-mode", "fuzzy", "--row-mode")]
    [InlineData("--key", "Data:not-a-rule", "--key")]
    [InlineData("--sheet-pairing", "fuzzy", "--sheet-pairing")]
    [InlineData("--pair", "missing-separator", "--pair")]
    [InlineData("--map", "LeftData:RightData:not-a-pair", "--map")]
    public async Task InvalidAdvancedOptionFormatsReturnErrorExitCode(
        string option,
        string value,
        string expectedMessage)
    {
        var result = await RunCliAsync("file", "left.xlsx", "right.xlsx", option, value);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(expectedMessage, result.StandardError, StringComparison.Ordinal);
    }

    private static async Task<CliResult> RunCliAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(CliAssemblyPath());
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 zcompare CLI 测试进程。");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return new CliResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static string CliAssemblyPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = current.Parent?.Name ?? "Debug";
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ZCompare.slnx")))
        {
            current = current.Parent;
        }

        var root = current?.FullName ?? throw new DirectoryNotFoundException("找不到 ZCompare 仓库根目录。");
        var path = Path.Combine(root, "src", "ZCompare.Cli", "bin", configuration, "net10.0", "zcompare.dll");
        Assert.True(File.Exists(path), $"CLI 程序集不存在：{path}");
        return path;
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}
