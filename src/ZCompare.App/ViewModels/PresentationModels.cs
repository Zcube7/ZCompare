using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Media;
using ZCompare.App.Infrastructure;
using ZCompare.Core;

namespace ZCompare.App.ViewModels;

internal enum CompareMode
{
    Files,
    Folders,
}

internal enum CompareSide
{
    Left,
    Right,
}

internal sealed class FolderFileItemViewModel : ObservableObject
{
    private string? _leftPath;
    private string? _rightPath;
    private ComparisonStatus _status;
    private int _differenceCount;
    private object? _coreResult;
    private bool _isMarkedForComparison;

    public FolderFileItemViewModel(FolderFileResult result)
    {
        RelativePath = result.RelativePath;
        Apply(result);
    }

    public string RelativePath { get; }

    public string? LeftPath
    {
        get => _leftPath;
        private set
        {
            if (SetProperty(ref _leftPath, value))
            {
                OnPropertyChanged(nameof(HasLeft));
                OnPropertyChanged(nameof(LeftName));
                OnPropertyChanged(nameof(LeftDisplayPath));
                OnPropertyChanged(nameof(LeftInfo));
                OnPropertyChanged(nameof(LeftSize));
                OnPropertyChanged(nameof(LeftModified));
                OnPropertyChanged(nameof(LeftFileBrush));
            }
        }
    }

    public string? RightPath
    {
        get => _rightPath;
        private set
        {
            if (SetProperty(ref _rightPath, value))
            {
                OnPropertyChanged(nameof(HasRight));
                OnPropertyChanged(nameof(RightName));
                OnPropertyChanged(nameof(RightDisplayPath));
                OnPropertyChanged(nameof(RightInfo));
                OnPropertyChanged(nameof(RightSize));
                OnPropertyChanged(nameof(RightModified));
                OnPropertyChanged(nameof(RightFileBrush));
            }
        }
    }

    public bool HasLeft => !string.IsNullOrWhiteSpace(LeftPath);

    public bool HasRight => !string.IsNullOrWhiteSpace(RightPath);

    public string LeftName => HasLeft ? Path.GetFileName(LeftPath) ?? string.Empty : "（空）";

    public string RightName => HasRight ? Path.GetFileName(RightPath) ?? string.Empty : "（空）";

    public string LeftDisplayPath => HasLeft ? RelativePath : "—";

    public string RightDisplayPath => HasRight ? RelativePath : "—";

    public string LeftInfo => FormatFileInfo(LeftPath);

    public string RightInfo => FormatFileInfo(RightPath);

    public string LeftSize => FormatFileSize(LeftPath);

    public string RightSize => FormatFileSize(RightPath);

    public string LeftModified => FormatModifiedTime(LeftPath);

    public string RightModified => FormatModifiedTime(RightPath);

