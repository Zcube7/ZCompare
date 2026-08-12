using System.Buffers.Binary;
using System.Text;

namespace ZCompare.Core;

internal enum Ole2ContainerKind
{
    NotOle,
    LegacyWorkbook,
    EncryptedPackage,
    Unknown,
}

internal static class Ole2ContainerInspector
{
    private const uint FreeSector = 0xFFFFFFFF;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FatSector = 0xFFFFFFFD;
    private const uint DifatSector = 0xFFFFFFFC;
    private static ReadOnlySpan<byte> Signature =>
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public static Ole2ContainerKind Inspect(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            return Ole2ContainerKind.NotOle;
        }

        var originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            Span<byte> signature = stackalloc byte[8];
            if (stream.Read(signature) != signature.Length || !signature.SequenceEqual(Signature))
            {
                return Ole2ContainerKind.NotOle;
            }

            stream.Position = 0;
            var header = new byte[512];
            if (stream.Read(header, 0, header.Length) != header.Length)
            {
                return Ole2ContainerKind.Unknown;
            }

            return InspectCompoundFile(stream, header);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or OverflowException)
        {
            return Ole2ContainerKind.Unknown;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static Ole2ContainerKind InspectCompoundFile(Stream stream, byte[] header)
    {
        if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x1C, 2)) != 0xFFFE)
        {
            return Ole2ContainerKind.Unknown;
        }

        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x1E, 2));
        if (sectorShift is not (9 or 12))
        {
            return Ole2ContainerKind.Unknown;
        }

        var sectorSize = 1 << sectorShift;
        var maximumSectorCount = Math.Max(0L, (stream.Length - 512L) / sectorSize);
        if (maximumSectorCount == 0 || maximumSectorCount > int.MaxValue)
        {
            return Ole2ContainerKind.Unknown;
        }

        var fatSectorIds = ReadFatSectorIds(stream, header, sectorSize, (int)maximumSectorCount);
        if (fatSectorIds.Count == 0)
        {
            return Ole2ContainerKind.Unknown;
        }

        var fat = ReadFat(stream, fatSectorIds, sectorSize, (int)maximumSectorCount);
        var firstDirectorySector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x30, 4));
        var directoryNames = ReadDirectoryNames(
            stream,
            firstDirectorySector,
            fat,
            sectorSize,
            (int)maximumSectorCount);

        if (directoryNames.Contains("EncryptedPackage", StringComparer.OrdinalIgnoreCase) ||
            directoryNames.Contains("EncryptionInfo", StringComparer.OrdinalIgnoreCase))
        {
            return Ole2ContainerKind.EncryptedPackage;
        }

        if (directoryNames.Contains("Workbook", StringComparer.OrdinalIgnoreCase) ||
            directoryNames.Contains("Book", StringComparer.OrdinalIgnoreCase))
        {
            return Ole2ContainerKind.LegacyWorkbook;
        }

        return Ole2ContainerKind.Unknown;
    }

    private static List<uint> ReadFatSectorIds(
        Stream stream,
        byte[] header,
        int sectorSize,
        int maximumSectorCount)
    {
        var expectedCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x2C, 4));
        if (expectedCount == 0 || expectedCount > maximumSectorCount)
        {
            return [];
        }

        var result = new List<uint>((int)expectedCount);
        for (var index = 0; index < 109 && result.Count < expectedCount; index++)
        {
            AddSectorId(header.AsSpan(0x4C + (index * 4), 4), result, maximumSectorCount);
        }

        var nextDifatSector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x44, 4));
        var difatSectorCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x48, 4));
        var buffer = new byte[sectorSize];
        var visited = new HashSet<uint>();
        for (var index = 0U;
             result.Count < expectedCount && index < difatSectorCount && IsRegularSector(nextDifatSector, maximumSectorCount);
             index++)
        {
            if (!visited.Add(nextDifatSector) || !ReadSector(stream, nextDifatSector, sectorSize, buffer))
            {
                break;
            }

            for (var entry = 0; entry < (sectorSize / 4) - 1 && result.Count < expectedCount; entry++)
            {
                AddSectorId(buffer.AsSpan(entry * 4, 4), result, maximumSectorCount);
            }
            nextDifatSector = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sectorSize - 4, 4));
        }

        return result;
    }

    private static void AddSectorId(ReadOnlySpan<byte> bytes, List<uint> result, int maximumSectorCount)
    {
        var sectorId = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (IsRegularSector(sectorId, maximumSectorCount))
        {
            result.Add(sectorId);
        }
    }

    private static uint[] ReadFat(
        Stream stream,
        IReadOnlyList<uint> fatSectorIds,
        int sectorSize,
        int maximumSectorCount)
    {
        var entriesPerSector = sectorSize / 4;
        var result = new uint[fatSectorIds.Count * entriesPerSector];
        var buffer = new byte[sectorSize];
        for (var sectorIndex = 0; sectorIndex < fatSectorIds.Count; sectorIndex++)
        {
            if (!ReadSector(stream, fatSectorIds[sectorIndex], sectorSize, buffer))
            {
                return [];
            }

            for (var entry = 0; entry < entriesPerSector; entry++)
            {
                result[(sectorIndex * entriesPerSector) + entry] =
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(entry * 4, 4));
            }
        }

        return result.Length >= maximumSectorCount ? result : [];
    }

    private static HashSet<string> ReadDirectoryNames(
        Stream stream,
        uint firstSector,
        IReadOnlyList<uint> fat,
        int sectorSize,
        int maximumSectorCount)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<uint>();
        var buffer = new byte[sectorSize];
        var current = firstSector;
        while (IsRegularSector(current, maximumSectorCount) && current < fat.Count && visited.Add(current))
        {
            if (!ReadSector(stream, current, sectorSize, buffer))
            {
                break;
            }

            for (var offset = 0; offset + 128 <= buffer.Length; offset += 128)
            {
                var nameByteLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 64, 2));
                if (nameByteLength is < 2 or > 64 || (nameByteLength & 1) != 0)
                {
                    continue;
                }

                var name = Encoding.Unicode.GetString(buffer, offset, nameByteLength - 2);
                if (name.Length > 0)
                {
                    names.Add(name);
                }
            }

            var next = fat[(int)current];
            if (next is EndOfChain or FreeSector or FatSector or DifatSector)
            {
                break;
            }
            current = next;
        }

        return names;
    }

    private static bool ReadSector(Stream stream, uint sectorId, int sectorSize, byte[] buffer)
    {
        var offset = checked(((long)sectorId + 1) * sectorSize);
        if (offset < 512 || offset + sectorSize > stream.Length)
        {
            return false;
        }

        stream.Position = offset;
        stream.ReadExactly(buffer.AsSpan(0, sectorSize));
        return true;
    }

    private static bool IsRegularSector(uint sectorId, int maximumSectorCount) =>
        sectorId < maximumSectorCount;
}
