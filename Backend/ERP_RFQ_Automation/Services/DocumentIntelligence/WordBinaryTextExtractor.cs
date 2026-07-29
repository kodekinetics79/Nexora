using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Spire.Doc;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

/// <summary>
/// Clean plain-text extraction from legacy Word 97-2003 binary (.doc) files:
/// OLE Compound File parsing + Word FIB/Piece-Table extraction, avoiding the garbage
/// characters produced by raw ISO-8859-1 decoding.
///
/// Extracted VERBATIM from FolderService (which now delegates here) so the unified
/// extraction pipeline's ProductionDocumentReader can read the same .doc files the
/// SEC folder door receives. Static + stateless; the optional logger only adds
/// diagnostics.
/// </summary>
public static class WordBinaryTextExtractor
{
    // OLE Compound File magic bytes: D0 CF 11 E0 A1 B1 1A E1
    private static readonly byte[] OleSignature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    /// <summary>Returns the extracted text, or "" when the bytes are not a readable
    /// Word binary document. Never throws.</summary>
    public static string Extract(byte[] bytes, ILogger? logger = null)
    {
        var native = ExtractNative(bytes, logger);
        return string.IsNullOrWhiteSpace(native)
            ? ExtractWithLocalLibrary(bytes, logger)
            : native;
    }

