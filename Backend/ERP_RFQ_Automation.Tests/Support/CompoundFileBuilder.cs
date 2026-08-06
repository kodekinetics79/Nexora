using System.Buffers.Binary;
using System.Text;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>
/// Writes a REAL Microsoft Compound File (OLE2 / CFB) — header, FAT, MiniFAT, mini stream and a
/// balanced directory tree — so <c>.msg</c> tests drive the production reader over bytes an
/// Outlook client would actually produce.
///
/// <para>
/// This exists because the corpus has no genuine <c>.msg</c> and the existing OLE helper in
/// <c>MacroPolicyAndRejectionTruthTests</c> writes directory ENTRIES ONLY, with no stream content
/// at all — enough to test a macro-name scan, useless for testing a reader that has to pull the
/// subject, the body and an attachment payload back out. A stub proves nothing about a path that
/// has never run.
/// </para>
///
/// <para>
/// Small streams go through the mini stream exactly as the format requires (&lt; 4096 bytes), so
/// the reader's MiniFAT path — the one every MAPI property actually takes — is exercised rather
/// than bypassed.
/// </para>
/// </summary>
internal sealed class CompoundFileBuilder
{
    private const int SectorSize = 512;
    private const int MiniSectorSize = 64;
    private const int MiniStreamCutoff = 4096;
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private const uint FatSector = 0xFFFFFFFD;
    private const uint NoStream = 0xFFFFFFFF;

    private sealed class Node
    {
        public required string Name { get; init; }
        public required bool IsStorage { get; init; }
        public byte[] Data { get; init; } = [];
        public List<Node> Children { get; } = [];
        public uint Id { get; set; } = NoStream;
        public uint Left { get; set; } = NoStream;
        public uint Right { get; set; } = NoStream;
        public uint Child { get; set; } = NoStream;
        public uint Start { get; set; } = EndOfChain;
        public long Size { get; set; }
    }

    private readonly Node _root = new() { Name = "Root Entry", IsStorage = true };

    public CompoundFileBuilder AddStream(string name, byte[] data)
    {
        _root.Children.Add(new Node { Name = name, IsStorage = false, Data = data, Size = data.LongLength });
        return this;
    }

    public CompoundFileBuilder AddStream(string name, string text) =>
        AddStream(name, Encoding.Unicode.GetBytes(text));

    /// <summary>Adds a storage and returns a builder scoped to it.</summary>
    public CompoundFileBuilder AddStorage(string name, Action<StorageScope> configure)
    {
        var storage = new Node { Name = name, IsStorage = true };
        _root.Children.Add(storage);
        configure(new StorageScope(storage));
        return this;
    }

    internal sealed class StorageScope(object node)
    {
        private readonly Node _node = (Node)node;

        public StorageScope AddStream(string name, byte[] data)
        {
            _node.Children.Add(new Node { Name = name, IsStorage = false, Data = data, Size = data.LongLength });
            return this;
        }

        public StorageScope AddStream(string name, string text) =>
            AddStream(name, Encoding.Unicode.GetBytes(text));

        public StorageScope AddStorage(string name, Action<StorageScope> configure)
        {
            var storage = new Node { Name = name, IsStorage = true };
            _node.Children.Add(storage);
            configure(new StorageScope(storage));
            return this;
        }
    }

    public byte[] Build()
    {
        var all = new List<Node>();
        AssignIds(_root, all);
        LinkTree(_root);

        // 1) Mini stream: every stream smaller than the cutoff, 64-byte aligned.
        var miniStream = new MemoryStream();
        var miniFat = new List<uint>();
        foreach (var node in all.Where(n => !n.IsStorage && n.Data.Length > 0 && n.Data.Length < MiniStreamCutoff))
        {
            var first = (uint)(miniStream.Length / MiniSectorSize);
            var sectors = (node.Data.Length + MiniSectorSize - 1) / MiniSectorSize;
            for (var index = 0; index < sectors; index++)
            {
                miniFat.Add(index == sectors - 1 ? EndOfChain : first + (uint)index + 1);
                var offset = index * MiniSectorSize;
                var take = Math.Min(MiniSectorSize, node.Data.Length - offset);
                miniStream.Write(node.Data, offset, take);
                for (var pad = take; pad < MiniSectorSize; pad++) miniStream.WriteByte(0);
            }
            node.Start = first;
        }

        var sectorData = new List<byte[]>();
        var fat = new List<uint>();

        // 2) Large streams take regular sectors.
        foreach (var node in all.Where(n => !n.IsStorage && n.Data.Length >= MiniStreamCutoff))
        {
            node.Start = Allocate(sectorData, fat, node.Data);
        }

        // 3) The mini stream is itself a regular stream, owned by the root entry.
        var miniStreamBytes = miniStream.ToArray();
        _root.Size = miniStreamBytes.Length;
        _root.Start = miniStreamBytes.Length == 0 ? EndOfChain : Allocate(sectorData, fat, miniStreamBytes);

        // 4) The directory can only be laid out once every start sector is known.
        var directory = BuildDirectory(all);

        var miniFatBytes = PackUInts(miniFat);
        var firstMiniFat = miniFatBytes.Length == 0 ? EndOfChain : Allocate(sectorData, fat, miniFatBytes);
        var miniFatSectorCount = (uint)(miniFatBytes.Length / SectorSize);
        var firstDirectory = Allocate(sectorData, fat, directory);

        // 5) The FAT describes the sectors it lives in, so its size is a fixed point.
        var entriesPerSector = SectorSize / 4;
        var fatSectorCount = 1;
        while (true)
        {
            var required = (int)Math.Ceiling((sectorData.Count + fatSectorCount) / (double)entriesPerSector);
            if (required <= fatSectorCount) break;
            fatSectorCount = required;
        }

        var fatSectorIds = new List<uint>();
        for (var index = 0; index < fatSectorCount; index++)
        {
            fatSectorIds.Add((uint)sectorData.Count);
            sectorData.Add(new byte[SectorSize]);
            fat.Add(FatSector);
        }

        var fatEntries = new uint[fatSectorCount * entriesPerSector];
        Array.Fill(fatEntries, FreeSector);
        for (var index = 0; index < fat.Count; index++) fatEntries[index] = fat[index];
        var fatBytes = PackUInts(fatEntries);
        for (var index = 0; index < fatSectorCount; index++)
            Buffer.BlockCopy(fatBytes, index * SectorSize, sectorData[(int)fatSectorIds[index]], 0, SectorSize);

        // 6) Header + sectors.
        var file = new byte[SectorSize * (sectorData.Count + 1)];
        Convert.FromHexString("D0CF11E0A1B11AE1").CopyTo(file, 0);
        WriteUInt16(file, 24, 0x003E);
        WriteUInt16(file, 26, 0x0003);
        WriteUInt16(file, 28, 0xFFFE);
        WriteUInt16(file, 30, 9);
        WriteUInt16(file, 32, 6);
        WriteUInt32(file, 40, 0);
        WriteUInt32(file, 44, (uint)fatSectorCount);
        WriteUInt32(file, 48, firstDirectory);
        WriteUInt32(file, 56, MiniStreamCutoff);
        WriteUInt32(file, 60, firstMiniFat);
        WriteUInt32(file, 64, miniFatSectorCount);
        WriteUInt32(file, 68, EndOfChain);
        WriteUInt32(file, 72, 0);
        for (var index = 0; index < 109; index++)
        {
            WriteUInt32(file, 76 + index * 4,
                index < fatSectorIds.Count ? fatSectorIds[index] : FreeSector);
        }

        for (var index = 0; index < sectorData.Count; index++)
            Buffer.BlockCopy(sectorData[index], 0, file, SectorSize * (index + 1), SectorSize);

        return file;
    }

