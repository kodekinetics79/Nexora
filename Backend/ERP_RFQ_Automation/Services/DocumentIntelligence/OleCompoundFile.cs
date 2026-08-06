using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

/// <summary>
/// Thrown when a compound file cannot be walked safely. The message is OPERATOR/DEVELOPER
/// wording — callers translate it into user-facing copy; it is never rendered verbatim to a
/// tenant, because a malformed structure detail is not a remedy anyone can act on.
/// </summary>
public sealed class OleCompoundFileException : IOException
{
    public OleCompoundFileException(string message) : base(message) { }
}

/// <summary>One entry in the compound-file directory.</summary>
/// <param name="Name">The entry's own name (not a path).</param>
/// <param name="Path">Slash-joined path from the root, e.g. <c>__attach_version1.0_#00000000/__substg1.0_37010102</c>.</param>
/// <param name="IsStorage">True for a storage (directory), false for a stream (file).</param>
/// <param name="Length">Declared stream length in bytes; 0 for storages.</param>
public sealed record OleEntry(string Name, string Path, bool IsStorage, long Length);

/// <summary>
/// A bounded reader for the Microsoft Compound File Binary format (OLE2 / CFB) — the container
/// under legacy <c>.doc</c>/<c>.xls</c> AND under Outlook <c>.msg</c>.
///
/// <para>
/// WHY THIS EXISTS RATHER THAN A PACKAGE. <c>DocumentFileInspectionService.ReadOleDirectoryNames</c>
/// already walks the FAT and the directory to answer "does this contain a macro storage?", but it
/// deliberately reads NAMES ONLY. Reading an Outlook message needs stream CONTENT — the property
/// streams (<c>__substg1.0_*</c>) and the attachment storages — which means the FAT chain, the
/// MiniFAT chain and the root mini-stream as well. That is this type. It adds no third-party
/// dependency and no supply-chain surface to a file format that is, by construction, hostile input.
/// </para>
///
/// <para>
/// EVERY LOOP IS BOUNDED. Compound files are sector-linked lists whose links are attacker
/// controlled: a chain that points at itself is a hang, and a chain that fans out is an OOM. So
/// every chain walk carries a visited set (a repeated sector is a hard error, never a silent
/// stop), every read is range-checked against the buffer, the directory is capped by
/// <see cref="MaxDirectoryEntries"/>, and each stream read is capped by the caller's own limit.
/// Nothing here allocates in proportion to a declared length before that length has been checked
/// against the bytes that actually exist.
/// </para>
/// </summary>
public sealed class OleCompoundFile
{
    private const uint MaxRegSect = 0xFFFFFFFA;
    private const uint Difsect = 0xFFFFFFFC;
    private const uint Fatsect = 0xFFFFFFFD;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSect = 0xFFFFFFFF;
    private const uint NoStream = 0xFFFFFFFF;

    /// <summary>Directory-entry ceiling. A real .msg with 50 attachments carries a few hundred.</summary>
    public const int MaxDirectoryEntries = 10_000;

    private static readonly byte[] Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private readonly byte[] _bytes;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly long _miniStreamCutoff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly byte[] _directory;
    private readonly uint _miniStreamStart;
    private readonly long _miniStreamLength;

    private OleCompoundFile(
        byte[] bytes, int sectorSize, int miniSectorSize, long miniStreamCutoff,
        uint[] fat, uint[] miniFat, byte[] directory, uint miniStreamStart, long miniStreamLength)
    {
        _bytes = bytes;
        _sectorSize = sectorSize;
        _miniSectorSize = miniSectorSize;
        _miniStreamCutoff = miniStreamCutoff;
        _fat = fat;
        _miniFat = miniFat;
        _directory = directory;
        _miniStreamStart = miniStreamStart;
        _miniStreamLength = miniStreamLength;
    }

    /// <summary>True when <paramref name="bytes"/> opens with the CFB magic bytes.</summary>
    public static bool HasSignature(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Signature);