    private static string ExtractNative(byte[] bytes, ILogger? logger)
    {
        try
        {
            // Verify OLE signature
            if (bytes.Length < 8)
            {
                logger?.LogWarning("File too short to be a valid OLE document");
                return string.Empty;
            }

            for (int i = 0; i < OleSignature.Length; i++)
            {
                if (bytes[i] != OleSignature[i])
                {
                    logger?.LogWarning("Not a valid OLE document - signature mismatch");
                    return string.Empty;
                }
            }

            // ── Step 1: Parse OLE header ──────────────────────────────────────
            int sectorSize = 1 << ReadUInt16(bytes, 30);   // 512 or 4096 bytes
            int rootSector = (int)ReadUInt32(bytes, 48);   // first sector of root dir

            // Build the FAT (File Allocation Table) from the DIFAT stored in the header
            int[] fat = BuildFat(bytes, sectorSize);

            // ── Step 2: Read the directory to find stream locations ────────────
            byte[] rootDir = ReadStream(bytes, fat, rootSector, sectorSize);

            if (!TryFindDirectoryEntry(rootDir, "WordDocument", out int wdStart, out int wdStreamSize))
            {
                logger?.LogWarning("WordDocument stream not found in .doc file");
                return string.Empty;
            }

            bool hasTable1 = TryFindDirectoryEntry(rootDir, "1Table", out int t1Start, out _);
            bool hasTable0 = TryFindDirectoryEntry(rootDir, "0Table", out int t0Start, out _);

            if (!hasTable1 && !hasTable0)
            {
                logger?.LogWarning("Required OLE table streams not found in .doc file");
                return string.Empty;
            }

            // ── Step 3: Read the WordDocument stream ──────────────────────────
            byte[] wd = ReadStream(bytes, fat, wdStart, sectorSize, wdStreamSize);

            if (wd.Length < 200)
            {
                logger?.LogWarning("WordDocument stream too short ({Len} bytes)", wd.Length);
                return string.Empty;
            }

            // ── Step 4: Parse the FIB (File Information Block) ────────────────
            ushort cbFibRgW = ReadUInt16(wd, 32);
            int fibRgWEnd = 34 + cbFibRgW * 2;
            ushort cbFibRgLw = ReadUInt16(wd, fibRgWEnd);
            int fibRgLwStart = fibRgWEnd + 2;
            int ccpText = (int)ReadUInt32(wd, fibRgLwStart + 12);

            int fibRgLwEnd = fibRgLwStart + cbFibRgLw * 4;
            ushort cbFibRgFcLcb = ReadUInt16(wd, fibRgLwEnd);
            int fibRgFcLcbStart = fibRgLwEnd + 2;

            // fcClx is pair index 33 in FibRgFcLcb97
            const int CLX_PAIR_INDEX = 33;
            if (cbFibRgFcLcb <= CLX_PAIR_INDEX)
            {
                logger?.LogWarning("FibRgFcLcb too small ({Count} pairs)", cbFibRgFcLcb);
                return string.Empty;
            }

            int fcClx = (int)ReadUInt32(wd, fibRgFcLcbStart + CLX_PAIR_INDEX * 8);
            int lcbClx = (int)ReadUInt32(wd, fibRgFcLcbStart + CLX_PAIR_INDEX * 8 + 4);

            // Determine which table stream to use (bit 9 of word at offset 10)
            ushort fibFlags = ReadUInt16(wd, 10);
            bool useTable1 = (fibFlags & 0x0200) != 0;

            int tableStart = (useTable1 && hasTable1) ? t1Start
                           : hasTable0 ? t0Start
                           : t1Start;

            byte[] tableStream = ReadStream(bytes, fat, tableStart, sectorSize);

            // ── Step 5: Parse the Clx (piece table) ──────────────────────────
            if (fcClx < 0 || fcClx + lcbClx > tableStream.Length)
            {
                logger?.LogWarning("Clx offset out of range: fcClx={Fc}, len={Len}, streamLen={SL}",
                    fcClx, lcbClx, tableStream.Length);
                return string.Empty;
            }

            int clxIdx = fcClx;
            int clxEnd = fcClx + lcbClx;
            var sb = new StringBuilder(ccpText + 256);
            int piecesProcessed = 0;

            while (clxIdx < clxEnd)
            {
                byte clxt = tableStream[clxIdx++];

                if (clxt == 0x01) // Prc — skip it
                {
                    if (clxIdx + 2 > clxEnd) break;
                    ushort cb = ReadUInt16(tableStream, clxIdx);
                    clxIdx += 2 + cb;
                }
                else if (clxt == 0x02) // Pcdt — piece table
                {
                    if (clxIdx + 4 > clxEnd) break;
                    int lcbPlcPcd = (int)ReadUInt32(tableStream, clxIdx);
                    clxIdx += 4;

                    if (clxIdx + lcbPlcPcd > clxEnd) break;

                    // n = number of pieces
                    int n = (lcbPlcPcd - 4) / 12;
                    if (n <= 0) break;

                    // Read CP array: (n+1) × 4 bytes
                    int[] cps = new int[n + 1];
                    for (int i = 0; i <= n; i++)
                        cps[i] = (int)ReadUInt32(tableStream, clxIdx + i * 4);

                    // Read Pcd array: n × 8 bytes, starting after the CP array
                    int pcdBase = clxIdx + (n + 1) * 4;

                    for (int i = 0; i < n; i++)
                    {
                        int cpStart = cps[i];
                        int cpEnd = cps[i + 1];

                        // Only include main document text (before ccpText)
                        if (cpStart >= ccpText) continue;

                        int actualEnd = Math.Min(cpEnd, ccpText);
                        int charCount = actualEnd - cpStart;
                        if (charCount <= 0) continue;

                        // Pcd: flags(2) at +0, fc(4) at +2, prm(2) at +6
                        uint fcRaw = ReadUInt32(tableStream, pcdBase + i * 8 + 2);
                        bool compressed = (fcRaw & 0x40000000u) != 0;
                        int fc = (int)(fcRaw & 0x3FFFFFFFu);

                        if (compressed)
                        {
                            // CP1252 — byte offset = fc / 2
                            int byteOffset = fc >> 1;
                            if (byteOffset < 0 || byteOffset + charCount > wd.Length) continue;

                            string piece = Encoding.GetEncoding(1252).GetString(wd, byteOffset, charCount);
                            AppendDocPiece(sb, piece);
                            piecesProcessed++;
                        }
                        else
                        {
                            // UTF-16LE — byte offset = fc
                            int byteCount = charCount * 2;
                            if (fc < 0 || fc + byteCount > wd.Length) continue;

                            string piece = Encoding.Unicode.GetString(wd, fc, byteCount);
                            AppendDocPiece(sb, piece);
                            piecesProcessed++;
                        }
                    }
                    break; // Only one Pcdt per Clx
                }
                else
                {
                    break;
                }
            }

            string result = sb.ToString();

            // Post-processing: Clean up any remaining garbage patterns
            result = CleanExtractedText(result);

            logger?.LogInformation("OLE extraction completed: {Len} characters from {Pieces} pieces",
                result.Length, piecesProcessed);

            return result;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Native OLE/Word Binary DOC extraction failed.");
            return string.Empty;
        }
    }

