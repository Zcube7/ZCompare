namespace ZCompare.Core;

public sealed record ComparisonOptions
{
    public bool CompareFormulas { get; init; }
    public bool CompareFormatting { get; init; }
    public bool CompareFonts { get; init; }
    public bool CompareComments { get; init; }
    public bool CompareHyperlinks { get; init; }
    public bool CompareLayout { get; init; }
    public bool CaseSensitive { get; init; } = true;
    public RowAlignmentMode RowAlignmentMode { get; init; } = RowAlignmentMode.Conservative;
    public IReadOnlyList<KeyColumnRule> KeyColumnRules { get; init; } = Array.Empty<KeyColumnRule>();
    public WorksheetPairingMode WorksheetPairingMode { get; init; } = WorksheetPairingMode.Name;
    public IReadOnlyList<WorksheetPair> ManualWorksheetPairs { get; init; } = Array.Empty<WorksheetPair>();
    public IReadOnlyList<WorksheetColumnMapping> ColumnMappings { get; init; } =
        Array.Empty<WorksheetColumnMapping>();
    public int MaxFolderConcurrency { get; init; } = 2;
}

public sealed record FolderScanOptions
{
    public bool IncludeSubdirectories { get; init; } = true;
    public string FilePattern { get; init; } = "*.xlsx";
}

public sealed record KeyColumnRule(
    string WorksheetName,
    int HeaderRow,
    IReadOnlyList<string> ColumnIdentifiers);

public enum WorksheetPairingMode
{
    Name,
    Index,
    Manual
}

public sealed record WorksheetPair(
    string LeftWorksheetName,
    string RightWorksheetName);

public sealed record ColumnPair(
    string LeftColumnIdentifier,
    string RightColumnIdentifier);

public sealed record WorksheetColumnMapping(
    string LeftWorksheetName,
    string RightWorksheetName,
    IReadOnlyList<ColumnPair> ColumnPairs);

public enum RowAlignmentMode
{
    Conservative,
    StrictRowNumber,
    KeyColumns
}

public enum RowAlignmentStatus
{
    NotApplied,
    Matched,
    Inserted,
    Deleted,
    Modified,
    Ambiguous
}

public enum ComparisonStatus
{
    Pending,
    Same,
    Different,
    LeftOnly,
    RightOnly,
    Warning,
    Error,
    Cancelled
}

public enum DifferenceKind
{
    Value,
    CellType,
    Formula,
    FormulaResult,
    NumberFormat,
    Font,
    Fill,
    Border,
    Alignment,
    Comment,
    Hyperlink,
    Merge,
    RowInserted,
    RowDeleted,
    RowAlignmentWarning,
    RowHidden,
    ColumnHidden,
    WorksheetAdded,
    WorksheetRemoved,
    WorksheetOrder,
    WorksheetVisibility,
    UncomparedObject,
    Warning,
    Error
}

public enum CellValueKind
{
    Blank,
    Number,
    Text,
    Boolean,
    Error,
    Date
}

public enum FormulaKind
{
    None,
    Normal,
    Shared,
    Array
}

public enum FormulaCacheState
{
    NotApplicable,
    Missing,
    Empty,
    ValidEmptyString,
    Present
}

public enum ComparisonStage
{
    Hashing,
    Reading,
    Comparing,
    ScanningFolders,
    Completed
}

public sealed record CellFormatSnapshot(
    string NumberFormatCode,
    string FontFingerprint,
    string FillFingerprint,
    string BorderFingerprint,
    string AlignmentFingerprint,
    string? ForegroundArgb = null,
    string? BackgroundArgb = null,
    string? FontName = null,
    double? FontSize = null,
    bool Bold = false,
    bool Italic = false,
    string? HorizontalAlignment = null,
    string? VerticalAlignment = null,
    bool WrapText = false);

public sealed record CellSnapshot(
    string WorksheetName,
    string CellReference,
    CellValueKind ValueKind,
    string? RawValue,
    string? NormalizedValue,
    string DisplayValue,
    string? Formula,
    FormulaKind FormulaKind,
    string? FormulaReference,
    CellFormatSnapshot? Format,
    string? Comment,
    string? CommentAuthor,
    string? Hyperlink,
    bool IsRowHidden,
    bool IsColumnHidden,
    FormulaCacheState FormulaCacheState = FormulaCacheState.NotApplicable,
    string? RichTextFingerprint = null,
    string? HyperlinkFingerprint = null);

