using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MachineConnectionApi.Tests;

internal static class EvaluationBiffFixture
{
    // Genuine OLE/BIFF8 workbook generated once with xlwt 1.3.0 (not an XLSX renamed to XLS).
    // Sheet: Chinese headers; row 2 has a styled date, numeric zero and an empty score;
    // physical row 3 is blank; row 4 has an invalid date. Gzip only keeps the source fixture small.
    public static byte[] Bytes() => Decompress(Data);

    // The same xlwt OLE workbook with K2 replaced at a BIFF record boundary by FORMULA
    // (ptgInt 0), deliberately stale numeric/string cache 100. Workbook padding is shortened
    // accordingly; the compound directory/FAT and every BOUNDSHEET offset remain intact.
    public static byte[] CachedFormulaBytes(bool stringCache = false)
    {
        var bytes = Decompress(NumericFormulaData);
        if (stringCache)
        {
            // This golden file's Workbook occupies [512,4608); FORMULA is at 2535, length 25.
            new byte[] { 0, 0, 0, 0, 0, 0, 0xff, 0xff }.CopyTo(bytes, 2545);
            Array.Copy(bytes, 2564, bytes, 2574, 4608 - 2574);
            new byte[] { 0x07, 0x02, 6, 0, 3, 0, 0, (byte)'1', (byte)'0', (byte)'0' }.CopyTo(bytes, 2564);
        }
        return bytes;
    }

