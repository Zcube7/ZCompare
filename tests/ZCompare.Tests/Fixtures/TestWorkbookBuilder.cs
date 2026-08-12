using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace ZCompare.Tests.Fixtures;

public sealed class TestWorkbookBuilder
{
    private readonly List<TestSheet> _sheets = [];
    private readonly List<string> _chartSheets = [];
    private readonly Dictionary<string, string> _extraEntries = new(StringComparer.Ordinal);
    private bool _date1904;
    private string? _calculationMode;
    private bool _fullCalculationOnLoad;
    private bool _includeSystemTheme;
    private bool _useStrictWorkbookNamespace;
    private string? _stylesXml;

    public TestWorkbookBuilder WithDate1904(bool value = true)
    {
        _date1904 = value;
        return this;
    }

    public TestWorkbookBuilder WithCalculation(
        string mode = "manual",
        bool fullCalculationOnLoad = false)
    {
        _calculationMode = mode;
        _fullCalculationOnLoad = fullCalculationOnLoad;
        return this;
    }

    public TestWorkbookBuilder WithSystemTheme()
    {
        _includeSystemTheme = true;
        return this;
    }

    public TestWorkbookBuilder WithStrictWorkbookNamespace()
    {
        _useStrictWorkbookNamespace = true;
        return this;
    }

    public TestWorkbookBuilder WithStylesXml(string stylesXml)
    {
        _stylesXml = stylesXml;
        return this;
    }

    public TestWorkbookBuilder AddSheet(
        string name,
        Action<TestSheet>? configure = null,
        string state = "visible")
    {
        var sheet = new TestSheet(name, state);
        configure?.Invoke(sheet);
        _sheets.Add(sheet);
        return this;
    }

    public TestWorkbookBuilder AddChartSheet(string name)
    {
        _chartSheets.Add(name);
        return this;
    }

    public TestWorkbookBuilder AddUnreferencedPart(string entryName, string content)
    {
        _extraEntries.Add(entryName.Replace('\\', '/'), content);
        return this;
    }

    public string Save(string path)
    {
        if (_sheets.Count == 0)
        {
            AddSheet("Sheet1");
        }

        var parent = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);