    public ComparisonStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusSymbol));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(StatusBackground));
                OnPropertyChanged(nameof(LeftFileBrush));
                OnPropertyChanged(nameof(RightFileBrush));
                OnPropertyChanged(nameof(IsComparisonComplete));
                OnPropertyChanged(nameof(IsDifferenceResult));
                OnPropertyChanged(nameof(StatusToolTip));
                OnPropertyChanged(nameof(IssueDetails));
                OnPropertyChanged(nameof(IssueSummary));
                OnPropertyChanged(nameof(HasIssueDetails));
            }
        }
    }

    public string StatusText => Status switch
    {
        ComparisonStatus.Pending => "待比较",
        ComparisonStatus.Same => "相同",
        ComparisonStatus.Different => "不同",
        ComparisonStatus.LeftOnly => "仅左侧",
        ComparisonStatus.RightOnly => "仅右侧",
        ComparisonStatus.Warning => "警告",
        ComparisonStatus.Error => "错误",
        ComparisonStatus.Cancelled => "已取消",
        _ => Status.ToString(),
    };

    public string? Error => (CoreResult as FolderFileResult)?.Error;

    public WorkbookCompareResult? WorkbookComparison =>
        (CoreResult as FolderFileResult)?.Comparison;

    public string? IssueDetails
    {
        get
        {
            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(Error))
            {
                details.Add(Error);
            }

            if (WorkbookComparison is { } comparison)
            {
                details.AddRange(comparison.Warnings.Where(static warning =>
                    !string.IsNullOrWhiteSpace(warning)));
                details.AddRange(comparison.WorkbookDifferences
                    .Where(static difference => difference.Kind is
                        DifferenceKind.Warning or
                        DifferenceKind.UncomparedObject or
                        DifferenceKind.RowAlignmentWarning)
                    .Select(static difference => difference.Description)
                    .Where(static description => !string.IsNullOrWhiteSpace(description)));
                details.AddRange(comparison.Worksheets
                    .SelectMany(static worksheet => worksheet.Differences)
                    .Where(static difference => difference.Kind is
                        DifferenceKind.Warning or
                        DifferenceKind.UncomparedObject or
                        DifferenceKind.RowAlignmentWarning)
                    .Select(static difference => difference.Description)
                    .Where(static description => !string.IsNullOrWhiteSpace(description)));
            }

            var distinct = details
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return distinct.Length == 0 ? null : string.Join(Environment.NewLine, distinct);
        }
    }

    public string IssueSummary
    {
        get
        {
            var details = IssueDetails;
            if (string.IsNullOrWhiteSpace(details))
            {
                return string.Empty;
            }

            var lineBreak = details.IndexOfAny(['\r', '\n']);
            return lineBreak < 0 ? details : details[..lineBreak];
        }
    }

    public bool HasIssueDetails => !string.IsNullOrWhiteSpace(IssueDetails);

    public string StatusToolTip => HasIssueDetails
        ? $"{StatusText}：{IssueDetails}"
        : $"{StatusText}：{DifferenceCount} 处";

    public string StatusSymbol => Status switch
    {
        ComparisonStatus.Same => "=",
        ComparisonStatus.Different => "≠",
        ComparisonStatus.LeftOnly or ComparisonStatus.RightOnly => "+",
        ComparisonStatus.Warning => "!",
        ComparisonStatus.Error => "×",
        ComparisonStatus.Cancelled => "■",
        _ => "…",
    };

    public Brush StatusBrush => Status switch
    {
        ComparisonStatus.Same => Brushes.SeaGreen,
        ComparisonStatus.Different or ComparisonStatus.Error => Brushes.Crimson,
        ComparisonStatus.LeftOnly or ComparisonStatus.RightOnly => Brushes.MediumPurple,
        ComparisonStatus.Warning => Brushes.DarkOrange,
        _ => Brushes.SlateGray,
    };

    public Brush StatusBackground => Status switch
    {
        ComparisonStatus.Same => new SolidColorBrush(Color.FromRgb(236, 253, 245)),
        ComparisonStatus.Different or ComparisonStatus.Error => new SolidColorBrush(Color.FromRgb(255, 241, 242)),
        ComparisonStatus.LeftOnly or ComparisonStatus.RightOnly => new SolidColorBrush(Color.FromRgb(245, 243, 255)),
        ComparisonStatus.Warning => new SolidColorBrush(Color.FromRgb(255, 251, 235)),
        _ => new SolidColorBrush(Color.FromRgb(248, 250, 252)),
    };

    public Brush LeftFileBrush => Status == ComparisonStatus.LeftOnly
        ? Brushes.MediumPurple
        : Brushes.Black;

    public Brush RightFileBrush => Status == ComparisonStatus.RightOnly
        ? Brushes.MediumPurple
        : Brushes.Black;

    public bool IsComparisonComplete => Status is
        ComparisonStatus.Same or
        ComparisonStatus.Different or
        ComparisonStatus.LeftOnly or
        ComparisonStatus.RightOnly or
        ComparisonStatus.Warning or
        ComparisonStatus.Error or
        ComparisonStatus.Cancelled;

    public bool IsDifferenceResult => IsComparisonComplete && Status != ComparisonStatus.Same;

    public int DifferenceCount
    {
        get => _differenceCount;
        private set => SetProperty(ref _differenceCount, value);
    }

    public object? CoreResult
    {
        get => _coreResult;
        private set
        {
            if (SetProperty(ref _coreResult, value))
            {
                OnPropertyChanged(nameof(Error));
                OnPropertyChanged(nameof(WorkbookComparison));
                OnPropertyChanged(nameof(StatusToolTip));
                OnPropertyChanged(nameof(IssueDetails));
                OnPropertyChanged(nameof(IssueSummary));
                OnPropertyChanged(nameof(HasIssueDetails));
            }
        }
    }

    public bool IsMarkedForComparison
    {
        get => _isMarkedForComparison;
        set
        {
            if (SetProperty(ref _isMarkedForComparison, value))
            {
                OnPropertyChanged(nameof(IsSelected));
            }
        }
    }

    // Retained for existing callers; this is a comparison mark, not DataGrid focus/selection.
    public bool IsSelected
    {
        get => IsMarkedForComparison;
        set => IsMarkedForComparison = value;
    }

    public void Apply(FolderFileResult result)
    {
        LeftPath = result.LeftPath;
        RightPath = result.RightPath;
        Status = result.Status;
        DifferenceCount = result.DifferenceCount;
        CoreResult = result;
    }

    public void ApplyComparison(WorkbookCompareResult comparison)
    {
        var error = comparison.Status == ComparisonStatus.Error
            ? comparison.Warnings.FirstOrDefault() ?? "比较引擎返回错误状态"
            : null;
        Status = comparison.Status;
        DifferenceCount = comparison.DifferenceCount;
        CoreResult = new FolderFileResult(
            RelativePath,
            LeftPath,
            RightPath,
            comparison.Status,
            comparison.DifferenceCount,
            comparison,
            error);
    }

    public void ApplyError(string error)
    {
        Status = ComparisonStatus.Error;
        DifferenceCount = 0;
        CoreResult = new FolderFileResult(
            RelativePath,
            LeftPath,
            RightPath,
            ComparisonStatus.Error,
            0,
            null,
            error);
    }

    public void ApplyCancelled()
    {
        Status = ComparisonStatus.Cancelled;
        DifferenceCount = 0;
        CoreResult = new FolderFileResult(
            RelativePath,
            LeftPath,
            RightPath,
            ComparisonStatus.Cancelled,
            0,
            null,
            null);
    }

    public void InvalidateCachedComparison()
    {
        if (!HasLeft || !HasRight || Status == ComparisonStatus.Pending)
        {
            return;
        }

        Status = ComparisonStatus.Pending;
        DifferenceCount = 0;
        CoreResult = new FolderFileResult(
            RelativePath,
            LeftPath,
            RightPath,
            ComparisonStatus.Pending,
            0,
            null,
            null);
    }

    private static string FormatFileInfo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "—";
        }

        try
        {
            var file = new FileInfo(path);
            return $"{file.Length / 1024d:N0} KB · {file.LastWriteTime:yyyy-MM-dd HH:mm}";
        }
        catch (IOException)
        {
            return "无法读取";
        }
        catch (UnauthorizedAccessException)
        {
            return "无访问权限";
        }
    }

    private static string FormatFileSize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "—";
        }

        try
        {
            return new FileInfo(path).Length.ToString("N0");
        }
        catch (IOException)
        {
            return "无法读取";
        }
        catch (UnauthorizedAccessException)
        {
            return "无权限";
        }
    }

    private static string FormatModifiedTime(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "—";
        }

        try
        {
            return new FileInfo(path).LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch (IOException)
        {
            return "无法读取";
        }
        catch (UnauthorizedAccessException)
        {
            return "无权限";
        }
    }
}