    /// <summary>
    /// Parses the header, the FAT, the MiniFAT and the directory. Throws
    /// <see cref="OleCompoundFileException"/> for anything structurally impossible rather than
    /// returning a half-built reader.
    /// </summary>
    public static OleCompoundFile Open(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 512 || !HasSignature(bytes))
            throw new OleCompoundFileException("The compound file is truncated or carries no OLE signature.");

        var sectorShift = ReadUInt16(bytes, 30);
        var sectorSize = sectorShift switch
        {
            9 => 512,
            12 => 4096,
            _ => throw new OleCompoundFileException("The compound file declares an unsupported sector size.")
        };
        var miniSectorShift = ReadUInt16(bytes, 32);
        if (miniSectorShift is < 4 or > 12 || miniSectorShift >= sectorShift)
            throw new OleCompoundFileException("The compound file declares an unsupported mini-sector size.");
        var miniSectorSize = 1 << miniSectorShift;

        if (bytes.Length < sectorSize || bytes.Length % sectorSize != 0)
            throw new OleCompoundFileException("The compound file length is not a whole number of sectors.");
        var sectorCount = (bytes.Length / sectorSize) - 1;
        if (sectorCount <= 0)
            throw new OleCompoundFileException("The compound file carries no data sectors.");

        var fatSectorCount = ReadUInt32(bytes, 44);
        var firstDirectorySector = ReadUInt32(bytes, 48);
        var miniStreamCutoff = ReadUInt32(bytes, 56);
        var firstMiniFatSector = ReadUInt32(bytes, 60);
        var miniFatSectorCount = ReadUInt32(bytes, 64);
        var firstDifatSector = ReadUInt32(bytes, 68);
        var difatSectorCount = ReadUInt32(bytes, 72);

        if (fatSectorCount == 0 || fatSectorCount > sectorCount ||
            miniFatSectorCount > sectorCount || difatSectorCount > sectorCount)
            throw new OleCompoundFileException("The compound file declares an impossible allocation table.");
        if (miniStreamCutoff == 0 || miniStreamCutoff > sectorSize)
            miniStreamCutoff = 4096;

        // ---- DIFAT -> the list of FAT sectors -------------------------------------------
        var fatSectors = new List<uint>((int)fatSectorCount);
        var seenFatSectors = new HashSet<uint>();
        for (var index = 0; index < 109 && fatSectors.Count < fatSectorCount; index++)
        {
            var sector = ReadUInt32(bytes, 76 + index * 4);
            if (sector == FreeSect) continue;
            AddSector(fatSectors, seenFatSectors, sector, sectorCount);
        }

        var visitedDifat = new HashSet<uint>();
        var difatSector = firstDifatSector;
        var difatEntriesPerSector = sectorSize / 4 - 1;
        for (var index = 0u; index < difatSectorCount && fatSectors.Count < fatSectorCount; index++)
        {
            if (difatSector >= sectorCount || !visitedDifat.Add(difatSector))
                throw new OleCompoundFileException("The compound file DIFAT chain is invalid.");
            var offset = SectorOffset(difatSector, sectorSize);
            for (var entry = 0; entry < difatEntriesPerSector && fatSectors.Count < fatSectorCount; entry++)
            {
                var sector = ReadUInt32(bytes, offset + entry * 4);
                if (sector == FreeSect) continue;
                AddSector(fatSectors, seenFatSectors, sector, sectorCount);
            }
            difatSector = ReadUInt32(bytes, offset + difatEntriesPerSector * 4);
        }

        if (fatSectors.Count != fatSectorCount)
            throw new OleCompoundFileException("The compound file allocation table is incomplete.");

        var entriesPerSector = sectorSize / 4;
        var fat = new uint[fatSectors.Count * entriesPerSector];
        var cursor = 0;
        foreach (var fatSector in fatSectors)
        {
            var offset = SectorOffset(fatSector, sectorSize);
            for (var entry = 0; entry < entriesPerSector; entry++)
                fat[cursor++] = ReadUInt32(bytes, offset + entry * 4);
        }