        WriteEntry(archive, "[Content_Types].xml", BuildContentTypes());
        WriteEntry(archive, "_rels/.rels", PackageRelationships);
        WriteEntry(archive, "xl/workbook.xml", BuildWorkbook());
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelationships());
        WriteEntry(archive, "xl/styles.xml", _stylesXml ?? Styles);
        if (_includeSystemTheme)
        {
            WriteEntry(archive, "xl/theme/theme1.xml", SystemTheme);
        }

        for (var index = 0; index < _sheets.Count; index++)
        {
            var sheet = _sheets[index];
            sheet.IndexHint = index + 1;
            WriteEntry(archive, $"xl/worksheets/sheet{index + 1}.xml", BuildWorksheet(sheet));

            if (sheet.HasRelationships)
            {
                WriteEntry(
                    archive,
                    $"xl/worksheets/_rels/sheet{index + 1}.xml.rels",
                    BuildWorksheetRelationships(sheet));
            }

            if (sheet.Comments.Count > 0)
            {
                WriteEntry(archive, $"xl/comments{index + 1}.xml", BuildComments(sheet));
            }
            if (sheet.ThreadedComments.Count > 0)
            {
                WriteEntry(
                    archive,
                    $"xl/threadedComments/threadedComment{index + 1}.xml",
                    BuildThreadedComments(sheet));
            }
        }

        for (var index = 0; index < _chartSheets.Count; index++)
        {
            WriteEntry(archive, $"xl/chartsheets/sheet{index + 1}.xml", BuildChartSheet());
        }

        foreach (var pair in _extraEntries)
        {
            WriteEntry(archive, pair.Key, pair.Value);
        }

        return path;
    }

    private string BuildContentTypes()
    {
        var overrides = new StringBuilder();
        overrides.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        overrides.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        if (_includeSystemTheme)
        {
            overrides.Append("<Override PartName=\"/xl/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>");
        }

        for (var index = 0; index < _sheets.Count; index++)
        {
            overrides.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"/xl/worksheets/sheet{index + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            if (_sheets[index].Comments.Count > 0)
            {
                overrides.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"/xl/comments{index + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.comments+xml\"/>");
            }
            if (_sheets[index].ThreadedComments.Count > 0)
            {
                overrides.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"/xl/threadedComments/threadedComment{index + 1}.xml\" ContentType=\"application/vnd.ms-excel.threadedcomments+xml\"/>");
            }
        }
        for (var index = 0; index < _chartSheets.Count; index++)
        {
            overrides.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"/xl/chartsheets/sheet{index + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.chartsheet+xml\"/>");
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              {(_extraEntries.Keys.Any(static name => name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) ? "<Default Extension=\"txt\" ContentType=\"text/plain\"/>" : string.Empty)}
              {overrides}
            </Types>
            """;
    }

    private string BuildWorkbook()
    {
        var properties = _date1904 ? "<workbookPr date1904=\"1\"/>" : string.Empty;
        var sheets = new StringBuilder();
        for (var index = 0; index < _sheets.Count; index++)
        {
            var sheet = _sheets[index];
            var state = sheet.State == "visible" ? string.Empty : $" state=\"{Xml(sheet.State)}\"";
            sheets.Append(CultureInfo.InvariantCulture, $"<sheet name=\"{Xml(sheet.Name)}\" sheetId=\"{index + 1}\"{state} r:id=\"rId{index + 1}\"/>");
        }
        for (var index = 0; index < _chartSheets.Count; index++)
        {
            var sheetIndex = _sheets.Count + index + 1;
            sheets.Append(CultureInfo.InvariantCulture, $"<sheet name=\"{Xml(_chartSheets[index])}\" sheetId=\"{sheetIndex}\" r:id=\"rId{sheetIndex}\"/>");
        }

        var calculation = _calculationMode is null
            ? string.Empty
            : $"<calcPr calcMode=\"{Xml(_calculationMode)}\" fullCalcOnLoad=\"{(_fullCalculationOnLoad ? 1 : 0)}\" forceFullCalc=\"{(_fullCalculationOnLoad ? 1 : 0)}\"/>";

        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="{(_useStrictWorkbookNamespace ? StrictSpreadsheetNamespace : TransitionalSpreadsheetNamespace)}"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              {properties}<sheets>{sheets}</sheets>{calculation}
            </workbook>
            """;
    }

    private string BuildWorkbookRelationships()
    {
        var relationships = new StringBuilder();
        for (var index = 0; index < _sheets.Count; index++)
        {
            relationships.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{index + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{index + 1}.xml\"/>");
        }
        for (var index = 0; index < _chartSheets.Count; index++)
        {
            var relationshipIndex = _sheets.Count + index + 1;
            relationships.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{relationshipIndex}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/chartsheet\" Target=\"chartsheets/sheet{index + 1}.xml\"/>");
        }

        var styleRelationshipIndex = _sheets.Count + _chartSheets.Count + 1;
        relationships.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{styleRelationshipIndex}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        if (_includeSystemTheme)
        {
            relationships.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rId{styleRelationshipIndex + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"theme/theme1.xml\"/>");
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              {relationships}
            </Relationships>
            """;
    }

    private static string BuildWorksheet(TestSheet sheet)
    {
        var columns = new StringBuilder();
        foreach (var column in sheet.HiddenColumns.OrderBy(static range => range.Minimum).ThenBy(static range => range.Maximum))
        {
            columns.Append(CultureInfo.InvariantCulture, $"<col min=\"{column.Minimum}\" max=\"{column.Maximum}\" hidden=\"1\" width=\"8.43\" customWidth=\"1\"/>");
        }

        var rows = new StringBuilder();
        foreach (var rowGroup in sheet.Cells.GroupBy(cell => RowOf(cell.Address)).OrderBy(group => group.Key))
        {
            var hidden = sheet.HiddenRows.Contains(rowGroup.Key) ? " hidden=\"1\"" : string.Empty;
            rows.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowGroup.Key}\"{hidden}>");
            foreach (var cell in rowGroup.OrderBy(cell => ColumnOf(cell.Address)))
            {
                rows.Append(BuildCell(cell));
            }

            rows.Append("</row>");
        }

        foreach (var emptyRow in sheet.HiddenRows
            .Union(sheet.ExplicitEmptyRows)
            .Except(sheet.Cells.Select(cell => RowOf(cell.Address)))
            .Order())
        {
            var hidden = sheet.HiddenRows.Contains(emptyRow) ? " hidden=\"1\"" : string.Empty;
            rows.Append(CultureInfo.InvariantCulture, $"<row r=\"{emptyRow}\"{hidden}/>");
        }

        var merges = sheet.Merges.Count == 0
            ? string.Empty
            : $"<mergeCells count=\"{sheet.Merges.Count}\">{string.Concat(sheet.Merges.Select(range => $"<mergeCell ref=\"{Xml(range)}\"/>"))}</mergeCells>";

        var hyperlinks = new StringBuilder();
        var relationshipIndex = 1;
        foreach (var link in sheet.Hyperlinks)
        {
            var display = link.Display is null ? string.Empty : $" display=\"{Xml(link.Display)}\"";
            var tooltip = link.Tooltip is null ? string.Empty : $" tooltip=\"{Xml(link.Tooltip)}\"";
            hyperlinks.Append(CultureInfo.InvariantCulture, $"<hyperlink ref=\"{Xml(link.Reference)}\" r:id=\"rIdLink{relationshipIndex++}\"{display}{tooltip}/>");
        }

        var hyperlinkXml = hyperlinks.Length == 0 ? string.Empty : $"<hyperlinks>{hyperlinks}</hyperlinks>";
        var conditionalFormattingXml = sheet.HasConditionalFormatting
            ? "<conditionalFormatting sqref=\"A1\"><cfRule type=\"expression\" priority=\"1\"><formula>1=1</formula></cfRule></conditionalFormatting>"
            : string.Empty;
        var dataValidationXml = sheet.HasDataValidation
            ? "<dataValidations count=\"1\"><dataValidation type=\"whole\" sqref=\"A1\"/></dataValidations>"
            : string.Empty;

        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                       xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              {(columns.Length == 0 ? string.Empty : $"<cols>{columns}</cols>")}
              <sheetData>{rows}</sheetData>
              {merges}{conditionalFormattingXml}{dataValidationXml}{hyperlinkXml}
            </worksheet>
            """;
    }

    private static string BuildChartSheet() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <chartsheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <sheetViews><sheetView workbookViewId="0"/></sheetViews>
        </chartsheet>
        """;

    private static string BuildCell(TestCell cell)
    {
        var type = cell.Type switch
        {
            TestCellType.Number => string.Empty,
            TestCellType.Boolean => " t=\"b\"",
            TestCellType.Error => " t=\"e\"",
            TestCellType.InlineString => " t=\"inlineStr\"",
            TestCellType.FormulaString => " t=\"str\"",
            _ => throw new ArgumentOutOfRangeException(nameof(cell.Type)),
        };
        var style = cell.StyleIndex == 0 ? string.Empty : $" s=\"{cell.StyleIndex}\"";

        string content;
        if (cell.Type == TestCellType.InlineString)
        {
            var runs = cell.RichTextRuns is null
                ? $"<t xml:space=\"preserve\">{Xml(cell.Value)}</t>"
                : string.Concat(cell.RichTextRuns.Select((run, index) =>
                    $"<r>{(index == 0 && cell.BoldFirstRichTextRun ? "<rPr><b/></rPr>" : string.Empty)}<t xml:space=\"preserve\">{Xml(run)}</t></r>"));
            var phoneticText = cell.PhoneticText is null
                ? string.Empty
                : $"<rPh sb=\"0\" eb=\"1\"><t>{Xml(cell.PhoneticText)}</t></rPh>";
            content = $"<is>{runs}{phoneticText}</is>";
        }
        else
        {
            var formula = cell.Formula is null ? string.Empty : BuildFormula(cell);
            var value = cell.Value is null ? string.Empty : $"<v>{Xml(cell.Value)}</v>";
            content = formula + value;
        }

        return $"<c r=\"{Xml(cell.Address)}\"{type}{style}>{content}</c>";
    }

    private static string BuildFormula(TestCell cell)
    {
        var attributes = new StringBuilder();
        if (cell.FormulaKind is not null)
        {
            attributes.Append(CultureInfo.InvariantCulture, $" t=\"{Xml(cell.FormulaKind)}\"");
        }

        if (cell.FormulaReference is not null)
        {
            attributes.Append(CultureInfo.InvariantCulture, $" ref=\"{Xml(cell.FormulaReference)}\"");
        }

        if (cell.SharedIndex is not null)
        {
            attributes.Append(CultureInfo.InvariantCulture, $" si=\"{cell.SharedIndex.Value}\"");
        }

        return $"<f{attributes}>{Xml(cell.Formula)}</f>";
    }

    private static string BuildWorksheetRelationships(TestSheet sheet)
    {
        var relationships = new StringBuilder();
        var relationshipIndex = 1;
        foreach (var link in sheet.Hyperlinks)
        {
            relationships.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rIdLink{relationshipIndex++}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"{Xml(link.Target)}\" TargetMode=\"External\"/>");
        }

        if (sheet.Comments.Count > 0)
        {
            relationships.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rIdComments\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments\" Target=\"../comments{sheet.IndexHint}.xml\"/>");
        }
        if (sheet.ThreadedComments.Count > 0)
        {
            relationships.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"rIdThreadedComments\" Type=\"http://schemas.microsoft.com/office/2017/10/relationships/threadedComment\" Target=\"../threadedComments/threadedComment{sheet.IndexHint}.xml\"/>");
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              {relationships}
            </Relationships>
            """;
    }

    private static string BuildComments(TestSheet sheet)
    {
        var authors = sheet.Comments.Select(comment => comment.Author).Distinct(StringComparer.Ordinal).ToArray();
        var authorXml = string.Concat(authors.Select(author => $"<author>{Xml(author)}</author>"));
        var commentXml = string.Concat(sheet.Comments.Select(comment =>
        {
            var authorId = Array.IndexOf(authors, comment.Author);
            return $"<comment ref=\"{Xml(comment.Address)}\" authorId=\"{authorId}\"><text><t xml:space=\"preserve\">{Xml(comment.Text)}</t></text></comment>";
        }));

        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <comments xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <authors>{authorXml}</authors><commentList>{commentXml}</commentList>
            </comments>
            """;
    }

    private static string BuildThreadedComments(TestSheet sheet)
    {
        var comments = string.Concat(sheet.ThreadedComments.Select((comment, index) =>
            $"<threadedComment ref=\"{Xml(comment.Address)}\" dT=\"2026-01-01T00:00:00Z\" personId=\"{{11111111-1111-1111-1111-111111111111}}\" id=\"{{22222222-2222-2222-2222-{index + 1:000000000000}}}\"><text>{Xml(comment.Text)}</text></threadedComment>"));
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <ThreadedComments xmlns="http://schemas.microsoft.com/office/spreadsheetml/2018/threadedcomments">
              {comments}
            </ThreadedComments>
            """;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static int RowOf(string address)
    {
        var digits = new string(address.Where(char.IsDigit).ToArray());
        return int.Parse(digits, CultureInfo.InvariantCulture);
    }

    private static int ColumnOf(string address)
    {
        var column = 0;
        foreach (var character in address.TakeWhile(char.IsLetter))
        {
            column = (column * 26) + char.ToUpperInvariant(character) - 'A' + 1;
        }

        return column;
    }

    private static string Xml(string? value) => SecurityElement.Escape(value) ?? string.Empty;

    private const string PackageRelationships = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """;

    private const string Styles = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="4">
            <numFmt numFmtId="164" formatCode="yyyy-mm-dd"/>
            <numFmt numFmtId="165" formatCode="0.00"/>
            <numFmt numFmtId="166" formatCode="h:mm"/>
            <numFmt numFmtId="167" formatCode="[h]:mm:ss"/>
          </numFmts>
          <fonts count="3">
            <font><sz val="11"/><color theme="1"/><name val="Calibri"/></font>
            <font><b/><sz val="12"/><color rgb="FFFF0000"/><name val="Calibri"/></font>
            <font><sz val="11"/><color theme="0" tint="-0.25"/><name val="Calibri"/></font>
          </fonts>
          <fills count="3">
            <fill><patternFill patternType="none"/></fill>
            <fill><patternFill patternType="gray125"/></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFFFFF00"/><bgColor indexed="64"/></patternFill></fill>
          </fills>
          <borders count="2">
            <border><left/><right/><top/><bottom/><diagonal/></border>
            <border><left style="thin"><color rgb="FF000000"/></left><right style="thin"><color rgb="FF000000"/></right><top style="thin"><color rgb="FF000000"/></top><bottom style="thin"><color rgb="FF000000"/></bottom><diagonal/></border>
          </borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="7">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyFont="1"/>
            <xf numFmtId="166" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="167" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;

    private const string TransitionalSpreadsheetNamespace =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private const string StrictSpreadsheetNamespace =
        "http://purl.oclc.org/ooxml/spreadsheetml/main";

    private const string SystemTheme = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="ZCompare Test Theme">
          <a:themeElements>
            <a:clrScheme name="ZCompare Test Colors">
              <a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1>
              <a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1>
              <a:dk2><a:srgbClr val="1F497D"/></a:dk2>
              <a:lt2><a:srgbClr val="EEECE1"/></a:lt2>
              <a:accent1><a:srgbClr val="4F81BD"/></a:accent1>
              <a:accent2><a:srgbClr val="C0504D"/></a:accent2>
              <a:accent3><a:srgbClr val="9BBB59"/></a:accent3>
              <a:accent4><a:srgbClr val="8064A2"/></a:accent4>
              <a:accent5><a:srgbClr val="4BACC6"/></a:accent5>
              <a:accent6><a:srgbClr val="F79646"/></a:accent6>
              <a:hlink><a:srgbClr val="0000FF"/></a:hlink>
              <a:folHlink><a:srgbClr val="800080"/></a:folHlink>
            </a:clrScheme>
            <a:fontScheme name="ZCompare Test Fonts"><a:majorFont/><a:minorFont/></a:fontScheme>
            <a:fmtScheme name="ZCompare Test Formats">
              <a:fillStyleLst/><a:lnStyleLst/><a:effectStyleLst/><a:bgFillStyleLst/>
            </a:fmtScheme>
          </a:themeElements>
        </a:theme>
        """;
}