internal sealed class WorksheetTabViewModel : ObservableObject
{
    private int? _differenceCount;

    public required string Name { get; init; }

    public string? LeftWorksheetName { get; init; }

    public string? RightWorksheetName { get; init; }

    public bool HasLeftWorksheet => !string.IsNullOrWhiteSpace(LeftWorksheetName);

    public bool HasRightWorksheet => !string.IsNullOrWhiteSpace(RightWorksheetName);

    public bool IsOneSided { get; init; }

    public int? DifferenceCount
    {
        get => _differenceCount;
        set
        {
            if (SetProperty(ref _differenceCount, value))
            {
                OnPropertyChanged(nameof(Header));
                OnPropertyChanged(nameof(HasDifferences));
            }
        }
    }

    public bool HasDifferences => IsOneSided || DifferenceCount > 0;

    public string Header => IsOneSided && DifferenceCount is null
        ? $"{Name} (+)"
        : DifferenceCount > 0 ? $"{Name} ({DifferenceCount})" : Name;
}

internal sealed class GridCellViewModel
{
    public required string Address { get; init; }

    public string DisplayValue { get; init; } = string.Empty;

    public IReadOnlyList<TextDifferenceSegment> DisplaySegments { get; init; } = [];

    public string RawValue { get; init; } = string.Empty;

    public string Formula { get; init; } = string.Empty;

    public string DifferenceDetails { get; init; } = string.Empty;

    public string AdvancedDifferenceDetails { get; init; } = string.Empty;

    public bool IsDifferent { get; init; }

    public bool IsValueDifferent { get; init; }

    public bool HasAdvancedDifference => !string.IsNullOrWhiteSpace(AdvancedDifferenceDetails);

    public bool IsMissing { get; init; }

    public Brush Background { get; init; } = Brushes.White;

    public Brush Foreground { get; init; } = Brushes.Black;

    public FontFamily FontFamily { get; init; } = new("Microsoft YaHei UI");

    public double FontSize { get; init; } = 13;

    public FontWeight FontWeight { get; init; } = FontWeights.Normal;

    public FontStyle FontStyle { get; init; } = FontStyles.Normal;

    public TextAlignment TextAlignment { get; init; } = TextAlignment.Left;

    public TextWrapping TextWrapping { get; init; } = TextWrapping.NoWrap;

    public string ToolTip => string.IsNullOrEmpty(DifferenceDetails)
        ? $"{Address}\n显示值：{ClipForToolTip(DisplayValue, 160)}\n原始值：{ClipForToolTip(RawValue, 160)}"
        : $"{Address}\n{ClipForToolTip(DifferenceDetails, 480)}\n显示值：{ClipForToolTip(DisplayValue, 160)}\n原始值：{ClipForToolTip(RawValue, 160)}";

    private static string ClipForToolTip(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength] + "…（双击查看完整详情）";
}

internal sealed class GridRowViewModel
{
    public required int RowNumber { get; init; }

