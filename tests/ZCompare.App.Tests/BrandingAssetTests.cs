using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace ZCompare.App.Tests;

public sealed class BrandingAssetTests
{
    private static readonly int[] ExpectedIconSizes = [16, 24, 32, 48, 64, 128, 256];
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    [Fact]
    public void BrandAssetsUseTransparentRgbSourceAndExpectedDimensions()
    {
        var brandingDirectory = Path.Combine(FindRepositoryRoot(), "assets", "branding");
        var svg = File.ReadAllText(Path.Combine(brandingDirectory, "zcompare-icon.svg"));

        Assert.Contains("#2563EB", svg, StringComparison.Ordinal);
        Assert.Contains("#DC2626", svg, StringComparison.Ordinal);
        Assert.Contains("#16A34A", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<rect", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gradient", svg, StringComparison.OrdinalIgnoreCase);

        AssertPng(Path.Combine(brandingDirectory, "zcompare-icon-256.png"), 256);
        AssertPng(Path.Combine(brandingDirectory, "zcompare-icon-1024.png"), 1024);
    }

    [Fact]
    public void WindowsIconContainsAllRequired32BitPngLayers()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "assets", "branding", "zcompare.ico"));

        Assert.Equal((ushort)0, ReadUInt16(bytes, 0));
        Assert.Equal((ushort)1, ReadUInt16(bytes, 2));
        Assert.Equal((ushort)ExpectedIconSizes.Length, ReadUInt16(bytes, 4));

        var actualSizes = new List<int>();
        for (var index = 0; index < ExpectedIconSizes.Length; index++)
        {
            var entryOffset = 6 + (index * 16);
            var width = bytes[entryOffset] == 0 ? 256 : bytes[entryOffset];
            var height = bytes[entryOffset + 1] == 0 ? 256 : bytes[entryOffset + 1];
            var payloadLength = ReadUInt32(bytes, entryOffset + 8);
            var payloadOffset = ReadUInt32(bytes, entryOffset + 12);

            Assert.Equal(width, height);
            Assert.Equal((ushort)1, ReadUInt16(bytes, entryOffset + 4));
            Assert.Equal((ushort)32, ReadUInt16(bytes, entryOffset + 6));
            Assert.True((ulong)payloadOffset + payloadLength <= (ulong)bytes.Length);
            Assert.True(bytes.AsSpan((int)payloadOffset, PngSignature.Length).SequenceEqual(PngSignature));
            actualSizes.Add(width);
        }

        Assert.Equal(ExpectedIconSizes, actualSizes);
    }

    private static void AssertPng(string path, int expectedSize)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature));
        Assert.Equal(expectedSize, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(expectedSize, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
        Assert.Equal(8, bytes[24]);
        Assert.Equal(6, bytes[25]);
    }

    private static ushort ReadUInt16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ZCompare.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("找不到 ZCompare 仓库根目录。");
    }
}