public sealed class TestSheet(string name, string state)
{
    public string Name { get; } = name;
    public string State { get; } = state;
    internal int IndexHint { get; set; }
    internal List<TestCell> Cells { get; } = [];
    internal HashSet<int> HiddenRows { get; } = [];
    internal HashSet<int> ExplicitEmptyRows { get; } = [];
    internal List<TestColumnRange> HiddenColumns { get; } = [];
    internal List<string> Merges { get; } = [];
    internal List<TestHyperlink> Hyperlinks { get; } = [];
    internal List<TestComment> Comments { get; } = [];
    internal List<TestThreadedComment> ThreadedComments { get; } = [];
    internal bool HasConditionalFormatting { get; private set; }
    internal bool HasDataValidation { get; private set; }
    internal bool HasRelationships => Hyperlinks.Count > 0 || Comments.Count > 0 || ThreadedComments.Count > 0;

    public TestSheet Cell(
        string address,
        string? value,
        TestCellType type = TestCellType.Number,
        uint styleIndex = 0,
        string? formula = null,
        string? formulaKind = null,
        string? formulaReference = null,
        uint? sharedIndex = null,
        IReadOnlyList<string>? richTextRuns = null,
        bool boldFirstRichTextRun = true,
        string? phoneticText = null)
    {
        Cells.Add(new TestCell(
            address,
            value,
            type,
            styleIndex,
            formula,
            formulaKind,
            formulaReference,
            sharedIndex,
            richTextRuns,
            boldFirstRichTextRun,
            phoneticText));
        return this;
    }