    private static uint Allocate(List<byte[]> sectorData, List<uint> fat, byte[] data)
    {
        var first = (uint)sectorData.Count;
        var sectors = Math.Max(1, (data.Length + SectorSize - 1) / SectorSize);
        for (var index = 0; index < sectors; index++)
        {
            var sector = new byte[SectorSize];
            var offset = index * SectorSize;
            var take = Math.Min(SectorSize, Math.Max(0, data.Length - offset));
            if (take > 0) Buffer.BlockCopy(data, offset, sector, 0, take);
            sectorData.Add(sector);
            fat.Add(index == sectors - 1 ? EndOfChain : first + (uint)index + 1);
        }
        return first;
    }

    private static void AssignIds(Node root, List<Node> all)
    {
        root.Id = 0;
        all.Add(root);
        var queue = new Queue<Node>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            foreach (var child in queue.Dequeue().Children)
            {
                child.Id = (uint)all.Count;
                all.Add(child);
                queue.Enqueue(child);
            }
        }
    }

    private static void LinkTree(Node storage)
    {
        var ordered = storage.Children
            .OrderBy(child => child.Name.Length)
            .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        storage.Child = Balance(ordered, 0, ordered.Count - 1);
        foreach (var child in storage.Children.Where(c => c.IsStorage)) LinkTree(child);
    }

    private static uint Balance(List<Node> ordered, int low, int high)
    {
        if (low > high) return NoStream;
        var middle = (low + high) / 2;
        var node = ordered[middle];
        node.Left = Balance(ordered, low, middle - 1);
        node.Right = Balance(ordered, middle + 1, high);
        return node.Id;
    }

    private static byte[] BuildDirectory(List<Node> all)
    {
        var entriesPerSector = SectorSize / 128;
        var sectors = Math.Max(1, (int)Math.Ceiling(all.Count / (double)entriesPerSector));
        var directory = new byte[sectors * SectorSize];
        for (var index = 0; index < directory.Length; index += 128)
        {
            WriteUInt32(directory, index + 68, NoStream);
            WriteUInt32(directory, index + 72, NoStream);
            WriteUInt32(directory, index + 76, NoStream);
        }

        foreach (var node in all)
        {
            var offset = (int)node.Id * 128;
            var name = Encoding.Unicode.GetBytes(node.Name + '\0');
            name.CopyTo(directory, offset);
            WriteUInt16(directory, offset + 64, (ushort)name.Length);
            directory[offset + 66] = node.Id == 0 ? (byte)5 : node.IsStorage ? (byte)1 : (byte)2;
            directory[offset + 67] = 1; // black
            WriteUInt32(directory, offset + 68, node.Left);
            WriteUInt32(directory, offset + 72, node.Right);
            WriteUInt32(directory, offset + 76, node.Child);
            WriteUInt32(directory, offset + 116, node.Start);
            BinaryPrimitives.WriteInt64LittleEndian(
                directory.AsSpan(offset + 120, 8), node.IsStorage ? node.Size : node.Data.LongLength);
        }
        return directory;
    }

    private static byte[] PackUInts(IReadOnlyCollection<uint> values)
    {
        if (values.Count == 0) return [];
        var sectors = (int)Math.Ceiling(values.Count / (double)(SectorSize / 4));
        var bytes = new byte[sectors * SectorSize];
        for (var index = 0; index < bytes.Length; index += 4) WriteUInt32(bytes, index, FreeSector);
        var cursor = 0;
        foreach (var value in values) WriteUInt32(bytes, cursor++ * 4, value);
        return bytes;
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
