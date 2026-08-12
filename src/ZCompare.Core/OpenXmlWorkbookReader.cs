using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace ZCompare.Core;

public sealed class OpenXmlWorkbookReader : IWorkbookReader
{
    public async Task<WorkbookInfo> ReadMetadataAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ValidateXlsxPath(filePath);
        using var workbook = WorkbookDocument.Open(filePath, cancellationToken);
        var sheets = new List<WorksheetInfo>(workbook.Sheets.Count);
        foreach (var sheet in workbook.Sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sheets.Add(new WorksheetInfo(sheet.Name, sheet.Index, sheet.Visibility, 0));
        }

        await Task.CompletedTask.ConfigureAwait(false);

        return new WorkbookInfo(filePath, workbook.Uses1904DateSystem, sheets, workbook.Warnings);
    }

    public async IAsyncEnumerable<CellSnapshot> ReadCellsAsync(
        string filePath,
        string worksheetName,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateXlsxPath(filePath);
        using var workbook = WorkbookDocument.Open(filePath, cancellationToken);
        var sheet = workbook.GetSheet(worksheetName);
        await foreach (var cell in workbook.ReadCellsAsync(sheet, cancellationToken).ConfigureAwait(false))
        {
            yield return cell;
        }
    }

    internal async IAsyncEnumerable<CellSnapshot> ReadValueCellsAsync(
        string filePath,
        string worksheetName,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateXlsxPath(filePath);
        using var workbook = WorkbookDocument.Open(
            filePath,
            WorkbookReadProfile.ForComparison(new ComparisonOptions()),
            cancellationToken);
        var sheet = workbook.GetSheet(worksheetName);
        await foreach (var cell in workbook.ReadCellsAsync(sheet, cancellationToken).ConfigureAwait(false))
        {
            yield return cell;
        }
    }

    public async Task<WorksheetPreview> LoadWorksheetPreviewAsync(
        string filePath,
        string worksheetName,
        CancellationToken cancellationToken = default)
    {
        ValidateXlsxPath(filePath);
        using var workbook = WorkbookDocument.Open(filePath, cancellationToken);
        var sheet = workbook.GetSheet(worksheetName);
        var layout = workbook.ReadLayout(sheet, cancellationToken);
        var cells = new Dictionary<string, CellSnapshot>(StringComparer.OrdinalIgnoreCase);
        await foreach (var cell in workbook.ReadCellsAsync(sheet, cancellationToken).ConfigureAwait(false))
        {
            cells[cell.CellReference] = cell;
        }

        return new WorksheetPreview(
            filePath,
            sheet.Name,
            cells,
            layout.MergedRanges,
            layout.HiddenRows,
            layout.HiddenColumns.Select(static range => range.DisplayRange).ToArray());
    }

    internal static void ValidateXlsxPath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!string.Equals(Path.GetExtension(filePath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("首版仅支持 .xlsx 文件。");
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("找不到要读取的工作簿。", filePath);
        }
    }
}

internal readonly record struct WorkbookReadProfile(
    bool ReadFormulaText,
    bool ReadStyles,
    bool ReadRichTextFormatting,
    bool ReadComments,
    bool ReadHyperlinks,
    bool ReadLayout,
    bool SkipValueResolution)
{
    public static WorkbookReadProfile Full { get; } = new(true, true, true, true, true, true, false);

    public static WorkbookReadProfile ByteIdenticalProbe { get; } =
        new(false, false, false, false, false, false, true);

    public static WorkbookReadProfile ForComparison(ComparisonOptions options) => new(
        options.CompareFormulas,
        options.CompareFormatting || options.CompareFonts,
        options.CompareFonts,
        options.CompareComments,
        options.CompareHyperlinks,
        options.CompareLayout,
        false);

    public bool NeedsSidecarData => ReadComments || ReadHyperlinks;
    public bool NeedsWorksheetPreScan => ReadHyperlinks || ReadLayout;
}

internal sealed class WorkbookDocument : IDisposable
{
    private static readonly XNamespace SpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string StrictSpreadsheetNamespace =
        "http://purl.oclc.org/ooxml/spreadsheetml/main";
    private static readonly XNamespace RelationshipNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly ResolvedCellFormat GeneralFormat = new(
        0,
        new CellFormatSnapshot(
            "General",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "FF000000",
            "FFFFFFFF"));

    private readonly FileStream _stream;
    private readonly SpreadsheetDocument _document;
    private readonly WorkbookReadProfile _readProfile;
    private readonly IReadOnlyList<RichTextValue> _sharedStrings;
    private readonly StyleContext? _styles;
    private readonly Dictionary<SheetEntry, WorksheetLayout> _layoutCache = [];
    private bool _disposed;

    private WorkbookDocument(
        string filePath,
        FileStream stream,
        SpreadsheetDocument document,
        WorkbookReadProfile readProfile,
        CancellationToken cancellationToken)
    {
        FilePath = filePath;
        _stream = stream;
        _document = document;
        _readProfile = readProfile;

        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("XLSX 缺少 workbook 部件。");
        var workbook = workbookPart.Workbook
            ?? throw new InvalidDataException("XLSX 缺少 workbook.xml 根元素。");
        cancellationToken.ThrowIfCancellationRequested();
        _sharedStrings = readProfile.SkipValueResolution
            ? Array.Empty<RichTextValue>()
            : ReadSharedStrings(
                workbookPart.SharedStringTablePart,
                readProfile.ReadRichTextFormatting,
                cancellationToken);
        _styles = readProfile.ReadStyles ? StyleContext.Create(workbookPart) : null;
        cancellationToken.ThrowIfCancellationRequested();
        Uses1904DateSystem = workbook.WorkbookProperties?.Date1904?.Value ?? false;
        Sheets = ReadSheets(workbookPart);
        Warnings = ReadWorkbookWarnings(workbookPart, Sheets, cancellationToken);
    }

    public string FilePath { get; }
    public bool Uses1904DateSystem { get; }
    public IReadOnlyList<SheetEntry> Sheets { get; }
    public IReadOnlyList<string> Warnings { get; }
    public CellFormatSnapshot DefaultFormat => (_styles?.GetFormat(0) ?? GeneralFormat).Snapshot;
    private bool IsValueOnlyProfile =>
        !_readProfile.ReadFormulaText &&
        !_readProfile.ReadStyles &&
        !_readProfile.ReadRichTextFormatting &&
        !_readProfile.ReadComments &&
        !_readProfile.ReadHyperlinks &&
        !_readProfile.ReadLayout;

    public static WorkbookDocument Open(string filePath, CancellationToken cancellationToken = default) =>
        Open(filePath, WorkbookReadProfile.Full, cancellationToken);