        // ---- MiniFAT -------------------------------------------------------------------
        var miniFat = Array.Empty<uint>();
        if (miniFatSectorCount > 0 && firstMiniFatSector <= MaxRegSect)
        {
            var miniFatSectors = WalkChain(fat, firstMiniFatSector, sectorCount, (int)miniFatSectorCount);
            miniFat = new uint[miniFatSectors.Count * entriesPerSector];
            cursor = 0;
            foreach (var sector in miniFatSectors)
            {
                var offset = SectorOffset(sector, sectorSize);
                for (var entry = 0; entry < entriesPerSector; entry++)
                    miniFat[cursor++] = ReadUInt32(bytes, offset + entry * 4);
            }
        }

        // ---- Directory -----------------------------------------------------------------
        var maxDirectorySectors = Math.Max(1, (int)Math.Ceiling(MaxDirectoryEntries * 128d / sectorSize));
        var directorySectors = WalkChain(fat, firstDirectorySector, sectorCount, maxDirectorySectors);
        if (directorySectors.Count == 0)
            throw new OleCompoundFileException("The compound file has no directory.");
        var directory = new byte[directorySectors.Count * sectorSize];
        cursor = 0;
        foreach (var sector in directorySectors)
        {
            Buffer.BlockCopy(bytes, SectorOffset(sector, sectorSize), directory, cursor, sectorSize);
            cursor += sectorSize;
        }

        // The root entry owns the mini stream.
        if (directory.Length < 128)
            throw new OleCompoundFileException("The compound file directory is truncated.");
        var miniStreamStart = ReadUInt32(directory, 116);
        var miniStreamLength = ReadInt64(directory, 120);
        if (miniStreamLength < 0)
            throw new OleCompoundFileException("The compound file mini stream declares a negative length.");

