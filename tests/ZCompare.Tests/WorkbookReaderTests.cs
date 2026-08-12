using System.Buffers.Binary;
using System.Text;
using ZCompare.Core;
using ZCompare.Tests.Fixtures;

namespace ZCompare.Tests;

public sealed class WorkbookReaderTests : ComparisonTestBase
{
    [Fact]
    public async Task LegacyOleWorkbookWithXlsxExtensionReportsClearFormatError()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = temporaryDirectory.File("legacy.xlsx");
        File.WriteAllBytes(path, CreateOleCompoundFile("Workbook"));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => CreateReader().ReadMetadataAsync(path));

        Assert.Contains("旧版二进制 XLS", exception.Message, StringComparison.Ordinal);
        Assert.Contains("另存为普通 XLSX", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EncryptedOfficeWorkbookReportsPasswordProtectionInsteadOfLegacyXls()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = temporaryDirectory.File("encrypted.xlsx");
        File.WriteAllBytes(path, CreateOleCompoundFile("EncryptedPackage"));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => CreateReader().ReadMetadataAsync(path));

        Assert.Contains("加密或受密码保护", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("旧版二进制 XLS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StrictWorkbookFailsClosedInsteadOfReturningSame()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var left = new TestWorkbookBuilder()
            .WithStrictWorkbookNamespace()
            .AddSheet("Sheet1", sheet => sheet.Cell("A1", "1"))
            .Save(temporaryDirectory.File("left.xlsx"));
        var right = temporaryDirectory.File("right.xlsx");
        File.Copy(left, right);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => CreateComparer().CompareAsync(left, right));

        Assert.Contains("Strict", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolvesSystemThemeLastColorAndTintToArgb()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = new TestWorkbookBuilder()
            .WithSystemTheme()
            .AddSheet("主题", sheet => sheet.Cell("A1", "1", styleIndex: 4))
            .Save(temporaryDirectory.File("theme.xlsx"));

        var preview = await CreateReader().LoadWorksheetPreviewAsync(path, "主题");
        var foreground = Assert.IsType<string>(preview.Cells["A1"].Format?.ForegroundArgb);

        Assert.Matches("^[0-9A-F]{8}$", foreground);
        Assert.Equal("FFBFBFBF", foreground);
    }

    [Fact]
    public async Task ReadsValueKindsRichTextDateSystemAndExactLongId()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = new TestWorkbookBuilder()
            .WithDate1904()
            .AddSheet("值", sheet => sheet
                .Cell("A1", "9007199254740993")
                .Cell("A2", "1", TestCellType.Boolean)
                .Cell("A3", "#DIV/0!", TestCellType.Error)
                .Cell("A4", "unused", TestCellType.InlineString, richTextRuns: ["富", "文本"])
                .Cell("A5", "1", TestCellType.Number, styleIndex: 1))
            .Save(temporaryDirectory.File("reader.xlsx"));

        var reader = CreateReader();
        var metadata = await reader.ReadMetadataAsync(path);
        var preview = await reader.LoadWorksheetPreviewAsync(path, "值");

        Assert.True(metadata.Uses1904DateSystem);
        Assert.Equal("9007199254740993", preview.Cells["A1"].RawValue);
        Assert.Equal(CellValueKind.Number, preview.Cells["A1"].ValueKind);
        Assert.Equal(CellValueKind.Boolean, preview.Cells["A2"].ValueKind);
        Assert.Equal(CellValueKind.Error, preview.Cells["A3"].ValueKind);
        Assert.Equal("富文本", preview.Cells["A4"].RawValue);
        Assert.Equal(CellValueKind.Text, preview.Cells["A4"].ValueKind);
        Assert.Equal(CellValueKind.Date, preview.Cells["A5"].ValueKind);
    }

    [Fact]
    public async Task ReadsBlankCommentAuthorHyperlinkHiddenLayoutAndMerge()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var path = new TestWorkbookBuilder()
            .AddSheet("布局", sheet => sheet
                .Cell("A1", "链接", TestCellType.InlineString)
                .Cell("B2", "批注", TestCellType.InlineString)
                .HideRow(2)
                .HideColumn(1)
                .Merge("B2:C2")
                .Hyperlink("A1", "https://example.test/path")
                .Comment("B2", "没有作者也可读取", author: string.Empty))
            .Save(temporaryDirectory.File("layout.xlsx"));

        var preview = await CreateReader().LoadWorksheetPreviewAsync(path, "布局");

        Assert.Equal("https://example.test/path", preview.Cells["A1"].Hyperlink);
        Assert.Equal("没有作者也可读取", preview.Cells["B2"].Comment);
        Assert.Equal(string.Empty, preview.Cells["B2"].CommentAuthor);
        Assert.True(preview.Cells["B2"].IsRowHidden);
        Assert.True(preview.Cells["A1"].IsColumnHidden);
        Assert.Contains("B2:C2", preview.MergedRanges);
    }

    private static byte[] CreateOleCompoundFile(string streamName)
    {
        const int sectorSize = 512;
        var bytes = new byte[sectorSize * 3];
        byte[] signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        signature.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x18, 2), 0x003E);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x1A, 2), 0x0003);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x1C, 2), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x1E, 2), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x20, 2), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x2C, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x30, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x38, 4), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x3C, 4), 0xFFFFFFFE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x44, 4), 0xFFFFFFFE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x4C, 4), 0);
        for (var offset = 0x50; offset < sectorSize; offset += 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), 0xFFFFFFFF);
        }

        var fatOffset = sectorSize;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fatOffset, 4), 0xFFFFFFFD);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fatOffset + 4, 4), 0xFFFFFFFE);
        for (var offset = fatOffset + 8; offset < fatOffset + sectorSize; offset += 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), 0xFFFFFFFF);
        }

        var directoryOffset = sectorSize * 2;
        WriteDirectoryEntry(bytes, directoryOffset, "Root Entry", type: 5);
        WriteDirectoryEntry(bytes, directoryOffset + 128, streamName, type: 2);
        return bytes;
    }

    private static void WriteDirectoryEntry(byte[] bytes, int offset, string name, byte type)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name + '\0');
        nameBytes.CopyTo(bytes, offset);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 64, 2), (ushort)nameBytes.Length);
        bytes[offset + 66] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 68, 4), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 72, 4), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 76, 4), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 116, 4), 0xFFFFFFFE);
    }
}
