using ZCompare.Core;
using System.Reflection;
using System.Text;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
return await ZCompareCli.RunAsync(args);

internal static class ZCompareCli
{
    private const int ExitSame = 0;
    private const int ExitDifferent = 1;
    private const int ExitError = 2;
    private const int ExitCancelled = 3;
    private const int ExitIncomplete = 4;
    private const int ExitDifferentWithErrors = 5;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] is "-v" or "--version" or "version")
        {
            Console.WriteLine($"ZCompare {ProductVersion}");
            return ExitSame;
        }

        if (args.Length > 0 && args[0] is "-h" or "--help" or "help")
        {
            WriteUsage();
            return ExitSame;
        }

        if (!TryParse(args, out var command, out var error))
        {
            if (!string.IsNullOrEmpty(error))
            {
                Console.Error.WriteLine(error);
            }
            WriteUsage();
            return ExitError;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            var progress = new Progress<ComparisonProgress>(value =>
            {
                var count = value.Total > 0 ? $" {value.Processed}/{value.Total}" : string.Empty;
                var item = string.IsNullOrEmpty(value.CurrentItem) ? string.Empty : $" {value.CurrentItem}";
                Console.Error.WriteLine($"[{value.Stage}]{count}{item} {value.Message}".TrimEnd());
            });

            return command.Mode switch
            {
                CommandMode.File => await CompareFileAsync(command, progress, cancellation.Token),
                CommandMode.Folder => await CompareFolderAsync(command, progress, cancellation.Token),
                _ => ExitError,
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("比较已取消。");
            return ExitCancelled;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"比较失败：{exception.Message}");
            if (command.ReportPath is not null)
            {
                try
                {
                    await ComparisonReportExporter.ExportFatalErrorAsync(
                        command.LeftPath,
                        command.RightPath,
                        exception.Message,
                        command.ReportPath,
                        command.ReportFormat,
                        CancellationToken.None);
                    Console.Error.WriteLine($"错误报告已写入：{command.ReportPath}");
                }
                catch (Exception reportException)
                {
                    Console.Error.WriteLine($"错误报告写入失败：{reportException.Message}");
                }
            }
            return ExitError;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static string ProductVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private static async Task<int> CompareFileAsync(
        CliCommand command,
        IProgress<ComparisonProgress> progress,
        CancellationToken cancellationToken)
    {
        var comparer = new WorkbookComparer();
        var result = await comparer.CompareAsync(
            command.LeftPath,
            command.RightPath,
            command.Options,
            progress,
            cancellationToken);
        await ExportIfRequestedAsync(result, command, cancellationToken);
        Console.WriteLine($"状态：{result.Status}；差异：{result.DifferenceCount}；耗时：{result.Elapsed}");
        return ExitCode(result.Status);
    }

    private static async Task<int> CompareFolderAsync(
        CliCommand command,
        IProgress<ComparisonProgress> progress,
        CancellationToken cancellationToken)
    {
        var comparer = new FolderComparer();
        var result = await comparer.CompareAsync(
            command.LeftPath,
            command.RightPath,
            command.Options,
            command.ScanOptions,
            progress,
            cancellationToken);
        await ExportIfRequestedAsync(result, command, cancellationToken);
        var differentFiles = result.Files.Count(static file => file.Status is
            ComparisonStatus.Different or ComparisonStatus.LeftOnly or ComparisonStatus.RightOnly);
        Console.WriteLine($"状态：{result.Status}；文件：{result.Files.Count}；差异文件：{differentFiles}；错误文件：{result.ErrorFileCount}；耗时：{result.Elapsed}");
        return FolderExitCode(result);
    }

    private static Task ExportIfRequestedAsync(
        WorkbookCompareResult result,
        CliCommand command,
        CancellationToken cancellationToken) =>
        command.ReportPath is null
            ? Task.CompletedTask
            : ComparisonReportExporter.ExportAsync(result, command.ReportPath, command.ReportFormat, cancellationToken);

    private static Task ExportIfRequestedAsync(
        FolderCompareResult result,
        CliCommand command,
        CancellationToken cancellationToken) =>
        command.ReportPath is null
            ? Task.CompletedTask
            : ComparisonReportExporter.ExportAsync(result, command.ReportPath, command.ReportFormat, cancellationToken);

    private static int ExitCode(ComparisonStatus status) => status switch
    {
        ComparisonStatus.Same => ExitSame,
        ComparisonStatus.Cancelled => ExitCancelled,
        ComparisonStatus.Error => ExitError,
        _ => ExitDifferent,
    };

    private static int FolderExitCode(FolderCompareResult result)
    {
        if (result.ErrorFileCount == 0)
        {
            return ExitCode(result.Status);
        }

        return result.HasConfirmedDifferences ? ExitDifferentWithErrors : ExitIncomplete;
    }

    private static bool TryParse(string[] args, out CliCommand command, out string? error)
    {
        command = default!;
        error = null;
        if (args.Length == 0)
        {
            error = "缺少命令。";
            return false;
        }

        var mode = args[0].ToLowerInvariant() switch
        {
            "file" => CommandMode.File,
            "folder" => CommandMode.Folder,
            _ => CommandMode.Unknown,
        };
        if (mode == CommandMode.Unknown || args.Length < 3)
        {
            error = "命令必须是 file 或 folder，并提供左右两个路径。";
            return false;
        }

        string leftPath;
        string rightPath;
        try
        {
            leftPath = Path.GetFullPath(args[1]);
            rightPath = Path.GetFullPath(args[2]);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"左右路径无效：{exception.Message}";
            return false;
        }
        string? reportPath = null;
        ComparisonReportFormat? reportFormat = null;
        var compareFormulas = false;
        var compareFormatting = false;
        var compareFonts = false;
        var compareComments = false;
        var compareHyperlinks = false;
        var compareLayout = false;
        var caseSensitive = true;
        var rowAlignment = RowAlignmentMode.Conservative;
        var worksheetPairing = WorksheetPairingMode.Name;
        var keyColumnRules = new List<KeyColumnRule>();
        var manualWorksheetPairs = new List<WorksheetPair>();
        var columnMappings = new List<WorksheetColumnMapping>();
        var includeSubdirectories = true;
        var filePattern = "*.xlsx";

        for (var index = 3; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--report" when index + 1 < args.Length:
                    if (!TryGetFullPath(args[++index], out reportPath, out error))
                    {
                        error = $"报告路径无效：{error}";
                        return false;
                    }
                    break;
                case "--report-format" when index + 1 < args.Length:
                    if (!TryParseReportFormat(args[++index], out var parsedFormat))
                    {
                        error = "--report-format 只接受 json 或 xlsx。";
                        return false;
                    }
                    reportFormat = parsedFormat;
                    break;
                case "--strict-rows":
                    rowAlignment = RowAlignmentMode.StrictRowNumber;
                    break;
                case "--row-mode" when index + 1 < args.Length:
                    if (!TryParseRowAlignmentMode(args[++index], out rowAlignment))
                    {
                        error = "--row-mode 只接受 conservative、strict 或 key。";
                        return false;
                    }
                    break;
                case "--key" when index + 1 < args.Length:
                    if (!TryParseKeyColumnRule(args[++index], out var keyRule, out error))
                    {
                        return false;
                    }
                    keyColumnRules.Add(keyRule);
                    rowAlignment = RowAlignmentMode.KeyColumns;
                    break;
                case "--sheet-pairing" when index + 1 < args.Length:
                    if (!TryParseWorksheetPairingMode(args[++index], out worksheetPairing))
                    {
                        error = "--sheet-pairing 只接受 name、index 或 manual。";
                        return false;
                    }
                    break;
                case "--pair" when index + 1 < args.Length:
                    if (!TryParseWorksheetPair(args[++index], out var worksheetPair, out error))
                    {
                        return false;
                    }
                    manualWorksheetPairs.Add(worksheetPair);
                    worksheetPairing = WorksheetPairingMode.Manual;
                    break;
                case "--map" when index + 1 < args.Length:
                    if (!TryParseColumnMapping(args[++index], out var columnMapping, out error))
                    {
                        return false;
                    }
                    columnMappings.Add(columnMapping);
                    break;
                case "--no-subdirectories":
                    if (mode != CommandMode.Folder)
                    {
                        error = "--no-subdirectories 仅适用于 folder 命令。";
                        return false;
                    }
                    includeSubdirectories = false;
                    break;
                case "--pattern" when index + 1 < args.Length:
                    if (mode != CommandMode.Folder)
                    {
                        error = "--pattern 仅适用于 folder 命令。";
                        return false;
                    }
                    filePattern = args[++index];
                    break;
                case "--ignore-case":
                    caseSensitive = false;
                    break;
                case "--formulas":
                    compareFormulas = true;
                    break;
                case "--formatting":
                    compareFormatting = true;
                    break;
                case "--fonts":
                    compareFonts = true;
                    break;
                case "--comments":
                    compareComments = true;
                    break;
                case "--hyperlinks":
                    compareHyperlinks = true;
                    break;
                case "--layout":
                    compareLayout = true;
                    break;
                default:
                    error = $"未知或不完整的参数：{args[index]}";
                    return false;
            }
        }

        if (reportPath is not null && reportFormat is null)
        {
            if (!TryParseReportFormat(Path.GetExtension(reportPath).TrimStart('.'), out var inferredFormat))
            {
                error = "报告路径必须使用 .json 或 .xlsx 扩展名，或显式指定 --report-format。";
                return false;
            }
            reportFormat = inferredFormat;
        }

        command = new CliCommand(
            mode,
            leftPath,
            rightPath,
            new ComparisonOptions
            {
                CompareFormulas = compareFormulas,
                CompareFormatting = compareFormatting,
                CompareFonts = compareFonts,
                CompareComments = compareComments,
                CompareHyperlinks = compareHyperlinks,
                CompareLayout = compareLayout,
                CaseSensitive = caseSensitive,
                RowAlignmentMode = rowAlignment,
                KeyColumnRules = keyColumnRules,
                WorksheetPairingMode = worksheetPairing,
                ManualWorksheetPairs = manualWorksheetPairs,
                ColumnMappings = columnMappings,
            },
            new FolderScanOptions
            {
                IncludeSubdirectories = includeSubdirectories,
                FilePattern = filePattern,
            },
            reportPath,
            reportFormat ?? ComparisonReportFormat.Json);
        return true;
    }

    private static bool TryParseRowAlignmentMode(string value, out RowAlignmentMode mode)
    {
        mode = value.ToLowerInvariant() switch
        {
            "conservative" => RowAlignmentMode.Conservative,
            "strict" => RowAlignmentMode.StrictRowNumber,
            "key" => RowAlignmentMode.KeyColumns,
            _ => (RowAlignmentMode)(-1),
        };
        return Enum.IsDefined(mode);
    }

    private static bool TryGetFullPath(string value, out string? fullPath, out string? error)
    {
        try
        {
            fullPath = Path.GetFullPath(value);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = null;
            error = exception.Message;
            return false;
        }
    }

    private static bool TryParseWorksheetPairingMode(string value, out WorksheetPairingMode mode)
    {
        mode = value.ToLowerInvariant() switch
        {
            "name" => WorksheetPairingMode.Name,
            "index" => WorksheetPairingMode.Index,
            "manual" => WorksheetPairingMode.Manual,
            _ => (WorksheetPairingMode)(-1),
        };
        return Enum.IsDefined(mode);
    }

    private static bool TryParseKeyColumnRule(
        string value,
        out KeyColumnRule rule,
        out string? error)
    {
        rule = default!;
        error = null;
        var parts = value.Split(':', 3, StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[0]) ||
            !int.TryParse(parts[1], out var headerRow) || headerRow < 1)
        {
            error = "--key 格式必须是 工作表:表头行:列字母，例如 Data:1:A,C。";
            return false;
        }

        var columns = parts[2]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (columns.Length == 0)
        {
            error = "--key 至少需要一个关键列字母。";
            return false;
        }
        rule = new KeyColumnRule(parts[0], headerRow, columns);
        return true;
    }

    private static bool TryParseWorksheetPair(
        string value,
        out WorksheetPair pair,
        out string? error)
    {
        pair = default!;
        error = null;
        var separator = value.IndexOf('=');
        if (separator <= 0 || separator >= value.Length - 1)
        {
            error = "--pair 格式必须是 左侧工作表=右侧工作表。";
            return false;
        }

        var left = value[..separator].Trim();
        var right = value[(separator + 1)..].Trim();
        if (left.Length == 0 || right.Length == 0)
        {
            error = "--pair 的左右工作表名不能为空。";
            return false;
        }
        pair = new WorksheetPair(left, right);
        return true;
    }

    private static bool TryParseColumnMapping(
        string value,
        out WorksheetColumnMapping mapping,
        out string? error)
    {
        mapping = default!;
        error = null;
        var parts = value.Split(':', 3, StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            error = "--map 格式必须是 左表:右表:左列=右列，例如 LeftData:RightData:A=B,C=D。";
            return false;
        }

        var pairs = new List<ColumnPair>();
        foreach (var item in parts[2].Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = item.IndexOf('=');
            if (separator <= 0 || separator != item.LastIndexOf('=') || separator == item.Length - 1)
            {
                error = $"--map 中的列对“{item}”无效，请使用 A=B 格式。";
                return false;
            }
            pairs.Add(new ColumnPair(item[..separator].Trim(), item[(separator + 1)..].Trim()));
        }
        if (pairs.Count == 0)
        {
            error = "--map 至少需要一组左右列。";
            return false;
        }

        mapping = new WorksheetColumnMapping(parts[0], parts[1], pairs);
        return true;
    }

    private static bool TryParseReportFormat(string value, out ComparisonReportFormat format)
    {
        if (string.Equals(value, "json", StringComparison.OrdinalIgnoreCase))
        {
            format = ComparisonReportFormat.Json;
            return true;
        }
        if (string.Equals(value, "xlsx", StringComparison.OrdinalIgnoreCase))
        {
            format = ComparisonReportFormat.Xlsx;
            return true;
        }

        format = default;
        return false;
    }

    private static void WriteUsage()
    {
        Console.Error.WriteLine(
            """
            用法：
              zcompare --version
              zcompare file <左侧.xlsx> <右侧.xlsx> [选项]
              zcompare folder <左侧目录> <右侧目录> [选项]

            选项：
              --strict-rows              严格按原行号比较
              --row-mode <模式>          conservative、strict 或 key
              --key <表:表头行:列>       关键列规则；可重复，例如 Data:1:A,C
              --sheet-pairing <模式>     name、index 或 manual
              --pair <左表=右表>         手工工作表配对；可重复
              --map <左表:右表:列对>     左右列映射；可重复，例如 L:R:A=B,C=D
              --ignore-case              忽略文字大小写
              --formulas                 比较公式文本
              --formatting               比较数字格式、填充、边框和对齐
              --fonts                    比较字体
              --comments                 比较批注
              --hyperlinks               比较链接
              --layout                   比较布局
              --no-subdirectories        文件夹比较不递归扫描子目录
              --pattern <通配符>         文件夹文件名通配符，默认 *.xlsx
              --report <路径>            导出 .json 或 .xlsx 报告
              --report-format json|xlsx  显式指定报告格式

            退出码：0 相同；1 存在差异或警告；2 顶层执行错误；3 已取消；
                    4 无确认差异但有文件无法读取；5 同时存在确认差异和无法读取文件。
            """);
    }

    private enum CommandMode
    {
        Unknown,
        File,
        Folder,
    }

    private sealed record CliCommand(
        CommandMode Mode,
        string LeftPath,
        string RightPath,
        ComparisonOptions Options,
        FolderScanOptions ScanOptions,
        string? ReportPath,
        ComparisonReportFormat ReportFormat);
}