    public required int DisplayRowNumber { get; init; }

    public int? LeftRowNumber { get; init; }

    public int? RightRowNumber { get; init; }

    public bool IsPlaceholder => RowNumber <= 0;

    public string RowHeader => IsPlaceholder ? "—" : RowNumber.ToString();

    public RowAlignmentStatus AlignmentStatus { get; init; } = RowAlignmentStatus.NotApplied;

    public string AlignmentMessage { get; init; } = string.Empty;

    public bool IsDifferent { get; init; }

    public required IReadOnlyList<GridCellViewModel> Cells { get; init; }
}

internal sealed class DifferenceItemViewModel
{
    public required string Worksheet { get; init; }

    public required string Address { get; init; }

    public string Description { get; init; } = string.Empty;

    public object? CoreDifference { get; init; }
}

internal static class DifferencePresentation
{
    public static string FormatDetail(Difference difference)
    {
        var details = new List<string> { difference.Description };
        if (difference.LeftDetail is not null)
        {
            details.Add($"左：{MakeWhitespaceVisible(difference.LeftDetail)}");
        }
        if (difference.RightDetail is not null)
        {
            details.Add($"右：{MakeWhitespaceVisible(difference.RightDetail)}");
        }
        return string.Join(Environment.NewLine, details);
    }

    private static string MakeWhitespaceVisible(string value)
    {
        if (value.Length == 0)
        {
            return "⟦空字符串⟧";
        }

        if (!value.All(char.IsWhiteSpace))
        {
            return value;
        }

        return value
            .Replace(" ", "␠", StringComparison.Ordinal)
            .Replace("\t", "⇥", StringComparison.Ordinal)
            .Replace("\r", "␍", StringComparison.Ordinal)
            .Replace("\n", "␊", StringComparison.Ordinal);
    }
}

internal sealed class GridViewportViewModel : ObservableObject
{
    private IEnumerable _leftRows = Array.Empty<GridRowViewModel>();
    private IEnumerable _rightRows = Array.Empty<GridRowViewModel>();
    private int _columnCount;
    private bool _differencesOnly;
    private WorksheetPreview? _leftPreview;
    private WorksheetPreview? _rightPreview;
    private IReadOnlyList<Difference> _differences = [];
    private IReadOnlyDictionary<int, RowAlignment> _rowAlignments =
        new Dictionary<int, RowAlignment>();
    private GridColumnMappingPlan _columnMapping = GridColumnMappingPlan.Empty;
    private bool _usesCompactRowAlignment;
    private int _rowCount;
    private int[]? _visibleRows;

    public IEnumerable LeftRows
    {
        get => _leftRows;
        private set => SetProperty(ref _leftRows, value);
    }

    public IEnumerable RightRows
    {
        get => _rightRows;
        private set => SetProperty(ref _rightRows, value);
    }

    public int ColumnCount
    {
        get => _columnCount;
        private set => SetProperty(ref _columnCount, value);
    }

    public bool DifferencesOnly => _differencesOnly;

    public void Clear()
    {
        _leftPreview = null;
        _rightPreview = null;
        _differences = [];
        _rowAlignments = new Dictionary<int, RowAlignment>();
        _columnMapping = GridColumnMappingPlan.Empty;
        _usesCompactRowAlignment = false;
        _rowCount = 0;
        _visibleRows = null;
        LeftRows = Array.Empty<GridRowViewModel>();
        RightRows = Array.Empty<GridRowViewModel>();
        ColumnCount = 0;
    }

    public void SetPreviews(
        WorksheetPreview? left,
        WorksheetPreview? right,
        IReadOnlyList<Difference> differences,
        bool differencesOnly)
    {
        SetPreviews(left, right, differences, null, null, differencesOnly);
    }

    public void SetPreviews(
        WorksheetPreview? left,
        WorksheetPreview? right,
        IReadOnlyList<Difference> differences,
        IReadOnlyList<RowAlignment>? rowAlignments,
        bool differencesOnly)
    {
        SetPreviews(left, right, differences, rowAlignments, null, differencesOnly);
    }