public sealed record Difference(
    DifferenceKind Kind,
    string? WorksheetName,
    string? CellReference,
    string Description,
    CellSnapshot? Left,
    CellSnapshot? Right,
    string? LeftDetail,
    string? RightDetail);

public sealed record WorksheetInfo(
    string Name,
    int Index,
    string Visibility,
    int NonEmptyCellCount);

public sealed record WorkbookInfo(
    string FilePath,
    bool Uses1904DateSystem,
    IReadOnlyList<WorksheetInfo> Worksheets,
    IReadOnlyList<string> Warnings);

public sealed record WorksheetPreview(
    string FilePath,
    string WorksheetName,
    IReadOnlyDictionary<string, CellSnapshot> Cells,
    IReadOnlyList<string> MergedRanges,
    IReadOnlySet<uint> HiddenRows,
    IReadOnlyList<string> HiddenColumns);

public sealed record RowAlignment(
    int DisplayRow,
    int? LeftRow,
    int? RightRow,
    RowAlignmentStatus Status,
    string? Message = null);

public sealed record WorksheetCompareResult(
    string WorksheetName,
    ComparisonStatus Status,
    int DifferenceCount,
    IReadOnlyList<Difference> Differences,
    int LeftCellCount,
    int RightCellCount,
    IReadOnlyList<RowAlignment>? RowAlignments = null,
    int LeftRowCount = 0,
    int RightRowCount = 0,
    string? LeftWorksheetName = null,
    string? RightWorksheetName = null,
    IReadOnlyList<ColumnPair>? AppliedColumnPairs = null)
{
    public IReadOnlyList<RowAlignment> RowAlignments { get; init; } =
        RowAlignments ?? Array.Empty<RowAlignment>();

    public IReadOnlyList<RowAlignment> Alignment => RowAlignments;

    public string EffectiveLeftWorksheetName => LeftWorksheetName ?? WorksheetName;

    public string EffectiveRightWorksheetName => RightWorksheetName ?? WorksheetName;

    public IReadOnlyList<ColumnPair> AppliedColumnPairs { get; init; } =
        AppliedColumnPairs ?? Array.Empty<ColumnPair>();

    public int RowDifferenceCount => Differences.Count(static difference =>
        difference.Kind is DifferenceKind.RowInserted or DifferenceKind.RowDeleted);

    public int CellDifferenceCount => Differences
        .Where(static difference =>
            difference.CellReference is not null &&
            difference.Kind is not (DifferenceKind.RowInserted or DifferenceKind.RowDeleted or DifferenceKind.RowAlignmentWarning))
        .Select(static difference => difference.CellReference!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public int DistinctDifferenceCount => RowDifferenceCount + CellDifferenceCount;
}

public sealed record WorkbookCompareResult(
    string LeftPath,
    string RightPath,
    ComparisonStatus Status,
    IReadOnlyList<WorksheetCompareResult> Worksheets,
    IReadOnlyList<Difference> WorkbookDifferences,
    IReadOnlyList<string> Warnings,
    bool ByteIdentical,
    string LeftSha256,
    string RightSha256,
    TimeSpan Elapsed)
{
    public int DifferenceCount =>
        WorkbookDifferences.Count + Worksheets.Sum(static worksheet => worksheet.DifferenceCount);
}

public sealed record FolderFileResult(
    string RelativePath,
    string? LeftPath,
    string? RightPath,
    ComparisonStatus Status,
    int DifferenceCount,
    WorkbookCompareResult? Comparison,
    string? Error);

public sealed record FolderCompareResult(
    string LeftDirectory,
    string RightDirectory,
    ComparisonStatus Status,
    IReadOnlyList<FolderFileResult> Files,
    TimeSpan Elapsed)
{
    public int ErrorFileCount => Files.Count(static file => file.Status == ComparisonStatus.Error);

    public bool HasConfirmedDifferences => Files.Any(static file => file.Status is
        ComparisonStatus.Different or ComparisonStatus.LeftOnly or ComparisonStatus.RightOnly);
}

public sealed record ComparisonProgress(
    ComparisonStage Stage,
    string? CurrentItem,
    int Processed,
    int Total,
    string Message,
    FolderFileResult? CompletedFile = null);