    // Small independent CFB fixture writer. Reverses physical stream sectors (and mini sectors)
    // so tests exercise chains rather than accidentally assuming a contiguous Workbook stream.
    public static byte[] WrapWorkbook(byte[] workbook, bool miniStream = false, int sectorSize = 512)
    {
        if (miniStream && workbook.Length >= 4096) throw new ArgumentException("Mini fixture must be smaller than 4096 bytes.");
        var streamLength = miniStream ? (workbook.Length + 63) / 64 * 64 : Math.Max(4096, workbook.Length);
        var streamSectorCount = (streamLength + sectorSize - 1) / sectorSize;
        var firstStreamSector = miniStream ? 3 : 2; // FAT, directory, optional MiniFAT, then stream.
        var sectorCount = firstStreamSector + streamSectorCount;
        if (sectorCount > sectorSize / 4) throw new ArgumentException("Fixture uses a single FAT sector.");
        var file = new byte[(sectorCount + 1) * sectorSize];
        void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(offset), value);
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(offset), value);
        new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }.CopyTo(file, 0);
        U16(24, 0x003e); U16(26, (ushort)(sectorSize == 512 ? 3 : 4)); U16(28, 0xfffe);
        U16(30, (ushort)(sectorSize == 512 ? 9 : 12)); U16(32, 6);
        U32(40, sectorSize == 512 ? 0u : 1u); U32(44, 1); U32(48, 1); U32(56, 4096);
        U32(60, miniStream ? 2u : 0xfffffffe); U32(64, miniStream ? 1u : 0u); U32(68, 0xfffffffe);
        for (var index = 0; index < 109; index++) U32(76 + index * 4, index == 0 ? 0u : uint.MaxValue);
        for (var index = 0; index < sectorSize / 4; index++) U32(sectorSize + index * 4, uint.MaxValue);
        U32(sectorSize, 0xfffffffd); U32(sectorSize + 4, 0xfffffffe);
        if (miniStream) U32(sectorSize + 8, 0xfffffffe);
        var storage = new byte[streamLength];
        var miniCount = streamLength / 64;
        if (miniStream)
        {
            var miniFat = 3 * sectorSize;
            for (var index = 0; index < sectorSize / 4; index++) U32(miniFat + index * 4, uint.MaxValue);
            for (var index = 0; index < miniCount; index++)
            {
                var physical = miniCount - index - 1;
                Array.Copy(workbook, index * 64, storage, physical * 64, Math.Min(64, workbook.Length - index * 64));
                U32(miniFat + physical * 4, physical == 0 ? 0xfffffffe : (uint)(physical - 1));
            }
        }
        else workbook.CopyTo(storage, 0);
        for (var index = 0; index < streamSectorCount; index++)
        {
            var physical = firstStreamSector + streamSectorCount - index - 1;
            Array.Copy(storage, index * sectorSize, file, (physical + 1) * sectorSize, Math.Min(sectorSize, streamLength - index * sectorSize));
            U32(sectorSize + physical * 4, index == streamSectorCount - 1 ? 0xfffffffe : (uint)(physical - 1));
        }
        var first = (uint)(sectorCount - 1);
        void DirectoryEntry(int slot, string name, byte type, uint start, uint size, uint child)
        {
            var entry = 2 * sectorSize + slot * 128;
            Encoding.Unicode.GetBytes(name + "\0").CopyTo(file, entry);
            U16(entry + 64, (ushort)((name.Length + 1) * 2));
            file[entry + 66] = type; file[entry + 67] = 1;
            U32(entry + 68, uint.MaxValue); U32(entry + 72, uint.MaxValue); U32(entry + 76, child);
            U32(entry + 116, start); U32(entry + 120, size);
        }
        DirectoryEntry(0, "Root Entry", 5, miniStream ? first : 0xfffffffe, miniStream ? (uint)streamLength : 0, 1);
        DirectoryEntry(1, "Workbook", 2, miniStream ? (uint)(miniCount - 1) : first,
            miniStream ? (uint)workbook.Length : (uint)streamLength, uint.MaxValue);
        return file;
    }

    private static byte[] Decompress(string data)
    {
        using var compressed = new MemoryStream(Convert.FromBase64String(data));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private const string NumericFormulaData = """
H4sIAAAAAAAC/+1Ya2gUVxQ+d+7uzu5md7OJSWpSiEEwvrKwpiA1oBvFRqRksWs00YgxiWuMjxgWsUQlmtogmJQWxEdFhEBK2z82/ml9/VDwhz8ExSCCIiaWgiiUtlgtqJmec+buZddGMFAFdc7snDnfOec+vrlz752da1fzRgZOlYzCC7IAJIxZPvBk+ASevjQIA8Yti8z01Yun5chbJT4vDqTHDeeCV0waQxrvUTBgyHURNcA9PNdAJ8S3dSTL3qAs4j40C+rDfNQCjqMnBMXcq3zWrawnsf6JM8+zrmbPV6znY+6IWA1XY/FZH6unuMGYyrEQUL2/cJlb7JkDhXCZnuI93wg71w0LU+3NW96yQKkrAIOAA7ok2ZFMEc6F78APsAolUlsbWbx4BApwsAfhb6sM4FF6Vl8oc/xv1i8A/f9k+82X+L04iC/6DxougL1greNJ0ovr8go3RVziUt+T+FDfiqZnsElMQ08JeUFUtR1YG6z7o0uZRXueLFdmXz2bncnvW5/uvtuN5uPDj+Mql8yuVE8Tmp9uvdEXrT/TqczfklPalHk23lCHJrW8a/fsRIBMT6IK2qEJ51YVPOj5IXH/kNlqbszJDFViqGJXf7zr6O87QnZNw93H28Yrh1Waq+5vGS/kBtEfv/313ARxwb1o0dKamkg0Ogc7RMwi8f1J1U1iJ21KC2kN+GtDyxHEJxr7jw02SjunoQ6nz9bm1o3tHUmcXFFsmGqgxgOW3c6Uz080Uj9xXlVGK+dG5s3Dn582SV5Uw1mLapAXmwDq9ZDLdh4vOWEc1Wc//nm9tmVZrIk9e3kjtbfbaTS6YEEPlcDCIY64WJM9i0vMZv0F1/oh2yWsC3A9wGv5skJl1OzjnC85Wo7tfMQyHJueYc9Au/fhZ6dLe3+NzUT75JLRnQUnb8YGYCpu/+uxPB37oEJUiGPfkpyJpa9CLc13WBf/Z5n2GmHVd0u9U+TCc1qYUPJY24jujtDIUPfKRhKR1MiFyKWRG5FbIw8ij0YmIlMjLyKvRj5EPo38iPwa5SDK0SiAKKBREFFQ8RLj8BLMK6TyBfPK1Yh4hTUiXnkaEa98jYjXJI2IVwGiboOQiXkrhw0VI16FOpN4FfGdKEbkV/cY94Fq+yqhNJ1KxD7QBYnYZEVMjkNMZhGTWcRkFjGZRUxmEZNZxKQmZiMasGKNMolJTYxugWRiho5lMpGayQIjH36mJKjOeKf1gyOOOOKII468fyL49Qp4P3fxC5T9umqq7zrP8RxzPpO8s5KAbXhsxz+Vn0AHXlPQNaHnpwjcIl2XeMUy6e+FJPXYego2Qwv3Y/OEn1/8uyEy+bxywfD/N4Um2v7YRPr5mtv/F0PJLrMAFgAA
""";

    private const string Data = """
H4sIAAAAAAAC/+1YXWgUVxQ+d+/uzu5md7OJSWoiaBDUVrOwRhAN6FbRiEgWXaOJRoxJ3Mb4E8MilqhEUw2CiSiItooIQkrti9WX1r8HhT74ICgNIrSIiSKIgtjSakHNeM6Zu9cdjWBABXXO7Jw537nn/nxz5547O9eu5vUfP10yAK/ITJAwaPrBm+UTePozIAJYbppkZq4+PE1HPirx+3AivR44H7pi0BzSfA+AC065L6EGuI3nCmiDxMbWVOkHlNk8hgZBY5iBWsBR9IShmEeVz7qJ9QjWv3DkBdZfs2cv6xkY2y+Ww9V4YuI09RTXusZyWRio3d+4zp/smQyFcJme4u37hRXrgVnplob1H1nBaHcQegEndF6qNZUmnAs/QgBgGUq0qio6Z04/FOBk98J/ZinAv5lVfbHU8X9YvwD0/2/3G2/w+3ASX/UfcLkBdoC5ihdJF+blJR4qcYvfu58kTnUvqX8Ga8U49JSQF0RF856VoepH7cos2v5ksTK7a9hsS/3U9HTbrQ40Hx96nFCxZLanO+vRXLDhenes5mybMu+mxjQr81yithpN6nnrtknJIJneZAW0QD2urQq433kiee+g0WSsyckuKseisq09ifYfHm4OWy31dRxtHqoeNmksu7d+qCIPiJ7EX/umJokL7kWz51dWRmOxyTggYhZN7E6pYRI7aVGaRTngn28av0d8rK7nSG+dtGJqq3H5bGhoWtPSmsLFFcOOqQXqPGha/Yz59lgdjRPXVXmsfGp0+nT8BWiT5KQasSXVECebIOrVkMt2HqecCM7qs5///qOqcWG8nj07eCO1tttxNLtgQifVwMphLnGzJnsi15jE+jtudRTbJawLMB/gdfzCQmVU7uSYXVw6HvuZwtIXn5Blf4l214NFZ0Z33Yl/hfbJeQNbCk7eiB+Hsbj9r8b6dOyEMlEmjhwmORvPXIVKzTdZF7+Wpn2uiBq7qd4pcuE5JSaUPNYWorsjNHKpe2UhiUhq5Ebk1siDyKORF5FXIwORoZEPkU8jPyK/RgFEAY1yEOVoFEQU1CiEKKR4iSF4CeYVVvGCeeVqRLwiGhGvPI2IV75GxGuERsSrAFGHi5CBcUv7XKqMeBXqSOJVpCMDWfdRMJMvNCImIxUTOQQTaWMibUykjYm0MZE2JtLGRGomFqIZKtYom4m0MZE2JtLGRGomM1358CsF4QvBSwmAI4444ogjjnz2Ivj1Cnh7d/MLlPW6aqjvOs/xHHQ+k3yykoSNeGzCP5VzoRWvaWgf1vNTBB6RaUu8ZZ3M90KSGuw9DeugkcexbtjPL/7dENl83rpi5N0toeH2Pziccb7n/l8AParEZgAWAAA=
""";
}