    public void SetPreviews(
        WorksheetPreview? left,
        WorksheetPreview? right,
        IReadOnlyList<Difference> differences,
        IReadOnlyList<RowAlignment>? rowAlignments,
        IReadOnlyList<ColumnPair>? appliedColumnPairs,
        bool differencesOnly)
    {
        _leftPreview = left;
        _rightPreview = right;
        _differences = differences;
        _differencesOnly = differencesOnly;
        _rowAlignments = (rowAlignments ?? [])
            .ToDictionary(static alignment => alignment.DisplayRow);
        _columnMapping = GridColumnMappingPlan.Create(appliedColumnPairs);
        _usesCompactRowAlignment = _rowAlignments.Values.Any(static alignment =>
            alignment.Status != RowAlignmentStatus.NotApplied);

        var maximumSourceRow = 0;
        var maximumDisplayRow = _rowAlignments.Count == 0 ? 0 : _rowAlignments.Keys.Max();
        var maximumColumn = 0;
        UpdateBounds(left?.Cells.Keys ?? [], sourceCoordinates: true, CompareSide.Left);
        UpdateBounds(right?.Cells.Keys ?? [], sourceCoordinates: true, CompareSide.Right);
        UpdateBounds(
            differences.Select(static item => item.CellReference).Where(static item => item is not null)!,
            sourceCoordinates: false,
            CompareSide.Left);
        _rowCount = Math.Max(
            30,
            _usesCompactRowAlignment
                ? maximumDisplayRow
                : Math.Max(maximumSourceRow, maximumDisplayRow));
        ColumnCount = Math.Max(12, maximumColumn);
        RebuildRows();

        void UpdateBounds(
            IEnumerable<string> references,
            bool sourceCoordinates,
            CompareSide side)
        {
            foreach (var reference in references)
            {
                if (ExcelAddress.TryParse(reference) is not { } address)
                {
                    continue;
                }
                if (sourceCoordinates)
                {
                    maximumSourceRow = Math.Max(maximumSourceRow, address.Row);
                }
                else
                {
                    maximumDisplayRow = Math.Max(maximumDisplayRow, address.Row);
                }
                var displayColumn = sourceCoordinates
                    ? _columnMapping.GetDisplayColumn(side, address.Column)
                    : address.Column;
                maximumColumn = Math.Max(maximumColumn, displayColumn);
            }
        }
    }

    public void SetDifferencesOnly(bool value)
    {
        if (_differencesOnly == value)
        {
            return;
        }

        _differencesOnly = value;
        OnPropertyChanged(nameof(DifferencesOnly));
        RebuildRows();
    }

    public int GetDisplayRowIndex(int originalRow)
    {
        if (originalRow <= 0)
        {
            return -1;
        }

        return _visibleRows is null
            ? originalRow <= _rowCount ? originalRow - 1 : -1
            : Array.BinarySearch(_visibleRows, originalRow) is var index && index >= 0 ? index : -1;
    }

    public GridCellViewModel? GetCell(CompareSide side, int displayRowIndex, int columnIndex)
    {
        var rows = side == CompareSide.Left ? LeftRows : RightRows;
        if (rows is not IList list || displayRowIndex < 0 || displayRowIndex >= list.Count)
        {
            return null;
        }

        return list[displayRowIndex] is GridRowViewModel row && columnIndex >= 0 && columnIndex < row.Cells.Count
            ? row.Cells[columnIndex]
            : null;
    }

    private void RebuildRows()
    {
        var differenceLookup = _differences
            .Where(static difference => ExcelAddress.TryParse(difference.CellReference) is not null)
            .GroupBy(static difference => difference.CellReference!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var oneSided = (_leftPreview is null) != (_rightPreview is null);
        var differenceRowSet = differenceLookup.Keys
            .Concat(oneSided ? _leftPreview?.Cells.Keys ?? _rightPreview?.Cells.Keys ?? [] : [])
            .Select(ExcelAddress.TryParse)
            .Where(static address => address is not null)
            .Select(static address => address!.Value.Row)
            .Concat(_rowAlignments.Values
                .Where(static alignment => alignment.Status is not (
                    RowAlignmentStatus.NotApplied or RowAlignmentStatus.Matched))
                .Select(static alignment => alignment.DisplayRow))
            .ToHashSet();
        _visibleRows = _differencesOnly
            ? differenceRowSet.Order().ToArray()
            : null;

        LeftRows = new VirtualGridRowList(
            _rowCount,
            ColumnCount,
            CompareSide.Left,
            _leftPreview is not null,
            _leftPreview?.Cells,
            _rightPreview?.Cells,
            differenceLookup,
            oneSided,
            _visibleRows,
            differenceRowSet,
            _rowAlignments,
            _usesCompactRowAlignment,
            _columnMapping);
        RightRows = new VirtualGridRowList(
            _rowCount,
            ColumnCount,
            CompareSide.Right,
            _rightPreview is not null,
            _rightPreview?.Cells,
            _leftPreview?.Cells,
            differenceLookup,
            oneSided,
            _visibleRows,
            differenceRowSet,
            _rowAlignments,
            _usesCompactRowAlignment,
            _columnMapping);
    }
}

internal readonly record struct ExcelAddress(int Row, int Column)
{
    public static ExcelAddress? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var address = value.AsSpan().Trim();
        var separator = address.LastIndexOf('!');
        if (separator >= 0)
        {
            address = address[(separator + 1)..];
        }

        var column = 0;
        var index = 0;
        while (index < address.Length && (address[index] == '$' || char.IsLetter(address[index])))
        {
            if (address[index] != '$')
            {
                column = checked((column * 26) + (char.ToUpperInvariant(address[index]) - 'A' + 1));
            }

            index++;
        }

        if (index < address.Length && address[index] == '$')
        {
            index++;
        }

        return column > 0 && int.TryParse(address[index..], out var row) && row > 0
            ? new ExcelAddress(row, column)
            : null;
    }