    private static string ExtractWithLocalLibrary(byte[] bytes, ILogger? logger)
    {
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var document = new Document();
            document.LoadFromStream(input, FileFormat.Doc);
            return CleanExtractedText(document.GetText());
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "The local legacy DOC library could not parse the security-cleared document.");
            return string.Empty;
        }
    }

    /// <summary>
    /// Post-process extracted text to remove any remaining Word format codes and garbage
    /// </summary>
    private static string CleanExtractedText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Remove known Word format code sequences
        text = Regex.Replace(text, @"OJPJQJCJ[a-zA-Z0-9]*", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"OJPJQJCj[a-zA-Z0-9]*", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"56ph[a-zA-Z0-9]*", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\$If\$If\$?If?", "");
        text = Regex.Replace(text, @"[Ö¶Ä½][\x00-\x1F\x7F-\x9F]+", "");
        text = Regex.Replace(text, @"ÿÿÿÿ+", "");

        // Remove sequences of alternating characters (Word table codes)
        text = Regex.Replace(text, @"[a-z]{2}([a-z]{2})\1{3,}", "", RegexOptions.IgnoreCase);

        // Remove standalone format characters
        text = Regex.Replace(text, @"[\x00-\x08\x0E-\x1F\x7F]", "");

        // Clean up multiple spaces and newlines
        text = Regex.Replace(text, @" {2,}", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");

        return text.Trim();
    }

    /// <summary>
    /// Translates Word Binary special characters and appends to the StringBuilder.
    /// Enhanced version with aggressive garbage filtering.
    /// </summary>
    private static void AppendDocPiece(StringBuilder sb, string piece)
    {
        // Pre-filter: remove known Word format code sequences before character processing
        piece = Regex.Replace(piece, @"OJPJQJCJ[a-zA-Z0-9]*", "", RegexOptions.IgnoreCase);
        piece = Regex.Replace(piece, @"OJPJQJCj[a-zA-Z0-9]*", "", RegexOptions.IgnoreCase);
        piece = Regex.Replace(piece, @"56ph[a-zA-Z0-9]*", "", RegexOptions.IgnoreCase);
        piece = Regex.Replace(piece, @"\$If\$If\$?If?", "");
        piece = Regex.Replace(piece, @"[Ö¶Ä½][\x00-\x1F\x7F-\x9F]{1,10}", "");
        piece = Regex.Replace(piece, @"ÿÿÿÿ+", "");
        piece = Regex.Replace(piece, @"[rRtTÖö]{2,}Ä½+", ""); // Specific garbage pattern

        foreach (char c in piece)
        {
            switch (c)
            {
                case '\x07': // Table cell / row end marker
                case '\x14': // Field separator
                    sb.Append('\t');
                    break;
                case '\x0B': // Vertical tab / line break in cell
                case '\x0C': // Page break
                    sb.Append('\n');
                    break;
                case '\r':   // Paragraph end
                    sb.Append('\n');
                    break;
                case '\x00': case '\x01': case '\x02': case '\x03':
                case '\x04': case '\x05': case '\x06': case '\x08':
                case '\x0E': case '\x0F': case '\x10': case '\x11':
                case '\x12': case '\x13': case '\x15': case '\x16':
                case '\x17': case '\x18': case '\x19': case '\x1A':
                case '\x1B': case '\x1C': case '\x1D': case '\x1E': case '\x1F':
                    // Skip all control characters
                    break;
                default:
                    // Accept standard printable ASCII (0x20–0x7E) and
                    // common extended-Latin printable chars (0xA0–0xFF)
                    if ((c >= ' ' && c <= '~') ||          // Standard ASCII printable
                        (c >= '\xA0' && c <= '\xFF'))      // Extended Latin-1
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
    }

    // ── OLE Helpers ─────────────────────────────────────────────────────────

    private static int[] BuildFat(byte[] data, int sectorSize)
    {
        int entriesPerSector = sectorSize / 4;
        int numFatSectors = (int)ReadUInt32(data, 44);

        var fat = new List<int>();

        // DIFAT entries are at offset 76 (109 entries in the header)
        for (int d = 0; d < 109 && d < numFatSectors; d++)
        {
            uint sectorId = ReadUInt32(data, 76 + d * 4);
            if (sectorId >= 0xFFFFFFFE) break;

            int offset = 512 + (int)sectorId * sectorSize;
            if (offset + sectorSize > data.Length) break;

            for (int j = 0; j < entriesPerSector; j++)
                fat.Add((int)ReadUInt32(data, offset + j * 4));
        }

        return fat.ToArray();
    }

    private static int[] GetSectorChain(int[] fat, int start)
    {
        var chain = new List<int>();
        var visited = new HashSet<int>();
        int current = start;

        while (current >= 0 && current < 0xFFFFFFFE && !visited.Contains(current))
        {
            visited.Add(current);
            chain.Add(current);
            current = current < fat.Length ? fat[current] : int.MaxValue;
        }

        return chain.ToArray();
    }

    private static byte[] ReadStream(byte[] data, int[] fat, int start, int sectorSize, int maxBytes = int.MaxValue)
    {
        int[] chain = GetSectorChain(fat, start);
        int totalBytes = Math.Min(chain.Length * sectorSize, maxBytes);
        var result = new byte[totalBytes];
        int written = 0;

        foreach (int sector in chain)
        {
            int offset = 512 + sector * sectorSize;
            int toCopy = Math.Min(sectorSize, totalBytes - written);
            if (toCopy <= 0) break;
            if (offset + toCopy > data.Length) break;

            Buffer.BlockCopy(data, offset, result, written, toCopy);
            written += toCopy;
        }

        if (written == totalBytes) return result;

        var trimmed = new byte[written];
        Buffer.BlockCopy(result, 0, trimmed, 0, written);
        return trimmed;
    }

    /// <summary>
    /// Scans the root directory stream for a named entry and returns its
    /// starting sector and declared stream size.
    /// </summary>
    private static bool TryFindDirectoryEntry(byte[] dirData, string name,
                                               out int startSector, out int streamSize)
    {
        startSector = 0;
        streamSize = 0;
        int entrySize = 128;
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);

        for (int i = 0; i + entrySize <= dirData.Length; i += entrySize)
        {
            int nameLen = ReadUInt16(dirData, i + 64);
            if (nameLen < 2) continue;

            // Compare name (nameLen includes null terminator → nameLen-2 bytes of name)
            int compareLen = nameLen - 2;
            if (compareLen != nameBytes.Length) continue;

            int charCount = compareLen / 2;
            if (charCount * 2 != nameBytes.Length) continue;

            bool match = true;
            for (int j = 0; j < charCount; j++)
            {
                char a = (char)(dirData[i + j * 2] | (dirData[i + j * 2 + 1] << 8));
                char b = (char)(nameBytes[j * 2] | (nameBytes[j * 2 + 1] << 8));
                if (char.ToUpperInvariant(a) != char.ToUpperInvariant(b))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                startSector = (int)ReadUInt32(dirData, i + 116);
                streamSize = (int)ReadUInt32(dirData, i + 120);
                return true;
            }
        }

        return false;
    }

    // ── Bit-level read helpers ───────────────────────────────────────────────

    private static ushort ReadUInt16(byte[] data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));

    private static uint ReadUInt32(byte[] data, int offset) =>
        (uint)(data[offset]
            | (data[offset + 1] << 8)
            | (data[offset + 2] << 16)
            | (data[offset + 3] << 24));
}