        return new OleCompoundFile(
            bytes, sectorSize, miniSectorSize, miniStreamCutoff,
            fat, miniFat, directory, miniStreamStart, miniStreamLength);
    }

    /// <summary>
    /// Every allocated directory entry, depth-first from the root, as slash-joined paths.
    /// The traversal carries a visited set over directory IDs: the red-black sibling/child
    /// pointers are attacker-controlled and a cycle would otherwise never terminate.
    /// </summary>
    public IReadOnlyList<OleEntry> Enumerate()
    {
        var entries = new List<OleEntry>();
        var visited = new HashSet<uint>();
        var rootChild = ReadUInt32(_directory, 76);
        if (rootChild != NoStream)
            Walk(rootChild, string.Empty, entries, visited, depth: 0);
        return entries;
    }

    /// <summary>
    /// Reads the stream at <paramref name="path"/>, or null when it is absent or is a storage.
    /// Never allocates more than <paramref name="maximumBytes"/>; a stream that declares more is
    /// an error, not a truncation, because silently returning half a property is exactly the
    /// silent corruption this program exists to remove.
    /// </summary>
    public byte[]? ReadStream(string path, long maximumBytes)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var located = FindEntryOffset(path);
        if (located is not { } offset) return null;
        if (_directory[offset + 66] != 2) return null; // not a stream
        return ReadStreamAt(offset, maximumBytes);
    }

    /// <summary>True when a storage or stream exists at <paramref name="path"/>.</summary>
    public bool Contains(string path) => FindEntryOffset(path) is not null;

    private byte[] ReadStreamAt(int entryOffset, long maximumBytes)
    {
        var length = ReadInt64(_directory, entryOffset + 120);
        if (length < 0)
            throw new OleCompoundFileException("A compound-file stream declares a negative length.");
        if (length == 0) return [];
        if (length > maximumBytes)
            throw new OleCompoundFileException("A compound-file stream exceeds the size this reader accepts.");
        if (length > _bytes.LongLength)
            throw new OleCompoundFileException("A compound-file stream declares more bytes than the file contains.");

        var start = ReadUInt32(_directory, entryOffset + 116);
        var result = new byte[length];

        if (length < _miniStreamCutoff)
        {
            ReadFromMiniStream(start, result);
            return result;
        }

        var sectorCount = (_bytes.Length / _sectorSize) - 1;
        var needed = (int)((length + _sectorSize - 1) / _sectorSize);
        var chain = WalkChain(_fat, start, sectorCount, needed);
        if (chain.Count < needed)
            throw new OleCompoundFileException("A compound-file stream chain is shorter than its declared length.");
        var written = 0;
        foreach (var sector in chain)
        {
            var take = Math.Min(_sectorSize, result.Length - written);
            if (take <= 0) break;
            Buffer.BlockCopy(_bytes, SectorOffset(sector, _sectorSize), result, written, take);
            written += take;
        }
        return result;
    }

    private void ReadFromMiniStream(uint firstMiniSector, byte[] destination)
    {
        if (_miniFat.Length == 0)
            throw new OleCompoundFileException("The compound file has no mini allocation table for a short stream.");

        var sectorCount = (_bytes.Length / _sectorSize) - 1;
        var miniStreamSectors = WalkChain(
            _fat, _miniStreamStart, sectorCount,
            (int)((_miniStreamLength + _sectorSize - 1) / Math.Max(1, _sectorSize)) + 1);

        var written = 0;
        var visited = new HashSet<uint>();
        var miniSector = firstMiniSector;
        while (written < destination.Length)
        {
            if (miniSector > MaxRegSect)
                throw new OleCompoundFileException("A short compound-file stream ends before its declared length.");
            if (!visited.Add(miniSector))
                throw new OleCompoundFileException("A short compound-file stream chain contains a cycle.");
            if (miniSector >= _miniFat.Length)
                throw new OleCompoundFileException("A short compound-file stream references an unallocated mini sector.");

            var absolute = (long)miniSector * _miniSectorSize;
            var containerIndex = (int)(absolute / _sectorSize);
            if (containerIndex >= miniStreamSectors.Count)
                throw new OleCompoundFileException("A short compound-file stream reads past the mini stream.");
            var offset = SectorOffset(miniStreamSectors[containerIndex], _sectorSize)
                         + (int)(absolute % _sectorSize);
            var take = Math.Min(_miniSectorSize, destination.Length - written);
            if (offset < 0 || offset + take > _bytes.Length)
                throw new OleCompoundFileException("A short compound-file stream reads past the end of the file.");
            Buffer.BlockCopy(_bytes, offset, destination, written, take);
            written += take;
            miniSector = _miniFat[miniSector];
        }
    }

    private void Walk(uint id, string prefix, List<OleEntry> entries, HashSet<uint> visited, int depth)
    {
        // The directory is a red-black tree per storage; depth is bounded so a hand-built
        // pathological tree cannot recurse the stack away.
        if (depth > 64 || id == NoStream || !visited.Add(id)) return;
        if (entries.Count >= MaxDirectoryEntries)
            throw new OleCompoundFileException("The compound file contains too many directory entries.");

        var offset = checked((int)id) * 128;
        if (offset < 0 || offset + 128 > _directory.Length) return;

        var objectType = _directory[offset + 66];
        var nameLength = ReadUInt16(_directory, offset + 64);
        var name = nameLength is >= 2 and <= 64 && nameLength % 2 == 0
            ? Encoding.Unicode.GetString(_directory, offset, nameLength - 2)
            : string.Empty;

        // Siblings first, then this entry, then its children: order does not matter to callers,
        // only completeness does.
        Walk(ReadUInt32(_directory, offset + 68), prefix, entries, visited, depth + 1);
        Walk(ReadUInt32(_directory, offset + 72), prefix, entries, visited, depth + 1);

        if (objectType is 1 or 2 && name.Length > 0)
        {
            var path = prefix.Length == 0 ? name : prefix + "/" + name;
            entries.Add(new OleEntry(name, path, objectType == 1, objectType == 2 ? ReadInt64(_directory, offset + 120) : 0));
            Walk(ReadUInt32(_directory, offset + 76), path, entries, visited, depth + 1);
        }
        else
        {
            Walk(ReadUInt32(_directory, offset + 76), prefix, entries, visited, depth + 1);
        }
    }

    private int? FindEntryOffset(string path)
    {
        var visited = new HashSet<uint>();
        var entries = new List<(string Path, int Offset)>();
        CollectOffsets(ReadUInt32(_directory, 76), string.Empty, entries, visited, 0);
        foreach (var (candidate, offset) in entries)
        {
            if (string.Equals(candidate, path, StringComparison.Ordinal)) return offset;
        }
        return null;
    }

    private void CollectOffsets(
        uint id, string prefix, List<(string Path, int Offset)> entries, HashSet<uint> visited, int depth)
    {
        if (depth > 64 || id == NoStream || !visited.Add(id)) return;
        if (entries.Count >= MaxDirectoryEntries) return;

        var offset = checked((int)id) * 128;
        if (offset < 0 || offset + 128 > _directory.Length) return;

        var objectType = _directory[offset + 66];
        var nameLength = ReadUInt16(_directory, offset + 64);
        var name = nameLength is >= 2 and <= 64 && nameLength % 2 == 0
            ? Encoding.Unicode.GetString(_directory, offset, nameLength - 2)
            : string.Empty;

        CollectOffsets(ReadUInt32(_directory, offset + 68), prefix, entries, visited, depth + 1);
        CollectOffsets(ReadUInt32(_directory, offset + 72), prefix, entries, visited, depth + 1);

        if (objectType is 1 or 2 && name.Length > 0)
        {
            var path = prefix.Length == 0 ? name : prefix + "/" + name;
            entries.Add((path, offset));
            CollectOffsets(ReadUInt32(_directory, offset + 76), path, entries, visited, depth + 1);
        }
        else
        {
            CollectOffsets(ReadUInt32(_directory, offset + 76), prefix, entries, visited, depth + 1);
        }
    }

    private static void AddSector(List<uint> sectors, HashSet<uint> seen, uint sector, int sectorCount)
    {
        if (sector >= sectorCount || !seen.Add(sector))
            throw new OleCompoundFileException("The compound file allocation table names an invalid sector.");
        sectors.Add(sector);
    }

    private static List<uint> WalkChain(uint[] fat, uint start, int sectorCount, int maximumSectors)
    {
        var chain = new List<uint>();
        if (start > MaxRegSect) return chain;
        var visited = new HashSet<uint>();
        var sector = start;
        while (sector <= MaxRegSect)
        {
            if (sector >= sectorCount)
                throw new OleCompoundFileException("A compound-file sector chain leaves the file.");
            if (!visited.Add(sector))
                throw new OleCompoundFileException("A compound-file sector chain contains a cycle.");
            chain.Add(sector);
            if (chain.Count > maximumSectors)
                throw new OleCompoundFileException("A compound-file sector chain is longer than its declared stream.");
            if (sector >= fat.Length)
                throw new OleCompoundFileException("A compound-file sector chain leaves the allocation table.");
            var next = fat[sector];
            if (next is Fatsect or Difsect or FreeSect) break;
            sector = next;
            if (sector == EndOfChain) break;
        }
        return chain;
    }

    private static int SectorOffset(uint sector, int sectorSize) => checked((int)((sector + 1) * (uint)sectorSize));

    private static ushort ReadUInt16(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 2 > bytes.Length)
            throw new OleCompoundFileException("The compound file is truncated.");
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
            throw new OleCompoundFileException("The compound file is truncated.");
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    }

    private static long ReadInt64(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 8 > bytes.Length)
            throw new OleCompoundFileException("The compound file is truncated.");
        return BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, 8));
    }
}