    public static string ToReference(int row, int column)
    {
        Span<char> buffer = stackalloc char[8];
        var index = buffer.Length;
        var value = column;
        while (value > 0)
        {
            value--;
            buffer[--index] = (char)('A' + (value % 26));
            value /= 26;
        }

        return string.Concat(buffer[index..], row.ToString());
    }
}

internal sealed class GridColumnMappingPlan
{
    private readonly IReadOnlyDictionary<int, int> _rightSourceByDisplayColumn;
    private readonly IReadOnlyDictionary<int, int> _rightDisplayBySourceColumn;
    private readonly IReadOnlySet<int> _consumedRightColumns;

    private GridColumnMappingPlan(
        IReadOnlyDictionary<int, int> rightSourceByDisplayColumn,
        IReadOnlyDictionary<int, int> rightDisplayBySourceColumn,
        IReadOnlySet<int> consumedRightColumns)
    {
        _rightSourceByDisplayColumn = rightSourceByDisplayColumn;
        _rightDisplayBySourceColumn = rightDisplayBySourceColumn;
        _consumedRightColumns = consumedRightColumns;
    }

    public static GridColumnMappingPlan Empty { get; } = new(
        new Dictionary<int, int>(),
        new Dictionary<int, int>(),
        new HashSet<int>());

    public static GridColumnMappingPlan Create(IReadOnlyList<ColumnPair>? pairs)
    {
        if (pairs is not { Count: > 0 })
        {
            return Empty;
        }

        var rightByDisplay = new Dictionary<int, int>();
        var displayByRight = new Dictionary<int, int>();
        foreach (var pair in pairs)
        {
            var left = ExcelAddress.TryParse(pair.LeftColumnIdentifier + "1")?.Column;
            var right = ExcelAddress.TryParse(pair.RightColumnIdentifier + "1")?.Column;
            if (left is not > 0 || right is not > 0 ||
                rightByDisplay.ContainsKey(left.Value) || displayByRight.ContainsKey(right.Value))
            {
                continue;
            }

            rightByDisplay[left.Value] = right.Value;
            displayByRight[right.Value] = left.Value;
        }

        return rightByDisplay.Count == 0
            ? Empty
            : new GridColumnMappingPlan(
                rightByDisplay,
                displayByRight,
                displayByRight.Keys.ToHashSet());
    }

    public int? GetSourceColumn(CompareSide side, int displayColumn)
    {
        if (side == CompareSide.Left)
        {
            return displayColumn;
        }
        if (_rightSourceByDisplayColumn.TryGetValue(displayColumn, out var mappedColumn))
        {
            return mappedColumn;
        }

        return _consumedRightColumns.Contains(displayColumn) ? null : displayColumn;
    }

    public int GetDisplayColumn(CompareSide side, int sourceColumn) =>
        side == CompareSide.Right && _rightDisplayBySourceColumn.TryGetValue(sourceColumn, out var displayColumn)
            ? displayColumn
            : sourceColumn;
}

internal sealed class VirtualGridRowList : IList
{
    private readonly int _columnCount;
    private readonly CompareSide _side;
    private readonly bool _hasWorksheet;
    private readonly IReadOnlyDictionary<string, CellSnapshot> _cells;
    private readonly IReadOnlyDictionary<string, CellSnapshot> _oppositeCells;
    private readonly IReadOnlyDictionary<string, Difference[]> _differences;
    private readonly bool _oneSidedWorksheet;
    private readonly int[]? _visibleRows;
    private readonly IReadOnlySet<int> _differenceRows;
    private readonly IReadOnlyDictionary<int, RowAlignment> _rowAlignments;
    private readonly bool _usesCompactRowAlignment;
    private readonly GridColumnMappingPlan _columnMapping;

    public VirtualGridRowList(
        int rowCount,
        int columnCount,
        CompareSide side,
        bool hasWorksheet,
        IReadOnlyDictionary<string, CellSnapshot>? cells,
        IReadOnlyDictionary<string, CellSnapshot>? oppositeCells,
        IReadOnlyDictionary<string, Difference[]> differences,
        bool oneSidedWorksheet,
        int[]? visibleRows,
        IReadOnlySet<int> differenceRows,
        IReadOnlyDictionary<int, RowAlignment> rowAlignments,
        bool usesCompactRowAlignment,
        GridColumnMappingPlan columnMapping)
    {
        _columnCount = columnCount;
        _side = side;
        _hasWorksheet = hasWorksheet;
        _cells = cells ?? new Dictionary<string, CellSnapshot>(StringComparer.OrdinalIgnoreCase);
        _oppositeCells = oppositeCells ?? new Dictionary<string, CellSnapshot>(StringComparer.OrdinalIgnoreCase);
        _differences = differences;
        _oneSidedWorksheet = oneSidedWorksheet;
        _visibleRows = visibleRows;
        _differenceRows = differenceRows;
        _rowAlignments = rowAlignments;
        _usesCompactRowAlignment = usesCompactRowAlignment;
        _columnMapping = columnMapping;
        Count = visibleRows?.Length ?? rowCount;
    }