    public static WorkbookDocument Open(
        string filePath,
        WorkbookReadProfile readProfile,
        CancellationToken cancellationToken = default)
    {
        OpenXmlWorkbookReader.ValidateXlsxPath(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 128,
            FileOptions.SequentialScan);

        try
        {
            EnsureSupportedSpreadsheetmlNamespace(stream, cancellationToken);
            var document = SpreadsheetDocument.Open(
                stream,
                false,
                new OpenSettings { AutoSave = false });
            return new WorkbookDocument(filePath, stream, document, readProfile, cancellationToken);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void EnsureSupportedSpreadsheetmlNamespace(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (Ole2ContainerInspector.Inspect(stream))
        {
            case Ole2ContainerKind.LegacyWorkbook:
                throw new NotSupportedException(
                    "文件内容是旧版二进制 XLS 工作簿，不是真正的 XLSX。请在 Excel 中另存为普通 XLSX 后再比较。");
            case Ole2ContainerKind.EncryptedPackage:
                throw new NotSupportedException(
                    "文件是加密或受密码保护的 Office 工作簿。请先在 Excel 中解除密码保护，再另存为普通 XLSX 后比较。");
            case Ole2ContainerKind.Unknown:
                throw new NotSupportedException(
                    "文件是 OLE 复合文档，但无法确认是旧版 XLS 还是加密 Office 文件；它不是真正的普通 XLSX。请在 Excel 中重新另存为 XLSX 后比较。");
        }

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            var workbookEntry = archive.Entries.FirstOrDefault(static entry =>
                string.Equals(entry.FullName, "xl/workbook.xml", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("XLSX 缺少 xl/workbook.xml 部件。");
            using var workbookStream = workbookEntry.Open();
            using var reader = XmlReader.Create(workbookStream, XmlSettings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (string.Equals(reader.NamespaceURI, StrictSpreadsheetNamespace, StringComparison.Ordinal))
                {
                    throw new NotSupportedException(
                        "检测到 ISO Strict XLSX。当前版本尚不能可靠比较 Strict 工作簿，请先在 Excel 中另存为普通 XLSX 后再比较。");
                }

                if (!string.Equals(reader.NamespaceURI, SpreadsheetNamespace.NamespaceName, StringComparison.Ordinal))
                {
                    throw new NotSupportedException(
                        $"不支持的 SpreadsheetML 命名空间：{reader.NamespaceURI}");
                }
                break;
            }
        }

        stream.Position = 0;
    }

    public SheetEntry GetSheet(string worksheetName) =>
        Sheets.FirstOrDefault(sheet => string.Equals(sheet.Name, worksheetName, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"工作簿中不存在工作表“{worksheetName}”。");

    public WorksheetLayout ReadLayout(
        SheetEntry sheet,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_layoutCache.TryGetValue(sheet, out var cached))
        {
            return cached;
        }

        var mergedRanges = new List<string>();
        var hiddenRows = new HashSet<uint>();
        var explicitRows = new HashSet<uint>();
        var rowsWithCells = new HashSet<uint>();
        uint currentExplicitRow = 0;
        var hiddenColumnSet = new HashSet<uint>();
        var hyperlinks = new List<HyperlinkEntry>();
        var unhandledObjects = new HashSet<string>(StringComparer.Ordinal);
        var comments = _readProfile.ReadComments
            ? ReadComments(sheet.Part, cancellationToken)
            : new Dictionary<string, CellComment>(StringComparer.OrdinalIgnoreCase);
        if (!_readProfile.NeedsWorksheetPreScan)
        {
            var sidecarOnly = new WorksheetLayout(
                mergedRanges,
                hiddenRows,
                Array.Empty<uint>().ToHashSet(),
                Array.Empty<ColumnRange>(),
                hyperlinks,
                comments,
                []);
            _layoutCache[sheet] = sidecarOnly;
            return sidecarOnly;
        }

        var externalLinks = _readProfile.ReadHyperlinks
            ? sheet.Part.HyperlinkRelationships.ToDictionary(
                static relationship => relationship.Id,
                static relationship => relationship.Uri.ToString(),
                StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        var hasComments = sheet.Part.GetPartsOfType<WorksheetCommentsPart>().Any();
        using var stream = sheet.Part.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = XmlReader.Create(stream, XmlSettings);
        var scanned = 0;
        while (reader.Read())
        {
            if ((++scanned & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "row" when _readProfile.ReadLayout:
                    if (!uint.TryParse(reader.GetAttribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var row))
                    {
                        row = currentExplicitRow + 1;
                    }
                    currentExplicitRow = row;
                    explicitRows.Add(row);
                    if (IsTrue(reader.GetAttribute("hidden")))
                    {
                        hiddenRows.Add(row);
                    }
                    break;
                case "c" when _readProfile.ReadLayout && currentExplicitRow > 0:
                    rowsWithCells.Add(currentExplicitRow);
                    break;
                case "col" when _readProfile.ReadLayout:
                    if (uint.TryParse(reader.GetAttribute("min"), NumberStyles.None, CultureInfo.InvariantCulture, out var min) &&
                        uint.TryParse(reader.GetAttribute("max"), NumberStyles.None, CultureInfo.InvariantCulture, out var max))
                    {
                        ApplyHiddenColumns(hiddenColumnSet, min, max, IsTrue(reader.GetAttribute("hidden")));
                    }
                    break;
                case "mergeCell" when _readProfile.ReadLayout:
                    var mergeReference = reader.GetAttribute("ref");
                    if (!string.IsNullOrEmpty(mergeReference))
                    {
                        mergedRanges.Add(mergeReference);
                    }
                    break;
                case "hyperlink" when _readProfile.ReadHyperlinks:
                    var reference = reader.GetAttribute("ref");
                    if (!string.IsNullOrEmpty(reference))
                    {
                        var relationshipId = reader.GetAttribute("id", RelationshipNamespace.NamespaceName);
                        var location = reader.GetAttribute("location");
                        var target = relationshipId is not null && externalLinks.TryGetValue(relationshipId, out var uri)
                            ? uri
                            : null;
                        hyperlinks.Add(new HyperlinkEntry(
                            CellReferenceUtility.NormalizeRange(reference),
                            target,
                            location,
                            reader.GetAttribute("display"),
                            reader.GetAttribute("tooltip")));
                    }
                    break;
                case "conditionalFormatting" when _readProfile.ReadLayout:
                    unhandledObjects.Add("条件格式");
                    break;
                case "dataValidations" when _readProfile.ReadLayout:
                    unhandledObjects.Add("数据验证");
                    break;
                case "drawing" when _readProfile.ReadLayout:
                case "picture" when _readProfile.ReadLayout:
                    unhandledObjects.Add("图表/图片/形状");
                    break;
                case "legacyDrawing" when _readProfile.ReadLayout && !hasComments:
                    unhandledObjects.Add("图表/图片/形状");
                    break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_readProfile.ReadLayout && sheet.Part.PivotTableParts.Any())
        {
            unhandledObjects.Add("数据透视表");
        }

        explicitRows.ExceptWith(rowsWithCells);

        var layout = new WorksheetLayout(
            mergedRanges,
            hiddenRows,
            explicitRows,
            ToColumnRanges(hiddenColumnSet),
            hyperlinks,
            comments,
            unhandledObjects.ToArray());
        _layoutCache[sheet] = layout;
        return layout;
    }

    private static void ApplyHiddenColumns(
        HashSet<uint> hiddenColumns,
        uint minimum,
        uint maximum,
        bool hidden)
    {
        const uint maximumExcelColumn = 16_384;
        minimum = Math.Max(1u, minimum);
        maximum = Math.Min(maximumExcelColumn, maximum);
        if (minimum > maximum)
        {
            return;
        }

        for (var column = minimum; column <= maximum; column++)
        {
            if (hidden)
            {
                hiddenColumns.Add(column);
            }
            else
            {
                hiddenColumns.Remove(column);
            }
        }
    }

    private static IReadOnlyList<ColumnRange> ToColumnRanges(HashSet<uint> hiddenColumns)
    {
        if (hiddenColumns.Count == 0)
        {
            return [];
        }

        var result = new List<ColumnRange>();
        var ordered = hiddenColumns.Order().ToArray();
        var minimum = ordered[0];
        var maximum = minimum;
        foreach (var column in ordered.AsSpan(1))
        {
            if (column == maximum + 1)
            {
                maximum = column;
                continue;
            }
            result.Add(new ColumnRange(minimum, maximum));
            minimum = maximum = column;
        }
        result.Add(new ColumnRange(minimum, maximum));
        return result;
    }

    public async IAsyncEnumerable<CellSnapshot> ReadCellsAsync(
        SheetEntry sheet,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var layout = _readProfile.NeedsSidecarData || _readProfile.ReadLayout
            ? ReadLayout(sheet, cancellationToken)
            : WorksheetLayout.Empty;
        var sidecarReferences = layout.Comments.Keys
            .Concat(layout.Hyperlinks.Select(static hyperlink => hyperlink.AnchorReference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reference => reference, CellReferenceComparer.Instance)
            .ToArray();
        var sidecarIndex = 0;
        var sharedFormulas = _readProfile.ReadFormulaText
            ? new Dictionary<uint, SharedFormula>()
            : null;
        uint currentRow = 0;
        var currentRowHidden = false;
        var fallbackColumn = 0;
        var processed = 0;

        using var stream = sheet.Part.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = XmlReader.Create(stream, XmlSettings);
        var scanned = 0;
        while (reader.Read())
        {
            if ((++scanned & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "row")
            {
                uint.TryParse(reader.GetAttribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out currentRow);
                currentRowHidden = _readProfile.ReadLayout && IsTrue(reader.GetAttribute("hidden"));
                fallbackColumn = 0;
                continue;
            }

            if (reader.LocalName != "c")
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var cell = ReadCellElement(
                reader,
                sheet.Name,
                currentRow,
                ref fallbackColumn,
                currentRowHidden,
                layout,
                sharedFormulas);
            while (sidecarIndex < sidecarReferences.Length &&
                CellReferenceUtility.Compare(sidecarReferences[sidecarIndex], cell.CellReference) < 0)
            {
                yield return CreateSidecarCell(sheet.Name, sidecarReferences[sidecarIndex], layout);
                sidecarIndex++;
            }
            if (sidecarIndex < sidecarReferences.Length &&
                string.Equals(sidecarReferences[sidecarIndex], cell.CellReference, StringComparison.OrdinalIgnoreCase))
            {
                sidecarIndex++;
            }
            yield return cell;

            processed++;
            if ((processed & 4095) == 0)
            {
                await Task.Yield();
            }
        }

        while (sidecarIndex < sidecarReferences.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return CreateSidecarCell(sheet.Name, sidecarReferences[sidecarIndex], layout);
            sidecarIndex++;
        }
    }

    private CellSnapshot CreateSidecarCell(string sheetName, string reference, WorksheetLayout layout)
    {
        layout.Comments.TryGetValue(reference, out var comment);
        var isRowHidden = CellReferenceUtility.TryParse(reference, out var column, out var row) &&
            layout.HiddenRows.Contains((uint)row);
        var isColumnHidden = column > 0 && layout.HiddenColumns.Any(range => range.Contains((uint)column));
        return new CellSnapshot(
            sheetName,
            reference,
            CellValueKind.Blank,
            null,
            null,
            string.Empty,
            null,
            FormulaKind.None,
            null,
            (_styles?.GetFormat(0) ?? GeneralFormat).Snapshot,
            comment?.Text,
            comment?.Author,
            layout.FindHyperlink(reference),
            isRowHidden,
            isColumnHidden,
            FormulaCacheState.NotApplicable,
            null,
            layout.FindHyperlinkFingerprint(reference));
    }

    private CellSnapshot ReadCellElement(
        XmlReader reader,
        string sheetName,
        uint currentRow,
        ref int fallbackColumn,
        bool currentRowHidden,
        WorksheetLayout layout,
        Dictionary<uint, SharedFormula>? sharedFormulas)
    {
        if (IsValueOnlyProfile)
        {
            return ReadValueOnlyCellElement(
                reader,
                sheetName,
                currentRow,
                ref fallbackColumn);
        }

        using var subtree = reader.ReadSubtree();
        var element = XElement.Load(subtree, LoadOptions.None);
        var reference = (string?)element.Attribute("r");
        if (string.IsNullOrEmpty(reference))
        {
            fallbackColumn++;
            reference = CellReferenceUtility.ToColumnName(fallbackColumn) + currentRow.ToString(CultureInfo.InvariantCulture);
        }
        else if (CellReferenceUtility.TryParse(reference, out var parsedColumn, out _))
        {
            fallbackColumn = parsedColumn;
        }

        var format = GeneralFormat;
        if (_styles is not null)
        {
            var styleIndex = uint.TryParse(
                (string?)element.Attribute("s"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedStyle)
                ? parsedStyle
                : 0u;
            format = _styles.GetFormat(styleIndex);
        }
        var dataType = (string?)element.Attribute("t");
        var formulaElement = element.Element(SpreadsheetNamespace + "f");
        var formulaText = _readProfile.ReadFormulaText ? formulaElement?.Value : null;
        var formulaReference = _readProfile.ReadFormulaText
            ? (string?)formulaElement?.Attribute("ref")
            : null;
        var formulaKind = FormulaKind.None;
        if (formulaElement is not null)
        {
            var formulaType = (string?)formulaElement.Attribute("t");
            formulaKind = formulaType switch
            {
                "shared" => FormulaKind.Shared,
                "array" => FormulaKind.Array,
                _ => FormulaKind.Normal
            };

            if (_readProfile.ReadFormulaText &&
                sharedFormulas is not null &&
                formulaKind == FormulaKind.Shared &&
                uint.TryParse((string?)formulaElement.Attribute("si"), NumberStyles.None, CultureInfo.InvariantCulture, out var sharedIndex))
            {
                if (!string.IsNullOrEmpty(formulaText))
                {
                    var shared = new SharedFormula(reference, formulaText, formulaReference);
                    sharedFormulas[sharedIndex] = shared;
                }
                else if (sharedFormulas.TryGetValue(sharedIndex, out var shared))
                {
                    formulaText = FormulaTranslator.Translate(shared.Formula, shared.AnchorReference, reference);
                    formulaReference = shared.Range;
                }
            }
        }

        var cachedValueElement = element.Element(SpreadsheetNamespace + "v");
        var cachedValue = cachedValueElement?.Value;
        var formulaCacheState = formulaElement is null
            ? FormulaCacheState.NotApplicable
            : cachedValueElement is null
                ? FormulaCacheState.Missing
                : cachedValue is { Length: > 0 }
                    ? FormulaCacheState.Present
                    : string.Equals(dataType, "str", StringComparison.Ordinal)
                        ? FormulaCacheState.ValidEmptyString
                        : FormulaCacheState.Empty;
        RichTextValue? inlineText = element.Element(SpreadsheetNamespace + "is") is { } inlineString
            ? ReadRichText(inlineString, _readProfile.ReadRichTextFormatting)
            : null;
        var value = CreateValue(dataType, cachedValue, inlineText, format);
        layout.Comments.TryGetValue(reference, out var comment);
        var hyperlink = layout.FindHyperlink(reference);
        var isColumnHidden = CellReferenceUtility.TryParse(reference, out var column, out _) &&
            layout.HiddenColumns.Any(range => range.Contains((uint)column));

        return new CellSnapshot(
            sheetName,
            reference,
            value.Kind,
            value.Raw,
            value.Normalized,
            value.Display,
            formulaText,
            formulaKind,
            formulaReference,
            format.Snapshot,
            comment?.Text,
            comment?.Author,
            hyperlink,
            currentRowHidden,
            isColumnHidden,
            formulaCacheState,
            value.RichTextFingerprint,
            layout.FindHyperlinkFingerprint(reference));
    }

    private CellSnapshot ReadValueOnlyCellElement(
        XmlReader reader,
        string sheetName,
        uint currentRow,
        ref int fallbackColumn)
    {
        var reference = reader.GetAttribute("r");
        if (string.IsNullOrEmpty(reference))
        {
            fallbackColumn++;
            reference = CellReferenceUtility.ToColumnName(fallbackColumn) +
                currentRow.ToString(CultureInfo.InvariantCulture);
        }
        else if (CellReferenceUtility.TryParse(reference, out var parsedColumn, out _))
        {
            fallbackColumn = parsedColumn;
        }

        var dataType = reader.GetAttribute("t");
        var formulaKind = FormulaKind.None;
        var cachedValueDepth = -1;
        string? cachedValue = null;
        string? inlineText = null;

        using (var subtree = reader.ReadSubtree())
        {
            string? firstInlineText = null;
            StringBuilder? inlineBuilder = null;
            var inlineTextDepth = -1;
            var phoneticDepth = -1;
            while (subtree.Read())
            {
                if (subtree.NodeType == XmlNodeType.Element)
                {
                    if (subtree.Depth == 1 && subtree.LocalName == "f")
                    {
                        formulaKind = subtree.GetAttribute("t") switch
                        {
                            "shared" => FormulaKind.Shared,
                            "array" => FormulaKind.Array,
                            _ => FormulaKind.Normal
                        };
                    }
                    else if (subtree.Depth == 1 && subtree.LocalName == "v")
                    {
                        cachedValue = string.Empty;
                        cachedValueDepth = subtree.IsEmptyElement ? -2 : subtree.Depth;
                    }
                    else if (!_readProfile.SkipValueResolution && subtree.LocalName == "rPh")
                    {
                        phoneticDepth = subtree.IsEmptyElement ? -1 : subtree.Depth;
                    }
                    else if (!_readProfile.SkipValueResolution && phoneticDepth < 0 && subtree.LocalName == "t")
                    {
                        inlineTextDepth = subtree.Depth;
                    }
                }
                else if (subtree.NodeType == XmlNodeType.EndElement)
                {
                    if (subtree.Depth == inlineTextDepth && subtree.LocalName == "t")
                    {
                        inlineTextDepth = -1;
                    }
                    if (subtree.Depth == phoneticDepth && subtree.LocalName == "rPh")
                    {
                        phoneticDepth = -1;
                    }
                    if (subtree.Depth == cachedValueDepth && subtree.LocalName == "v")
                    {
                        cachedValueDepth = -2;
                    }
                }
                else if (!_readProfile.SkipValueResolution &&
                    subtree.Depth > inlineTextDepth && inlineTextDepth >= 0 &&
                    subtree.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
                        XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                {
                    if (firstInlineText is null)
                    {
                        firstInlineText = subtree.Value;
                    }
                    else
                    {
                        inlineBuilder ??= new StringBuilder(firstInlineText);
                        inlineBuilder.Append(subtree.Value);
                    }
                }

                if (cachedValueDepth == 1 && subtree.Depth > cachedValueDepth &&
                    subtree.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
                        XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                {
                    cachedValue = _readProfile.SkipValueResolution
                        ? "1"
                        : cachedValue is null
                            ? subtree.Value
                            : cachedValue + subtree.Value;
                }
            }
            if (!_readProfile.SkipValueResolution)
            {
                inlineText = inlineBuilder?.ToString() ?? firstInlineText ??
                    (string.Equals(dataType, "inlineStr", StringComparison.Ordinal) ? string.Empty : null);
            }
        }

        var formulaCacheState = formulaKind == FormulaKind.None
            ? FormulaCacheState.NotApplicable
            : cachedValueDepth == -1
                ? FormulaCacheState.Missing
                : cachedValue is { Length: > 0 }
                    ? FormulaCacheState.Present
                    : string.Equals(dataType, "str", StringComparison.Ordinal)
                        ? FormulaCacheState.ValidEmptyString
                        : FormulaCacheState.Empty;
        var value = CreateValue(dataType, cachedValue, inlineText is null
            ? null
            : new RichTextValue(inlineText, null), GeneralFormat);

        return new CellSnapshot(
            sheetName,
            reference,
            value.Kind,
            value.Raw,
            value.Normalized,
            value.Display,
            null,
            formulaKind,
            null,
            GeneralFormat.Snapshot,
            null,
            null,
            null,
            false,
            false,
            formulaCacheState);
    }

    private CellValue CreateValue(
        string? dataType,
        string? cachedValue,
        RichTextValue? inlineText,
        ResolvedCellFormat format)
    {
        if (_readProfile.SkipValueResolution)
        {
            return new CellValue(CellValueKind.Blank, null, null, string.Empty);
        }

        switch (dataType)
        {
            case "s":
                if (int.TryParse(cachedValue, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                    index >= 0 && index < _sharedStrings.Count)
                {
                    var sharedText = _sharedStrings[index];
                    return new CellValue(
                        CellValueKind.Text,
                        sharedText.Text,
                        sharedText.Text,
                        sharedText.Text,
                        sharedText.Fingerprint);
                }
                return new CellValue(CellValueKind.Text, cachedValue, cachedValue, cachedValue ?? string.Empty);
            case "inlineStr":
                return new CellValue(
                    CellValueKind.Text,
                    inlineText?.Text,
                    inlineText?.Text,
                    inlineText?.Text ?? string.Empty,
                    inlineText?.Fingerprint);
            case "str":
                return new CellValue(CellValueKind.Text, cachedValue, cachedValue, cachedValue ?? string.Empty);
            case "b":
                var boolean = cachedValue == "1";
                return new CellValue(CellValueKind.Boolean, cachedValue, boolean ? "1" : "0", boolean ? "TRUE" : "FALSE");
            case "e":
                return new CellValue(CellValueKind.Error, cachedValue, cachedValue, cachedValue ?? string.Empty);
            case "d":
                var normalizedDate = DateTimeOffset.TryParse(
                    cachedValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                    out var date)
                    ? date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                    : cachedValue;
                return new CellValue(CellValueKind.Date, cachedValue, normalizedDate, cachedValue ?? string.Empty);
        }

        if (cachedValue is null)
        {
            return new CellValue(CellValueKind.Blank, null, null, string.Empty);
        }

        // The value-only comparer handles uncommon alternate numeric spellings lazily.
        // Keeping the raw spelling here avoids BigInteger parsing and canonical-string
        // allocation for the overwhelmingly common case where both sides are identical.
        if (IsValueOnlyProfile)
        {
            return new CellValue(CellValueKind.Number, cachedValue, cachedValue, cachedValue);
        }

        if (!ExactNumber.TryParse(cachedValue, out var number))
        {
            return new CellValue(CellValueKind.Number, cachedValue, cachedValue, cachedValue);
        }

        var temporalKind = ExcelDisplayFormatter.GetTemporalKind(
            format.NumberFormatId,
            format.Snapshot.NumberFormatCode);
        var normalizedNumber = temporalKind is ExcelTemporalKind.Date or ExcelTemporalKind.DateTime
            ? (Uses1904DateSystem ? number.AddInteger(1462) : number).ToCanonicalString()
            : number.ToCanonicalString();
        var normalized = temporalKind switch
        {
            ExcelTemporalKind.Date => "date:" + normalizedNumber,
            ExcelTemporalKind.DateTime => "datetime:" + normalizedNumber,
            ExcelTemporalKind.TimeOfDay => "time:" + normalizedNumber,
            ExcelTemporalKind.Duration => "duration:" + normalizedNumber,
            _ => "number:" + normalizedNumber
        };
        var display = ExcelDisplayFormatter.FormatNumber(
            cachedValue,
            number,
            format.NumberFormatId,
            format.Snapshot.NumberFormatCode,
            Uses1904DateSystem);
        return new CellValue(
            temporalKind == ExcelTemporalKind.None ? CellValueKind.Number : CellValueKind.Date,
            cachedValue,
            normalized,
            display);
    }

    private static IReadOnlyList<SheetEntry> ReadSheets(WorkbookPart workbookPart)
    {
        var result = new List<SheetEntry>();
        var index = 0;
        var workbook = workbookPart.Workbook
            ?? throw new InvalidDataException("XLSX 缺少 workbook.xml 根元素。");
        foreach (var sheet in workbook.Sheets?.Elements<Sheet>() ?? [])
        {
            var relationshipId = sheet.Id?.Value;
            if (string.IsNullOrEmpty(relationshipId) ||
                workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
            {
                continue;
            }

            result.Add(new SheetEntry(
                sheet.Name?.Value ?? $"Sheet{index + 1}",
                index,
                sheet.State?.Value.ToString() ?? "Visible",
                worksheetPart));
            index++;
        }

        return result;
    }

    private static IReadOnlyList<RichTextValue> ReadSharedStrings(
        SharedStringTablePart? part,
        bool includeFormatting,
        CancellationToken cancellationToken)
    {
        if (part is null)
        {
            return [];
        }

        var values = new List<RichTextValue>();
        using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = XmlReader.Create(stream, XmlSettings);
        var scanned = 0;
        while (reader.Read())
        {
            if ((++scanned & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                using var subtree = reader.ReadSubtree();
                if (includeFormatting)
                {
                    var element = XElement.Load(subtree, LoadOptions.None);
                    values.Add(ReadRichText(element, includeFormatting: true));
                }
                else
                {
                    values.Add(new RichTextValue(ReadPlainRichText(subtree), null));
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return values;
    }

    private static string ReadPlainRichText(XmlReader reader)
    {
        string? firstText = null;
        StringBuilder? text = null;
        var textDepth = -1;
        var phoneticDepth = -1;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == "rPh")
                {
                    phoneticDepth = reader.IsEmptyElement ? -1 : reader.Depth;
                }
                else if (phoneticDepth < 0 && reader.LocalName == "t")
                {
                    textDepth = reader.Depth;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.Depth == textDepth && reader.LocalName == "t")
                {
                    textDepth = -1;
                }
                if (reader.Depth == phoneticDepth && reader.LocalName == "rPh")
                {
                    phoneticDepth = -1;
                }
            }
            else if (textDepth >= 0 && reader.Depth > textDepth &&
                reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
                    XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                if (firstText is null)
                {
                    firstText = reader.Value;
                }
                else
                {
                    text ??= new StringBuilder(firstText);
                    text.Append(reader.Value);
                }
            }
        }
        return text?.ToString() ?? firstText ?? string.Empty;
    }

    private static RichTextValue ReadRichText(XElement container, bool includeFormatting)
    {
        var text = new StringBuilder();
        List<RichTextSegment>? segments = includeFormatting ? [] : null;
        foreach (var child in container.Elements())
        {
            string? segmentText = null;
            var formatting = string.Empty;
            if (child.Name == SpreadsheetNamespace + "t")
            {
                segmentText = child.Value;
            }
            else if (child.Name == SpreadsheetNamespace + "r")
            {
                segmentText = string.Concat(
                    child.Elements(SpreadsheetNamespace + "t").Select(static item => item.Value));
                if (includeFormatting)
                {
                    formatting = CanonicalRichTextProperties(child.Element(SpreadsheetNamespace + "rPr"));
                }
            }

            // rPh and phoneticPr are pronunciation metadata, not displayed cell text.
            if (segmentText is null)
            {
                continue;
            }

            text.Append(segmentText);
            if (segments is not null && segmentText.Length > 0)
            {
                if (segments.Count > 0 && string.Equals(segments[^1].Formatting, formatting, StringComparison.Ordinal))
                {
                    segments[^1] = segments[^1] with { Length = segments[^1].Length + segmentText.Length };
                }
                else
                {
                    segments.Add(new RichTextSegment(segmentText.Length, formatting));
                }
            }
        }

        string? fingerprint = null;
        if (segments is { Count: > 0 } && segments.Any(static segment => segment.Formatting.Length > 0))
        {
            fingerprint = string.Join(
                '|',
                segments.Select(static segment => $"{segment.Length}:{segment.Formatting}"));
        }
        return new RichTextValue(text.ToString(), fingerprint);
    }

    private static string CanonicalRichTextProperties(XElement? properties)
    {
        if (properties is null)
        {
            return string.Empty;
        }

        var result = new StringBuilder();
        foreach (var property in properties.Elements().OrderBy(static item => item.Name.LocalName, StringComparer.Ordinal))
        {
            var isBooleanToggle = property.Name.LocalName is
                "b" or "i" or "strike" or "outline" or "shadow" or "condense" or "extend";
            if (isBooleanToggle && !ReadBooleanProperty(property))
            {
                continue;
            }
            if (property.Name.LocalName == "u" &&
                string.Equals((string?)property.Attribute("val"), "none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Append(property.Name.LocalName);
            if (!isBooleanToggle)
            {
                foreach (var attribute in property.Attributes()
                    .OrderBy(static item => item.Name.LocalName, StringComparer.Ordinal)
                    .ThenBy(static item => item.Name.NamespaceName, StringComparer.Ordinal))
                {
                    result.Append('[')
                        .Append(attribute.Name.LocalName)
                        .Append('=')
                        .Append(attribute.Value)
                        .Append(']');
                }
            }
            result.Append(';');
        }
        return result.ToString();
    }

    private static bool ReadBooleanProperty(XElement element)
    {
        var value = (string?)element.Attribute("val");
        return value is null || IsTrue(value);
    }

    private static IReadOnlyDictionary<string, CellComment> ReadComments(
        WorksheetPart part,
        CancellationToken cancellationToken)
    {
        var commentsPart = part.GetPartsOfType<WorksheetCommentsPart>().FirstOrDefault();
        if (commentsPart is null)
        {
            return new Dictionary<string, CellComment>(StringComparer.OrdinalIgnoreCase);
        }

        using var stream = commentsPart.GetStream(FileMode.Open, FileAccess.Read);
        var document = XDocument.Load(stream, LoadOptions.None);
        var authors = document.Descendants(SpreadsheetNamespace + "author")
            .Select(static author => author.Value)
            .ToArray();
        var comments = new Dictionary<string, CellComment>(StringComparer.OrdinalIgnoreCase);
        var processed = 0;
        foreach (var element in document.Descendants(SpreadsheetNamespace + "comment"))
        {
            if ((++processed & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var reference = (string?)element.Attribute("ref");
            if (string.IsNullOrEmpty(reference))
            {
                continue;
            }

            var author = string.Empty;
            if (int.TryParse((string?)element.Attribute("authorId"), out var authorIndex) &&
                authorIndex >= 0 && authorIndex < authors.Length)
            {
                author = authors[authorIndex];
            }

            var text = string.Concat(element.Descendants(SpreadsheetNamespace + "t").Select(static node => node.Value));
            comments[reference] = new CellComment(text, author);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return comments;
    }

    private static IReadOnlyList<string> ReadWorkbookWarnings(
        WorkbookPart workbookPart,
        IReadOnlyList<SheetEntry> sheets,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var workbook = workbookPart.Workbook
            ?? throw new InvalidDataException("XLSX 缺少 workbook.xml 根元素。");
        var calculation = workbook.CalculationProperties;
        if (calculation is not null)
        {
            var attributes = calculation.GetAttributes().ToDictionary(
                static attribute => attribute.LocalName,
                static attribute => attribute.Value,
                StringComparer.OrdinalIgnoreCase);
            var isManual = attributes.TryGetValue("calcMode", out var mode) &&
                string.Equals(mode, "manual", StringComparison.OrdinalIgnoreCase);
            var recalculatesOnOpen =
                (attributes.TryGetValue("fullCalcOnLoad", out var fullCalc) && IsTrue(fullCalc)) ||
                (attributes.TryGetValue("forceFullCalc", out var forceCalc) && IsTrue(forceCalc));
            if ((isManual || recalculatesOnOpen) && WorkbookContainsFormula(sheets, cancellationToken))
            {
                if (isManual)
                {
                    warnings.Add("工作簿使用手动计算，公式缓存结果可能过期。");
                }
                if (recalculatesOnOpen)
                {
                    warnings.Add("工作簿要求打开后重新计算，当前仅比较已保存的公式缓存结果。");
                }
            }
        }

        if (workbook.DefinedNames?.ChildElements.Count > 0)
        {
            warnings.Add("检测到名称；首版不比较名称定义。");
        }
        if (workbookPart.Parts.Any(static pair => pair.OpenXmlPart.ContentType.Contains("externalLink", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add("检测到外部链接；首版不比较外部链接内容。");
        }
        if ((workbook.Sheets?.Elements<Sheet>() ?? []).Any(sheet =>
        {
            var relationshipId = sheet.Id?.Value;
            return !string.IsNullOrEmpty(relationshipId) &&
                workbookPart.GetPartById(relationshipId).ContentType.Contains("chartsheet", StringComparison.OrdinalIgnoreCase);
        }))
        {
            warnings.Add("检测到图表工作表；首版不比较图表工作表内容。");
        }
        if (workbookPart.WorksheetParts.Any(static worksheet =>
            worksheet.Parts.Any(static pair =>
                pair.OpenXmlPart.ContentType.Contains("threadedcomment", StringComparison.OrdinalIgnoreCase))))
        {
            warnings.Add("检测到现代批注；首版不比较现代批注内容。");
        }

        return warnings;
    }

    private static bool WorkbookContainsFormula(
        IReadOnlyList<SheetEntry> sheets,
        CancellationToken cancellationToken)
    {
        foreach (var sheet in sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = sheet.Part.GetStream(FileMode.Open, FileAccess.Read);
            using var reader = XmlReader.Create(stream, XmlSettings);
            var scanned = 0;
            while (reader.Read())
            {
                if ((++scanned & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "f")
                {
                    return true;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private static bool IsTrue(string? value) => value is "1" or "true" or "True";

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = false
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _document.Dispose();
        _stream.Dispose();
        _disposed = true;
    }

    private readonly record struct CellValue(
        CellValueKind Kind,
        string? Raw,
        string? Normalized,
        string Display,
        string? RichTextFingerprint = null);
    private readonly record struct RichTextValue(string Text, string? Fingerprint);
    private readonly record struct RichTextSegment(int Length, string Formatting);
    private sealed record SharedFormula(string AnchorReference, string Formula, string? Range);
}

internal sealed record SheetEntry(string Name, int Index, string Visibility, WorksheetPart Part);
internal sealed record CellComment(string Text, string Author);
internal sealed record HyperlinkEntry(
    string Reference,
    string? Target,
    string? Location,
    string? Display,
    string? Tooltip)
{
    public string AnchorReference => Reference.Split(':', 2)[0];
    public string? DisplayTarget => Target is null
        ? Location
        : Target + (Location is null ? string.Empty : "#" + Location);
    public string Detail =>
        $"范围={Reference}\n目标={ValueOrMissing(Target)}\n位置={ValueOrMissing(Location)}" +
        $"\n显示={ValueOrMissing(Display)}\n提示={ValueOrMissing(Tooltip)}";

    private static string ValueOrMissing(string? value) => value is null ? "<无>" : value;
}

internal sealed record WorksheetLayout(
    IReadOnlyList<string> MergedRanges,
    IReadOnlySet<uint> HiddenRows,
    IReadOnlySet<uint> ExplicitEmptyRows,
    IReadOnlyList<ColumnRange> HiddenColumns,
    IReadOnlyList<HyperlinkEntry> Hyperlinks,
    IReadOnlyDictionary<string, CellComment> Comments,
    IReadOnlyList<string> UnhandledObjects)
{
    public static WorksheetLayout Empty { get; } = new(
        [],
        new HashSet<uint>(),
        new HashSet<uint>(),
        [],
        [],
        new Dictionary<string, CellComment>(StringComparer.OrdinalIgnoreCase),
        []);

    public string? FindHyperlink(string cellReference)
    {
        return FindHyperlinkEntry(cellReference)?.DisplayTarget;
    }

    public string? FindHyperlinkFingerprint(string cellReference)
    {
        return FindHyperlinkEntry(cellReference)?.Detail;
    }

    private HyperlinkEntry? FindHyperlinkEntry(string cellReference)
    {
        foreach (var hyperlink in Hyperlinks)
        {
            if (CellReferenceUtility.RangeContains(hyperlink.Reference, cellReference))
            {
                return hyperlink;
            }
        }
        return null;
    }
}

internal readonly record struct ColumnRange(uint Minimum, uint Maximum)
{
    public bool Contains(uint column) => column >= Minimum && column <= Maximum;
    public string DisplayRange =>
        CellReferenceUtility.ToColumnName((int)Minimum) + ":" + CellReferenceUtility.ToColumnName((int)Maximum);
}

internal static class FormulaTranslator
{
    private const int MaximumColumn = 16_384;
    private const int MaximumRow = 1_048_576;

    public static string Translate(string formula, string sourceReference, string targetReference)
    {
        if (!CellReferenceUtility.TryParse(sourceReference, out var sourceColumn, out var sourceRow) ||
            !CellReferenceUtility.TryParse(targetReference, out var targetColumn, out var targetRow))
        {
            return formula;
        }

        var columnOffset = targetColumn - sourceColumn;
        var rowOffset = targetRow - sourceRow;
        var result = new StringBuilder(formula.Length + 8);
        var index = 0;
        while (index < formula.Length)
        {
            if (formula[index] == '"')
            {
                var end = FindStringEnd(formula, index);
                result.Append(formula, index, end - index);
                index = end;
                continue;
            }

            if (formula[index] == '[')
            {
                var end = FindBracketEnd(formula, index);
                result.Append(formula, index, end - index);
                index = end;
                continue;
            }

            if (TryConsumeSheetQualifier(formula, index, out var qualifierEnd))
            {
                result.Append(formula, index, qualifierEnd - index);
                index = qualifierEnd;
                continue;
            }

            if (TryTranslateWholeColumnRange(formula, index, columnOffset, out var translatedColumns, out var columnEnd) ||
                TryTranslateWholeRowRange(formula, index, rowOffset, out translatedColumns, out columnEnd))
            {
                result.Append(translatedColumns);
                index = columnEnd;
                continue;
            }

            if (TryTranslateCellReference(formula, index, columnOffset, rowOffset, out var translatedCell, out var cellEnd))
            {
                result.Append(translatedCell);
                index = cellEnd;
                continue;
            }

            result.Append(formula[index]);
            index++;
        }

        return result.ToString();
    }

    private static bool TryConsumeSheetQualifier(string formula, int start, out int end)
    {
        end = start;
        var index = start;
        var bracketDepth = 0;
        var inQuotedSheetName = false;
        var sawQualifierCharacter = false;
        while (index < formula.Length)
        {
            var character = formula[index];
            if (inQuotedSheetName)
            {
                if (character == '\'' && index + 1 < formula.Length && formula[index + 1] == '\'')
                {
                    index += 2;
                    continue;
                }
                if (character == '\'')
                {
                    inQuotedSheetName = false;
                }
                index++;
                continue;
            }

            if (character == '\'' && index == start)
            {
                inQuotedSheetName = true;
                sawQualifierCharacter = true;
                index++;
                continue;
            }

            if (character == '[')
            {
                bracketDepth++;
                sawQualifierCharacter = true;
                index++;
                continue;
            }
            if (character == ']' && bracketDepth > 0)
            {
                bracketDepth--;
                index++;
                continue;
            }
            if (bracketDepth > 0)
            {
                index++;
                continue;
            }

            if (character == '!' && sawQualifierCharacter)
            {
                end = index + 1;
                return true;
            }
            if (char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '\\' or ':')
            {
                sawQualifierCharacter = true;
                index++;
                continue;
            }
            break;
        }
        return false;
    }

    private static bool TryTranslateCellReference(
        string formula,
        int start,
        int columnOffset,
        int rowOffset,
        out string translated,
        out int end)
    {
        translated = string.Empty;
        end = start;
        if (!IsReferenceBoundaryBefore(formula, start))
        {
            return false;
        }

        var index = start;
        var columnAbsolute = ConsumeDollar(formula, ref index);
        var columnStart = index;
        while (index < formula.Length && char.IsAsciiLetter(formula[index]))
        {
            index++;
        }
        if (index == columnStart || index - columnStart > 3)
        {
            return false;
        }

        var rowAbsolute = ConsumeDollar(formula, ref index);
        var rowStart = index;
        while (index < formula.Length && char.IsAsciiDigit(formula[index]))
        {
            index++;
        }
        if (index == rowStart || !IsReferenceBoundaryAfter(formula, index) ||
            (index < formula.Length && formula[index] == '('))
        {
            return false;
        }

        var column = CellReferenceUtility.FromColumnName(formula[columnStart..(rowAbsolute ? rowStart - 1 : rowStart)]);
        if (column is <= 0 or > MaximumColumn ||
            !int.TryParse(formula.AsSpan(rowStart, index - rowStart), NumberStyles.None, CultureInfo.InvariantCulture, out var row) ||
            row is <= 0 or > MaximumRow)
        {
            return false;
        }

        if (!columnAbsolute)
        {
            column += columnOffset;
        }
        if (!rowAbsolute)
        {
            row += rowOffset;
        }

        translated = column is <= 0 or > MaximumColumn || row is <= 0 or > MaximumRow
            ? "#REF!"
            : (columnAbsolute ? "$" : string.Empty) +
                CellReferenceUtility.ToColumnName(column) +
                (rowAbsolute ? "$" : string.Empty) +
                row.ToString(CultureInfo.InvariantCulture);
        end = index;
        return true;
    }

    private static bool TryTranslateWholeColumnRange(
        string formula,
        int start,
        int columnOffset,
        out string translated,
        out int end)
    {
        translated = string.Empty;
        end = start;
        if (!IsReferenceBoundaryBefore(formula, start) ||
            !TryReadColumn(formula, start, out var firstColumn, out var firstAbsolute, out var index) ||
            index >= formula.Length || formula[index] != ':' ||
            !TryReadColumn(formula, index + 1, out var lastColumn, out var lastAbsolute, out end) ||
            !IsReferenceBoundaryAfter(formula, end))
        {
            return false;
        }

        if (!firstAbsolute)
        {
            firstColumn += columnOffset;
        }
        if (!lastAbsolute)
        {
            lastColumn += columnOffset;
        }
        if (firstColumn is <= 0 or > MaximumColumn || lastColumn is <= 0 or > MaximumColumn)
        {
            translated = "#REF!";
            return true;
        }

        translated = (firstAbsolute ? "$" : string.Empty) + CellReferenceUtility.ToColumnName(firstColumn) + ":" +
            (lastAbsolute ? "$" : string.Empty) + CellReferenceUtility.ToColumnName(lastColumn);
        return true;
    }

    private static bool TryTranslateWholeRowRange(
        string formula,
        int start,
        int rowOffset,
        out string translated,
        out int end)
    {
        translated = string.Empty;
        end = start;
        if (!IsReferenceBoundaryBefore(formula, start) ||
            !TryReadRow(formula, start, out var firstRow, out var firstAbsolute, out var index) ||
            index >= formula.Length || formula[index] != ':' ||
            !TryReadRow(formula, index + 1, out var lastRow, out var lastAbsolute, out end) ||
            !IsReferenceBoundaryAfter(formula, end))
        {
            return false;
        }

        if (!firstAbsolute)
        {
            firstRow += rowOffset;
        }
        if (!lastAbsolute)
        {
            lastRow += rowOffset;
        }
        if (firstRow is <= 0 or > MaximumRow || lastRow is <= 0 or > MaximumRow)
        {
            translated = "#REF!";
            return true;
        }

        translated = (firstAbsolute ? "$" : string.Empty) + firstRow.ToString(CultureInfo.InvariantCulture) + ":" +
            (lastAbsolute ? "$" : string.Empty) + lastRow.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryReadColumn(
        string formula,
        int start,
        out int column,
        out bool absolute,
        out int end)
    {
        var index = start;
        absolute = ConsumeDollar(formula, ref index);
        var columnStart = index;
        while (index < formula.Length && char.IsAsciiLetter(formula[index]) && index - columnStart < 3)
        {
            index++;
        }
        if (index == columnStart || (index < formula.Length && char.IsAsciiLetter(formula[index])))
        {
            column = 0;
            end = start;
            return false;
        }

        column = CellReferenceUtility.FromColumnName(formula[columnStart..index]);
        end = index;
        return column <= MaximumColumn;
    }

    private static bool TryReadRow(
        string formula,
        int start,
        out int row,
        out bool absolute,
        out int end)
    {
        var index = start;
        row = 0;
        absolute = ConsumeDollar(formula, ref index);
        var rowStart = index;
        while (index < formula.Length && char.IsAsciiDigit(formula[index]))
        {
            index++;
        }
        end = index;
        return index > rowStart &&
            int.TryParse(formula.AsSpan(rowStart, index - rowStart), NumberStyles.None, CultureInfo.InvariantCulture, out row) &&
            row is > 0 and <= MaximumRow;
    }

    private static bool ConsumeDollar(string formula, ref int index)
    {
        if (index >= formula.Length || formula[index] != '$')
        {
            return false;
        }
        index++;
        return true;
    }

    private static bool IsReferenceBoundaryBefore(string formula, int index) =>
        index == 0 || !IsIdentifierCharacter(formula[index - 1]);

    private static bool IsReferenceBoundaryAfter(string formula, int index) =>
        index >= formula.Length || !IsIdentifierCharacter(formula[index]);

    private static bool IsIdentifierCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '\\';

    private static int FindStringEnd(string formula, int start)
    {
        var index = start + 1;
        while (index < formula.Length)
        {
            if (formula[index] != '"')
            {
                index++;
                continue;
            }

            if (index + 1 < formula.Length && formula[index + 1] == '"')
            {
                index += 2;
                continue;
            }
            return index + 1;
        }
        return formula.Length;
    }

    private static int FindBracketEnd(string formula, int start)
    {
        var depth = 1;
        var index = start + 1;
        while (index < formula.Length && depth > 0)
        {
            depth += formula[index] switch
            {
                '[' => 1,
                ']' => -1,
                _ => 0
            };
            index++;
        }
        return index;
    }

}

internal static class CellReferenceUtility
{
    public static bool TryParse(string reference, out int column, out int row)
    {
        column = 0;
        row = 0;
        var index = 0;
        while (index < reference.Length && reference[index] == '$')
        {
            index++;
        }
        var start = index;
        while (index < reference.Length && char.IsAsciiLetter(reference[index]))
        {
            column = checked((column * 26) + (char.ToUpperInvariant(reference[index]) - 'A' + 1));
            index++;
        }
        if (index == start)
        {
            return false;
        }
        if (index < reference.Length && reference[index] == '$')
        {
            index++;
        }
        return int.TryParse(reference.AsSpan(index), NumberStyles.None, CultureInfo.InvariantCulture, out row) && row > 0;
    }

    public static int FromColumnName(string name)
    {
        var column = 0;
        foreach (var character in name)
        {
            column = checked((column * 26) + (char.ToUpperInvariant(character) - 'A' + 1));
        }
        return column;
    }

    public static string ToColumnName(int column)
    {
        if (column <= 0)
        {
            return string.Empty;
        }
        var builder = new StringBuilder();
        while (column > 0)
        {
            column--;
            builder.Insert(0, (char)('A' + (column % 26)));
            column /= 26;
        }
        return builder.ToString();
    }

    public static string NormalizeRange(string reference)
    {
        reference = reference.Trim();
        var separator = reference.IndexOf(':');
        if (separator < 0)
        {
            return TryParse(reference, out var column, out var row)
                ? ToColumnName(column) + row.ToString(CultureInfo.InvariantCulture)
                : reference.ToUpperInvariant();
        }

        if (!TryParse(reference[..separator], out var firstColumn, out var firstRow) ||
            !TryParse(reference[(separator + 1)..], out var lastColumn, out var lastRow))
        {
            return reference.Replace("$", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        }

        var minimumColumn = Math.Min(firstColumn, lastColumn);
        var maximumColumn = Math.Max(firstColumn, lastColumn);
        var minimumRow = Math.Min(firstRow, lastRow);
        var maximumRow = Math.Max(firstRow, lastRow);
        var first = ToColumnName(minimumColumn) + minimumRow.ToString(CultureInfo.InvariantCulture);
        var last = ToColumnName(maximumColumn) + maximumRow.ToString(CultureInfo.InvariantCulture);
        return string.Equals(first, last, StringComparison.Ordinal) ? first : first + ":" + last;
    }

    public static bool RangeContains(string range, string cellReference)
    {
        var separator = range.IndexOf(':');
        if (separator < 0)
        {
            return string.Equals(range, cellReference, StringComparison.OrdinalIgnoreCase);
        }
        if (!TryParse(range[..separator], out var minColumn, out var minRow) ||
            !TryParse(range[(separator + 1)..], out var maxColumn, out var maxRow) ||
            !TryParse(cellReference, out var column, out var row))
        {
            return false;
        }
        return column >= minColumn && column <= maxColumn && row >= minRow && row <= maxRow;
    }

    public static int Compare(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (TryParse(left, out var leftColumn, out var leftRow) &&
            TryParse(right, out var rightColumn, out var rightRow))
        {
            var rowComparison = leftRow.CompareTo(rightRow);
            return rowComparison != 0 ? rowComparison : leftColumn.CompareTo(rightColumn);
        }
        return StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }
}

internal sealed class CellReferenceComparer : IComparer<string>
{
    public static CellReferenceComparer Instance { get; } = new();
    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }
        if (left is null)
        {
            return -1;
        }
        if (right is null)
        {
            return 1;
        }
        return CellReferenceUtility.Compare(left, right);
    }
}