    public TestSheet HideRow(int row)
    {
        HiddenRows.Add(row);
        return this;
    }

    public TestSheet EmptyRow(int row)
    {
        ExplicitEmptyRows.Add(row);
        return this;
    }

    public TestSheet HideColumn(int column)
    {
        HiddenColumns.Add(new TestColumnRange(column, column));
        return this;
    }

    public TestSheet HideColumns(int minimum, int maximum)
    {
        HiddenColumns.Add(new TestColumnRange(minimum, maximum));
        return this;
    }

    public TestSheet Merge(string reference)
    {
        Merges.Add(reference);
        return this;
    }

    public TestSheet Hyperlink(
        string address,
        string uri,
        string? display = null,
        string? tooltip = null)
    {
        Hyperlinks.Add(new TestHyperlink(address, uri, display, tooltip));
        return this;
    }

    public TestSheet Comment(string address, string text, string author = "tester")
    {
        Comments.Add(new TestComment(address, text, author));
        return this;
    }

    public TestSheet ThreadedComment(string address, string text)
    {
        ThreadedComments.Add(new TestThreadedComment(address, text));
        return this;
    }

    public TestSheet ConditionalFormatting()
    {
        HasConditionalFormatting = true;
        return this;
    }

    public TestSheet DataValidation()
    {
        HasDataValidation = true;
        return this;
    }
}

internal sealed record TestCell(
    string Address,
    string? Value,
    TestCellType Type,
    uint StyleIndex,
    string? Formula,
    string? FormulaKind,
    string? FormulaReference,
    uint? SharedIndex,
    IReadOnlyList<string>? RichTextRuns,
    bool BoldFirstRichTextRun,
    string? PhoneticText);

internal sealed record TestComment(string Address, string Text, string Author);
internal sealed record TestThreadedComment(string Address, string Text);
internal sealed record TestColumnRange(int Minimum, int Maximum);
internal sealed record TestHyperlink(string Reference, string Target, string? Display, string? Tooltip);

public enum TestCellType
{
    Number,
    Boolean,
    Error,
    InlineString,
    FormulaString,
}