    public int Count { get; }

    public bool IsReadOnly => true;

    public bool IsFixedSize => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public object? this[int index]
    {
        get
        {
            if ((uint)index >= Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var displayRow = _visibleRows?[index] ?? index + 1;
            _rowAlignments.TryGetValue(displayRow, out var alignment);
            var leftRow = alignment?.LeftRow;
            var rightRow = alignment?.RightRow;
            if (alignment is null && !_usesCompactRowAlignment)
            {
                leftRow = displayRow;
                rightRow = displayRow;
            }

            if (!_hasWorksheet)
            {
                if (_side == CompareSide.Left)
                {
                    leftRow = null;
                }
                else
                {
                    rightRow = null;
                }
            }

            var row = _side == CompareSide.Left ? leftRow : rightRow;
            var oppositeRow = _side == CompareSide.Left ? rightRow : leftRow;
            var rowDifferent = _differenceRows.Contains(displayRow);
            return new GridRowViewModel
            {
                RowNumber = row ?? 0,
                DisplayRowNumber = displayRow,
                LeftRowNumber = leftRow,
                RightRowNumber = rightRow,
                AlignmentStatus = alignment?.Status ?? RowAlignmentStatus.NotApplied,
                AlignmentMessage = alignment?.Message ?? string.Empty,
                IsDifferent = rowDifferent,
                Cells = new VirtualGridCellList(
                    displayRow,
                    row,
                    oppositeRow,
                    _columnCount,
                    _cells,
                    _oppositeCells,
                    _differences,
                    _oneSidedWorksheet,
                    rowDifferent,
                    alignment?.Message,
                    _side,
                    _columnMapping),
            };
        }
        set => throw new NotSupportedException();
    }

    public IEnumerator GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public int IndexOf(object? value)
    {
        if (value is not GridRowViewModel row)
        {
            return -1;
        }

        return _visibleRows is null
            ? row.DisplayRowNumber - 1
            : Array.BinarySearch(_visibleRows, row.DisplayRowNumber) is var index && index >= 0 ? index : -1;
    }

    public void CopyTo(Array array, int index)
    {
        for (var row = 0; row < Count; row++)
        {
            array.SetValue(this[row], index + row);
        }
    }

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();
}

internal sealed class VirtualGridCellList : IReadOnlyList<GridCellViewModel>
{
    private readonly int _displayRow;
    private readonly int? _row;
    private readonly int? _oppositeRow;
    private readonly IReadOnlyDictionary<string, CellSnapshot> _cells;
    private readonly IReadOnlyDictionary<string, CellSnapshot> _oppositeCells;
    private readonly IReadOnlyDictionary<string, Difference[]> _differences;
    private readonly bool _oneSidedWorksheet;
    private readonly bool _rowDifferent;
    private readonly string? _alignmentMessage;
    private readonly CompareSide _side;
    private readonly GridColumnMappingPlan _columnMapping;

    public VirtualGridCellList(
        int displayRow,
        int? row,
        int? oppositeRow,
        int columnCount,
        IReadOnlyDictionary<string, CellSnapshot> cells,
        IReadOnlyDictionary<string, CellSnapshot> oppositeCells,
        IReadOnlyDictionary<string, Difference[]> differences,
        bool oneSidedWorksheet,
        bool rowDifferent,
        string? alignmentMessage,
        CompareSide side,
        GridColumnMappingPlan columnMapping)
    {
        _displayRow = displayRow;
        _row = row;
        _oppositeRow = oppositeRow;
        Count = columnCount;
        _cells = cells;
        _oppositeCells = oppositeCells;
        _differences = differences;
        _oneSidedWorksheet = oneSidedWorksheet;
        _rowDifferent = rowDifferent;
        _alignmentMessage = alignmentMessage;
        _side = side;
        _columnMapping = columnMapping;
    }

    public int Count { get; }

