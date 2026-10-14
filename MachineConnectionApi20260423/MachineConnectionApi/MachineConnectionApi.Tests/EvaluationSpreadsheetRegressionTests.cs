using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using ExcelDataReader;
using MachineConnectionApi.Services;

namespace MachineConnectionApi.Tests;

internal static class EvaluationSpreadsheetRegressionTests
{
    public static void RunAll()
    {
        RealBiffPreservesChineseDatesZeroAndBlank();
        FormulaCachesAreNotEvidence();
        BiffFormulaVariantsAndRecordBoundaries();
        CompoundWorkbookChains();
        BinaryBudgetsAndCorruption();
        OpenXmlBudgetsAndHostileContent();
        NonSeekableInputBudget();
    }

    private static void RealBiffPreservesChineseDatesZeroAndBlank()
    {
        var bytes = EvaluationBiffFixture.Bytes();
        Check(bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }),
            "fixture must be a real OLE BIFF workbook");
        using var source = new MemoryStream(bytes);
        var rows = EvaluationSpreadsheetReader.Read(source);
        Check(rows.Count == 4 && rows[0][0] == "机床名称" && rows[1][0] == "二进制机床", "BIFF Unicode or physical rows lost");
        Check(rows[1][7] == "2026-09-28" && rows[1][10] == "0" && rows[1][11] == "", "BIFF date, zero or blank changed");
        Check(rows[2].All(string.IsNullOrEmpty) && rows[3][7] == "2026-99-99", "BIFF blank row or invalid date hidden");
        Check(rows[1][13] == "中文备注，二进制真实结果" && source.CanRead, "BIFF evidence lost or caller stream closed");
    }

    private static void FormulaCachesAreNotEvidence()
    {
        foreach (var stringCache in new[] { false, true })
        {
            var bytes = EvaluationBiffFixture.CachedFormulaBytes(stringCache);
            using (var source = new MemoryStream(bytes))
            using (var reader = ExcelReaderFactory.CreateBinaryReader(source))
            {
                Check(reader.Read() && reader.Read(), "formula fixture must have a data row");
                Check(stringCache ? reader.GetString(10) == "100" : reader.GetDouble(10) == 100,
                    "real OLE FORMULA fixture must reproduce the library's stale 100 cache");
            }
            Reject(bytes, "公式");
            using var nonSeekable = new NonSeekableStream(bytes);
            Expect<ArgumentException>(() => EvaluationSpreadsheetReader.Read(nonSeekable), "公式");
        }
    }

    private static void BiffFormulaVariantsAndRecordBoundaries()
    {
        foreach (ushort version in new ushort[] { 0x0500, 0x0600 })
        {
            using (var values = new MemoryStream(RawBiff(2, version: version)))
                Check(EvaluationSpreadsheetReader.Read(values)[1][0] == "value", "BIFF5/8 literals must remain supported");
            foreach (var formulaKind in new[] { "number", "string", "shared", "array" })
            {
                var bytes = RawBiff(2, version: version, sheetRecords: FormulaRecords(formulaKind, version));
                using (var source = new MemoryStream(bytes))
                using (var reader = ExcelReaderFactory.CreateBinaryReader(source))
                {
                    Check(reader.Read() && reader.Read() && reader.GetValue(0)?.ToString() == "100",
                        $"BIFF {version:X} {formulaKind} fixture must expose a real 100 cache before preflight");
                }
                Reject(bytes, "公式");
                Reject(EvaluationBiffFixture.WrapWorkbook(bytes, miniStream: true), "公式");
            }
            // Even an orphan formula-family record must not be mistaken for a literal.
            foreach (ushort id in new ushort[] { 0x0006, 0x0206, 0x0406, 0x0021, 0x0221, 0x04bc, 0x0036, 0x0236, 0x0007, 0x0207 })
                Reject(RawBiff(2, version: version, sheetRecords: BiffRecord(id, writer => writer.Write(new byte[32]))), "公式");
        }

        // Formula-like bytes inside SST and CONTINUE text are data, not record headers.
        foreach (var text in new[] { "literal\u0006\0\u0019\0not a formula", new string('x', 8000) + "\u0006\0\u0019\0\u0007\u0002" })
        {
            using var input = new MemoryStream(RawBiff(2, text: text));
            Check(EvaluationSpreadsheetReader.Read(input)[1][0] == text, "formula-like string bytes must not be rejected");
        }
        var number = BitConverter.Int64BitsToDouble(0x4059000000000006);
        var literal = BiffRecord(0x0203, writer => { writer.Write((ushort)1); writer.Write(0); writer.Write(number); });
        using (var input = new MemoryStream(RawBiff(2, sheetRecords: literal)))
            Check(EvaluationSpreadsheetReader.Read(input)[1][0] == number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "formula-like bytes in a literal NUMBER must not be rejected");

        // A forged BOUNDSHEET must not redirect the library to a BOF/FORMULA embedded in a
        // non-formula record payload that a naive linear opcode check would skip.
        var hiddenSheet = RawBiff(2, sheetRecords: FormulaRecords("number", 0x0600));
        var innerOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(hiddenSheet.AsSpan(RecordOffset(hiddenSheet, 0x0085) + 4));
        var outer = RawBiff(2, sheetRecords: BiffRecord(0x00ec, writer => writer.Write(hiddenSheet[innerOffset..])));
        var bound = RecordOffset(outer, 0x0085);
        var drawing = RecordOffset(outer, 0x00ec);
        BinaryPrimitives.WriteInt32LittleEndian(outer.AsSpan(bound + 4), drawing + 4);
        Reject(outer, "结构");

        var truncated = RawBiff(2);
        Reject(truncated[..^1], "结构");
        var oldVersion = RawBiff(2);
        BinaryPrimitives.WriteUInt16LittleEndian(oldVersion.AsSpan(4), 0x0400);
        Reject(oldVersion, "BIFF5/BIFF8");
        var trailing = RawBiff(2).Concat(new byte[] { 6, 0, 0, 0 }).ToArray();
        Reject(trailing, "结构");
    }

    private static void CompoundWorkbookChains()
    {
        var raw = RawBiff(2);
        foreach (var mini in new[] { false, true })
        foreach (var sectorSize in new[] { 512, 4096 })
        {
            var bytes = EvaluationBiffFixture.WrapWorkbook(raw, mini, sectorSize);
            using var source = new MemoryStream(bytes);
            Check(EvaluationSpreadsheetReader.Read(source)[1][0] == "value", "fragmented regular/mini CFB v3/v4 chains must work");
        }
        var legacyBook = EvaluationBiffFixture.WrapWorkbook(RawBiff(2, version: 0x0500), miniStream: true);
        Array.Clear(legacyBook, 1152, 64);
        Encoding.Unicode.GetBytes("Book\0").CopyTo(legacyBook, 1152);
        BinaryPrimitives.WriteUInt16LittleEndian(legacyBook.AsSpan(1152 + 64), 10);
        using (var source = new MemoryStream(legacyBook))
            Check(EvaluationSpreadsheetReader.Read(source)[1][0] == "value", "BIFF5 Book stream name must remain supported");

        var padded = EvaluationBiffFixture.WrapWorkbook(raw);
        // Unallocated OLE data is not part of Workbook and must not trigger a formula rejection.
        padded = padded.Concat(new byte[512]).ToArray();
        new byte[] { 6, 0, 25, 0 }.CopyTo(padded, padded.Length - 512);
        using (var source = new MemoryStream(padded))
            Check(EvaluationSpreadsheetReader.Read(source)[1][0] == "value", "unrelated OLE bytes must not be scanned");

        void MutateAndReject(bool mini, Action<byte[]> mutate, string message = "结构")
        {
            var bytes = EvaluationBiffFixture.WrapWorkbook(raw, mini);
            mutate(bytes);
            Reject(bytes, message);
        }
        void Set(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        uint Get(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        const int directory = 1024;
        const int workbook = directory + 128;
        MutateAndReject(false, bytes => Set(bytes, 512 + 4, 1)); // Directory FAT cycle.
        MutateAndReject(false, bytes => { var first = Get(bytes, workbook + 116); Set(bytes, 512 + (int)first * 4, first); });
        MutateAndReject(false, bytes => Set(bytes, workbook + 116, 1)); // Workbook aliases directory sector.
        MutateAndReject(false, bytes => Set(bytes, workbook + 116, uint.MaxValue - 10));
        MutateAndReject(false, bytes => Set(bytes, workbook + 120, int.MaxValue));
        MutateAndReject(false, bytes => Set(bytes, workbook + 72, 1)); // Directory sibling cycle.
        MutateAndReject(false, bytes =>
        {
            Array.Copy(bytes, workbook, bytes, workbook + 128, 128);
            Set(bytes, workbook + 72, 2); // Two root-level Workbook candidates are ambiguous.
        });
        MutateAndReject(true, bytes => { var first = Get(bytes, workbook + 116); Set(bytes, 1536 + (int)first * 4, first); });
        MutateAndReject(true, bytes => Set(bytes, 512 + 2 * 4, 2)); // MiniFAT's regular chain cycle.
        MutateAndReject(true, bytes => Set(bytes, directory + 120, int.MaxValue));
        MutateAndReject(false, bytes => Set(bytes, 72, 1), "BIFF5/BIFF8"); // Explicit unsupported external DIFAT.
    }

    private static void BinaryBudgetsAndCorruption()
    {
        using (var valid = new MemoryStream(RawBiff(2, 0, new string('x', EvaluationSpreadsheetReader.MaxCellCharacters))))
        {
            var rows = EvaluationSpreadsheetReader.Read(valid);
            Check(rows.Count == 2 && rows[1][0].Length == EvaluationSpreadsheetReader.MaxCellCharacters,
                "BIFF CONTINUE strings at the legal length must be accepted");
        }
        Reject(RawBiff(EvaluationSpreadsheetReader.MaxRows + 1), "1000");
        Reject(RawBiff(2, EvaluationSpreadsheetReader.MaxColumns), "列");
        Reject(RawBiff(2, 0, new string('x', EvaluationSpreadsheetReader.MaxCellCharacters + 1)), "文本");
        Reject(RawBiff(322, 0, new string('x', EvaluationSpreadsheetReader.MaxCellCharacters)), "文本总量");
        Reject(RawBiff(2, encrypted: true), "加密");
        Reject(EvaluationBiffFixture.Bytes()[..120], "XLS");
        Reject(Encoding.UTF8.GetBytes("<html><table><tr><td>not BIFF</td></tr></table></html>"), "BIFF");
    }

    private static void OpenXmlBudgetsAndHostileContent()
    {
        Reject(Workbook($"<row r=\"{EvaluationSpreadsheetReader.MaxRows + 1}\"/>"), "1000");
        Reject(Workbook("<row r=\"1\"><c r=\"BXY1\"><v>1</v></c></row>"), "列"); // BXY is column 2001.
        Reject(Workbook("<row r=\"1\"><c r=\"A2\"><v>1</v></c></row>"), "坐标");
        Reject(Workbook("<row r=\"1\"><c r=\"A1\"/><c r=\"A1\"/></row>"), "重复");
        Reject(Workbook("<row r=\"1\"/><row r=\"1\"/>"), "坐标");
        Reject(Workbook("<row r=\"1\"><c r=\"A1\"><f>1+1</f><v>2</v></c></row>"), "公式");
        Reject(Workbook("<row r=\"1\"><c r=\"A1\" t=\"s\"><v>99</v></c></row>", "text"), "索引");
        Reject(Workbook("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>" + new string('x', 32768) + "</t></is></c></row>"), "文本");
        Reject(Workbook("", target: "https://never-contact.invalid/sheet.xml", external: true), "引用");
        Reject(Workbook("", target: "../sheet.xml"), "引用");
        using (var hostile = new MemoryStream(Workbook("", workbook: "<!DOCTYPE a [<!ENTITY x SYSTEM 'file:///never-read'>]><a>&x;</a>")))
            Expect<XmlException>(() => EvaluationSpreadsheetReader.Read(hostile));
        using (var hostile = new MemoryStream(Workbook("", sheetPrefix: "<!DOCTYPE worksheet [<!ENTITY x 'expanded'>]>")))
            Expect<XmlException>(() => EvaluationSpreadsheetReader.Read(hostile));

        var references = string.Concat(Enumerable.Range(1, 322).Select(row => $"<row r=\"{row}\"><c r=\"A{row}\" t=\"s\"><v>0</v></c></row>"));
        Reject(Workbook(references, new string('中', 32767)), "文本总量");
        using (var oversized = new MemoryStream())
        {
            using (var zip = new ZipArchive(oversized, ZipArchiveMode.Create, true))
            {
                using var entry = zip.CreateEntry("large").Open();
                entry.Write(new byte[21 * 1024 * 1024]);
            }
            Reject(oversized.ToArray(), "解压");
        }
        using (var duplicates = new MemoryStream())
        {
            using (var zip = new ZipArchive(duplicates, ZipArchiveMode.Create, true))
            {
                zip.CreateEntry("xl/workbook.xml");
                zip.CreateEntry("xl/workbook.xml");
            }
            Reject(duplicates.ToArray(), "重复");
        }
        // Sparse coordinates retain physical line numbers rather than collapsing empty rows.
        using var sparse = new MemoryStream(Workbook("<row r=\"1\"><c r=\"A1\"><v>1</v></c></row><row r=\"4\"><c r=\"A4\"><v>4</v></c></row>"));
        var sparseRows = EvaluationSpreadsheetReader.Read(sparse);
        Check(sparseRows.Count == 4 && sparseRows[1].Count == 0 && sparseRows[3][0] == "4", "XLSX physical row numbers lost");
    }

    private static void NonSeekableInputBudget()
    {
        using var allowed = new NonSeekableStream(EvaluationBiffFixture.Bytes());
        Check(EvaluationSpreadsheetReader.Read(allowed).Count == 4, "nonseekable BIFF input rejected");
        using var oversized = new NonSeekableStream(new byte[EvaluationSpreadsheetReader.MaxInputBytes + 1]);
        Expect<ArgumentException>(() => EvaluationSpreadsheetReader.Read(oversized), "5MiB");
    }

    private static byte[] Workbook(string rows, string? shared = null, string target = "worksheets/sheet1.xml", bool external = false,
        string? workbook = null, string sheetPrefix = "")
    {
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            void Add(string path, string text) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(text); }
            Add("xl/workbook.xml", workbook ?? $"<workbook xmlns=\"{ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"记录\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add("xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"{target}\"{(external ? " TargetMode=\"External\"" : "")}/></Relationships>");
            Add("xl/worksheets/sheet1.xml", $"{sheetPrefix}<worksheet xmlns=\"{ns}\"><sheetData>{rows}</sheetData></worksheet>");
            if (shared is not null) Add("xl/sharedStrings.xml", $"<sst xmlns=\"{ns}\"><si><t>{shared}</t></si></sst>");
        }
        return buffer.ToArray();
    }

    // Minimal native BIFF8 records for precise boundary/corruption tests. The golden OLE fixture above
    // is independently produced by xlwt; these are not used to prove normal Excel interoperability.
    private static byte[] RawBiff(int rows, int column = 0, string text = "value", bool encrypted = false,
        ushort version = 0x0600, byte[]? sheetRecords = null)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.Unicode, true);
        void Record(ushort id, Action<BinaryWriter> write)
        {
            using var data = new MemoryStream();
            using (var record = new BinaryWriter(data, Encoding.Unicode, true)) write(record);
            writer.Write(id); writer.Write((ushort)data.Length); writer.Write(data.ToArray());
        }
        void Bof(ushort kind) => Record(0x0809, record =>
        { record.Write(version); record.Write(kind); record.Write((ushort)0x0dbb); record.Write((ushort)0x07cc); record.Write(0); record.Write(6); });
        Bof(0x0005);
        if (encrypted) Record(0x002f, record => { record.Write((ushort)0); record.Write((ushort)0); record.Write((ushort)0); });
        Record(0x0042, record => record.Write((ushort)(version == 0x0600 ? 1200 : 1252)));
        var boundOffset = buffer.Position + 4;
        Record(0x0085, record =>
        {
            record.Write(0); record.Write((ushort)0); record.Write((byte)1);
            if (version == 0x0600) record.Write((byte)0);
            record.Write((byte)'S');
        });
        if (version == 0x0600)
        {
            var firstCount = Math.Min(7989, text.Length);
            Record(0x00fc, record =>
            { record.Write(rows); record.Write(1); record.Write((ushort)text.Length); record.Write((byte)0); record.Write(Encoding.ASCII.GetBytes(text[..firstCount])); });
            for (var offset = firstCount; offset < text.Length; offset += 7999)
            {
                var chunk = text.Substring(offset, Math.Min(7999, text.Length - offset));
                Record(0x003c, record => { record.Write((byte)0); record.Write(Encoding.ASCII.GetBytes(chunk)); });
            }
        }
        Record(0x000a, _ => { });
        var sheetOffset = (int)buffer.Position;
        Bof(0x0010);
        Record(0x0200, record =>
        {
            if (version == 0x0600) { record.Write(0); record.Write(rows); }
            else { record.Write((ushort)0); record.Write((ushort)rows); }
            record.Write((ushort)0); record.Write((ushort)(column + 1)); record.Write((ushort)0);
        });
        for (var row = 0; row < rows; row++)
        {
            if (version == 0x0600)
                Record(0x00fd, record => { record.Write((ushort)row); record.Write((ushort)column); record.Write((ushort)0); record.Write(0); });
            else
                Record(0x0204, record => { record.Write((ushort)row); record.Write((ushort)column); record.Write((ushort)0); record.Write((ushort)text.Length); record.Write(Encoding.ASCII.GetBytes(text)); });
        }
        if (sheetRecords is not null) writer.Write(sheetRecords);
        Record(0x000a, _ => { });
        var bytes = buffer.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan((int)boundOffset, 4), sheetOffset);
        return bytes;
    }

    private static byte[] FormulaRecords(string kind, ushort version)
    {
        var expression = kind is "shared" or "array" ? new byte[] { 0x01, 1, 0, 0, 0 } : new byte[] { 0x1e, 0, 0 };
        var formula = BiffRecord(0x0006, writer =>
        {
            writer.Write((ushort)1); writer.Write((ushort)0); writer.Write((ushort)0);
            if (kind == "string") writer.Write(new byte[] { 0, 0, 0, 0, 0, 0, 0xff, 0xff });
            else writer.Write(100d);
            writer.Write((ushort)(kind == "shared" ? 8 : 0)); writer.Write(0);
            writer.Write((ushort)expression.Length); writer.Write(expression);
        });
        byte[] continuation = kind switch
        {
            "string" => BiffRecord(0x0207, writer =>
            { writer.Write((ushort)3); if (version == 0x0600) writer.Write((byte)0); writer.Write(Encoding.ASCII.GetBytes("100")); }),
            "shared" => BiffRecord(0x04bc, writer =>
            { Range(writer); writer.Write((byte)0); writer.Write((byte)1); writer.Write((ushort)3); writer.Write(new byte[] { 0x1e, 0, 0 }); }),
            "array" => BiffRecord(0x0221, writer =>
            { Range(writer); writer.Write((ushort)0); writer.Write(0); writer.Write((ushort)3); writer.Write(new byte[] { 0x1e, 0, 0 }); }),
            _ => [],
        };
        return formula.Concat(continuation).ToArray();
        static void Range(BinaryWriter writer) { writer.Write((ushort)1); writer.Write((ushort)1); writer.Write((byte)0); writer.Write((byte)0); }
    }

    private static byte[] BiffRecord(ushort id, Action<BinaryWriter> write)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.Unicode, true);
        writer.Write(id); writer.Write((ushort)0); write(writer);
        var bytes = buffer.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), checked((ushort)(bytes.Length - 4)));
        return bytes;
    }

    private static int RecordOffset(byte[] bytes, ushort id)
    {
        for (var offset = 0; offset + 4 <= bytes.Length; offset += 4 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 2)))
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset)) == id) return offset;
        throw new InvalidOperationException("Fixture record not found: " + id.ToString("X4"));
    }

    private static void Reject(byte[] bytes, string message)
    {
        using var stream = new MemoryStream(bytes);
        Expect<ArgumentException>(() => EvaluationSpreadsheetReader.Read(stream), message);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action, string? message = null) where T : Exception
    {
        try { action(); }
        catch (T error)
        {
            Check(message is null || error.Message.Contains(message), $"Expected {message}; got {error.Message}");
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