    public GridCellViewModel this[int index]
    {
        get
        {
            if ((uint)index >= Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var column = index + 1;
            var displayReference = ExcelAddress.ToReference(_displayRow, column);
            var sourceColumn = _columnMapping.GetSourceColumn(_side, column);
            var oppositeSide = _side == CompareSide.Left ? CompareSide.Right : CompareSide.Left;
            var oppositeSourceColumn = _columnMapping.GetSourceColumn(oppositeSide, column);
            var reference = _row is { } row && sourceColumn is { } localColumn
                ? ExcelAddress.ToReference(row, localColumn)
                : null;
            var oppositeReference = _oppositeRow is { } oppositeRow &&
                oppositeSourceColumn is { } otherColumn
                    ? ExcelAddress.ToReference(oppositeRow, otherColumn)
                    : null;
            var cell = reference is not null && _cells.TryGetValue(reference, out var localCell)
                ? localCell
                : null;
            var oppositeCell = oppositeReference is not null &&
                _oppositeCells.TryGetValue(oppositeReference, out var matchedOppositeCell)
                    ? matchedOppositeCell
                    : null;
            var oppositeExists = oppositeCell is not null;
            _differences.TryGetValue(displayReference, out var differences);
            var alignedOneSided = (_row is null) != (_oppositeRow is null);
            var oneSidedValue = (_oneSidedWorksheet || alignedOneSided) && (cell is not null || oppositeExists);
            var valueDifferent = oneSidedValue || differences?.Any(IsValueDifference) == true;
            var dialogDifferences = differences?
                .Where(static difference =>
                    !IsValueDifference(difference)
                    || difference.Description.Contains("空白字符不同", StringComparison.Ordinal))
                .ToArray() ?? [];
            var differenceSummary = differences is { Length: > 0 }
                ? string.Join(
                    Environment.NewLine,
                    differences.Select(static difference => DifferencePresentation.FormatDetail(difference)))
                : oneSidedValue
                    ? cell is null ? "单元格仅对侧存在" : "单元格仅此侧存在"
                    : string.Empty;
            var isDifferent = differenceSummary.Length > 0;
            var isMissing = cell is null && oppositeExists;
            var background = isMissing
                ? new SolidColorBrush(Color.FromRgb(248, 250, 252))
                : isDifferent
                    ? new SolidColorBrush(Color.FromRgb(252, 165, 165))
                    : _rowDifferent
                        ? new SolidColorBrush(Color.FromRgb(255, 241, 242))
                        : ParseBrush(cell?.Format?.BackgroundArgb, Brushes.White);
            var foreground = isDifferent && !valueDifferent
                ? new SolidColorBrush(Color.FromRgb(185, 28, 28))
                : ParseBrush(cell?.Format?.ForegroundArgb, Brushes.Black);
            var displayValue = cell?.DisplayValue ?? string.Empty;
            var displaySegments = TextDifferenceHighlighter.CreateSegments(
                displayValue,
                oppositeCell?.DisplayValue ?? string.Empty,
                valueDifferent);

            var advancedDetails = dialogDifferences
                .Select(static difference => DifferencePresentation.FormatDetail(difference))
                .Concat(string.IsNullOrWhiteSpace(_alignmentMessage) ? [] : [_alignmentMessage])
                .Distinct(StringComparer.Ordinal);

            return new GridCellViewModel
            {
                Address = reference ?? "—",
                DisplayValue = displayValue,
                DisplaySegments = displaySegments,
                RawValue = cell?.RawValue ?? string.Empty,
                Formula = cell?.Formula ?? string.Empty,
                IsDifferent = isDifferent,
                IsValueDifferent = valueDifferent,
                IsMissing = isMissing,
                DifferenceDetails = differenceSummary,
                AdvancedDifferenceDetails = string.Join(
                    Environment.NewLine + Environment.NewLine,
                    advancedDetails),
                Background = background,
                Foreground = foreground,
                FontFamily = string.IsNullOrWhiteSpace(cell?.Format?.FontName)
                    ? new FontFamily("Segoe UI")
                    : new FontFamily(cell.Format.FontName),
                FontSize = cell?.Format?.FontSize is > 0 ? cell.Format.FontSize.Value : 13,
                FontWeight = isDifferent || cell?.Format?.Bold == true
                    ? FontWeights.SemiBold
                    : FontWeights.Normal,
                FontStyle = cell?.Format?.Italic == true ? FontStyles.Italic : FontStyles.Normal,
                TextAlignment = ParseTextAlignment(cell?.Format?.HorizontalAlignment),
                TextWrapping = cell?.Format?.WrapText == true ? TextWrapping.Wrap : TextWrapping.NoWrap,
            };
        }
    }

    private static bool IsValueDifference(Difference difference) => difference.Kind is
        DifferenceKind.Value or DifferenceKind.CellType or DifferenceKind.FormulaResult;

    private static Brush ParseBrush(string? argb, Brush fallback)
    {
        if (string.IsNullOrWhiteSpace(argb))
        {
            return fallback;
        }

        var value = argb.StartsWith('#') ? argb : $"#{argb}";
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private static TextAlignment ParseTextAlignment(string? alignment) => alignment?.ToLowerInvariant() switch
    {
        "center" or "centercontinuous" or "distributed" => TextAlignment.Center,
        "right" => TextAlignment.Right,
        "justify" or "fill" => TextAlignment.Justify,
        _ => TextAlignment.Left,
    };

    public IEnumerator<GridCellViewModel> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
