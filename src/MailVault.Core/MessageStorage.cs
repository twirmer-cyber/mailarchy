using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using MimeKit;
using ZstdSharp;

using MailVault.Core.Localization;

namespace MailVault.Core;

/// <summary>
/// Speicherformat der Originalnachrichten: Single-Instance-Speicherung großer MIME-Teile plus Kompression
/// mit einem je Archiv trainierten Wörterbuch.
///
/// Wie im Original (Methodennamen "...ApplySingleInstance", "ShouldCompressMimeEntity", per REA gefunden)
/// werden große base64-Teile (Anhänge, eingebettete Bilder) dekodiert, per SHA-256 identifiziert und nur einmal
/// gespeichert. Die Nachricht selbst wird als "Skelett" abgelegt: Kopfzeilen, Texte und MIME-Struktur, in dem
/// diese Teile durch Verweise ersetzt sind. Skelette werden mit zstd und einem Wörterbuch komprimiert, das aus
/// den Nachrichten des Archivs gelernt wird (wiederkehrende Kopfzeilen, Signaturen, HTML-Vorlagen).
///
/// Die Wiederherstellung ist byte-genau: Ein Teil wird nur ausgelagert, wenn die Neukodierung (Zeilenlänge,
/// Zeilenende, abschließende Leerzeilen) exakt die Originalbytes ergibt. Andernfalls bleibt er im Skelett.
/// </summary>
internal static class MessageStorage
{
    public const int FormatLegacy = 1;      // ganze Nachricht, Brotli
    public const int FormatBrotliSkeleton = 2;
    public const int FormatZstdSkeleton = 3;

    public const int PartRaw = 0, PartBrotli = 1, PartZstd = 2;

    /// <summary>Ab dieser Größe wird ein base64-Teil überhaupt dekodiert.</summary>
    const int MinPartBytes = 256;
    /// <summary>Ab dieser Größe werden Nicht-Text-Teile ausgelagert und dedupliziert (darunter überwiegt der Verwaltungsaufwand).</summary>
    const int MinExternalBytes = 8 * 1024;

    public static int SkeletonLevel = 9;
    public static int PartLevel = 9;

    public sealed record Part(byte[] Hash, byte[] Decoded);

    /// <summary>Unkomprimiertes Skelett und die ausgelagerten Teile.</summary>
    public sealed record Encoded(byte[] Skeleton, IReadOnlyList<Part> Parts);

    public static Encoded Encode(byte[] raw)
    {
        var parts = new List<Part>();
        using var skel = new MemoryStream(raw.Length / 2);
        var w = new BinaryWriter(skel);
        var pos = 0;
        foreach (var (start, end, isText) in FindBase64Leaves(raw))
        {
            if (!TryCanonicalBase64(raw.AsSpan(start, end - start), out var decoded, out var layout)) continue;
            WriteLiteral(w, raw, pos, start - pos);
            if (isText || decoded.Length < MinExternalBytes)
            {
                // Texte und kleine Teile dekodiert im Skelett: dort greift das Wörterbuch.
                w.Write((byte)2);
                w.Write7BitEncodedInt(decoded.Length);
                w.Write(decoded);
            }
            else
            {
                // Große Dateien (Anhänge, Bilder) nur einmal im Archiv speichern.
                var hash = SHA256.HashData(decoded);
                w.Write((byte)1);
                w.Write(hash);
                parts.Add(new Part(hash, decoded));
            }
            w.Write7BitEncodedInt(layout.LineLength);
            w.Write(layout.Crlf);
            w.Write7BitEncodedInt(layout.TrailingEols);
            pos = end;
        }
        WriteLiteral(w, raw, pos, raw.Length - pos);
        w.Flush();
        return new Encoded(skel.ToArray(), parts);
    }

    /// <summary>Setzt die Originalnachricht aus dem unkomprimierten Skelett und den Teilen zusammen.</summary>
    public static byte[] Assemble(byte[] skeleton, Func<byte[], byte[]> loadPart)
    {
        var r = new BinaryReader(new MemoryStream(skeleton));
        using var output = new MemoryStream(skeleton.Length * 2);
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            var type = r.ReadByte();
            if (type == 0)
            {
                var len = r.Read7BitEncodedInt();
                output.Write(skeleton, (int)r.BaseStream.Position, len);
                r.BaseStream.Position += len;
            }
            else if (type == 1)
            {
                var hash = r.ReadBytes(32);
                var layout = new Base64Layout(r.Read7BitEncodedInt(), r.ReadBoolean(), r.Read7BitEncodedInt());
                WriteBase64(output, loadPart(hash), layout);
            }
            else if (type == 2)
            {
                var decoded = r.ReadBytes(r.Read7BitEncodedInt());
                var layout = new Base64Layout(r.Read7BitEncodedInt(), r.ReadBoolean(), r.Read7BitEncodedInt());
                WriteBase64(output, decoded, layout);
            }
            else throw new InvalidDataException(CoreText.T("Storage_UnknownSegment"));
        }
        return output.ToArray();
    }

    static void WriteLiteral(BinaryWriter w, byte[] raw, int offset, int count)
    {
        if (count <= 0) return;
        w.Write((byte)0);
        w.Write7BitEncodedInt(count);
        w.Write(raw, offset, count);
    }

    /// <summary>Byte-Bereiche der Inhalte aller base64-kodierten Blatt-Teile (ohne Überlappung, aufsteigend).</summary>
    static List<(int Start, int End, bool IsText)> FindBase64Leaves(byte[] raw)
    {
        var result = new List<(int, int, bool)>();
        try
        {
            var parser = new MimeParser(new MemoryStream(raw, writable: false), MimeFormat.Entity);
            parser.MimeEntityEnd += (_, e) =>
            {
                if (e.Entity is MimePart { ContentTransferEncoding: ContentEncoding.Base64 } part &&
                    e.EndOffset - e.HeadersEndOffset >= MinPartBytes)
                {
                    var isText = part.ContentType.IsMimeType("text", "*") && !part.IsAttachment;
                    result.Add(((int)e.HeadersEndOffset, (int)e.EndOffset, isText));
                }
            };
            parser.ParseEntity();
        }
        catch (FormatException) { return new(); }
        result.Sort();
        var clean = new List<(int, int, bool)>();
        var last = 0;
        foreach (var (s, e, t) in result)
            if (s >= last && e <= raw.Length && e > s) { clean.Add((s, e, t)); last = e; }
        return clean;
    }

    readonly record struct Base64Layout(int LineLength, bool Crlf, int TrailingEols);

    /// <summary>
    /// Prüft, ob der Bereich exakt "base64-Zeilen fester Länge + Zeilenenden" ist, und liefert die dekodierten Bytes.
    /// </summary>
    static bool TryCanonicalBase64(ReadOnlySpan<byte> s, out byte[] decoded, out Base64Layout layout)
    {
        decoded = Array.Empty<byte>();
        layout = default;
        var crlf = s.IndexOf("\r\n"u8) >= 0;
        ReadOnlySpan<byte> eol = crlf ? "\r\n"u8 : "\n"u8;

        var end = s.Length;
        var trailing = 0;
        while (end >= eol.Length && s[(end - eol.Length)..end].SequenceEqual(eol)) { end -= eol.Length; trailing++; }
        var body = s[..end];
        if (body.Length == 0) return false;

        var firstEol = body.IndexOf(eol);
        var lineLength = firstEol < 0 ? body.Length : firstEol;
        if (lineLength == 0 || (firstEol >= 0 && lineLength % 4 != 0)) return false;

        var compact = ArrayPool<byte>.Shared.Rent(body.Length);
        try
        {
            var n = 0;
            var rest = body;
            while (true)
            {
                var i = rest.IndexOf(eol);
                var line = i < 0 ? rest : rest[..i];
                if (i >= 0 && line.Length != lineLength) return false;
                if (i < 0 && (line.Length == 0 || line.Length > lineLength)) return false;
                foreach (var b in line)
                    if (!(b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'/' or (byte)'='))
                        return false;
                line.CopyTo(compact.AsSpan(n));
                n += line.Length;
                if (i < 0) break;
                rest = rest[(i + eol.Length)..];
            }
            if (n % 4 != 0) return false;
            var chars = System.Text.Encoding.ASCII.GetString(compact, 0, n);
            try { decoded = Convert.FromBase64String(chars); }
            catch (FormatException) { return false; }
            if (Convert.ToBase64String(decoded) != chars) return false;
        }
        finally { ArrayPool<byte>.Shared.Return(compact); }

        layout = new Base64Layout(lineLength, crlf, trailing);
        using var check = new MemoryStream(s.Length);
        WriteBase64(check, decoded, layout);
        return check.Length == s.Length && check.GetBuffer().AsSpan(0, s.Length).SequenceEqual(s);
    }

    static void WriteBase64(Stream output, byte[] decoded, Base64Layout layout)
    {
        var text = System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(decoded));
        ReadOnlySpan<byte> eol = layout.Crlf ? "\r\n"u8 : "\n"u8;
        for (var i = 0; i < text.Length; i += layout.LineLength)
        {
            if (i > 0) output.Write(eol);
            output.Write(text, i, Math.Min(layout.LineLength, text.Length - i));
        }
        for (var t = 0; t < layout.TrailingEols; t++) output.Write(eol);
    }

    // ------------------------------------------------------------------ Skelette (zstd + Wörterbuch)

    public static byte[] CompressSkeleton(byte[] skeleton, int dictId, byte[]? dict)
    {
        using var c = new Compressor(SkeletonLevel);
        if (dict is not null) c.LoadDictionary(dict);
        var frame = c.Wrap(skeleton);
        using var ms = new MemoryStream(frame.Length + 5);
        var w = new BinaryWriter(ms);
        w.Write7BitEncodedInt(dictId);
        w.Write(frame);
        return ms.ToArray();
    }

    public static byte[] DecompressSkeleton(byte[] stored, Func<int, byte[]> getDict)
    {
        var r = new BinaryReader(new MemoryStream(stored));
        var dictId = r.Read7BitEncodedInt();
        var offset = (int)r.BaseStream.Position;
        using var d = new Decompressor();
        if (dictId != 0) d.LoadDictionary(getDict(dictId));
        return d.Unwrap(stored.AsSpan(offset)).ToArray();
    }

    public static int SkeletonDictId(byte[] stored) => new BinaryReader(new MemoryStream(stored)).Read7BitEncodedInt();

    /// <summary>Trainiert ein Wörterbuch aus Beispiel-Skeletten; null, wenn die Stichprobe nicht reicht.</summary>
    public static byte[]? TrainDictionary(IReadOnlyList<byte[]> samples)
    {
        var total = samples.Sum(s => (long)s.Length);
        var size = (int)Math.Min(112 * 1024, total / 20);
        if (samples.Count < 50 || size < 8 * 1024) return null;
        try { return DictBuilder.TrainFromBuffer(samples, size).ToArray(); }
        catch (ZstdException) { return null; }
    }

    // ------------------------------------------------------------------ Teile

    /// <summary>Speicherform eines Teils: komprimiert, wenn das etwas bringt (JPEG, ZIP, DOCX usw. bleiben roh).</summary>
    public static (byte[] Data, int Codec) PackPart(byte[] decoded)
    {
        var sample = decoded.AsSpan(0, Math.Min(decoded.Length, 64 * 1024));
        using (var probe = new Compressor(1))
            if (probe.Wrap(sample).Length > sample.Length * 0.92) return (decoded, PartRaw);
        using var c = new Compressor(PartLevel);
        var packed = c.Wrap(decoded).ToArray();
        return packed.Length < decoded.Length * 0.95 ? (packed, PartZstd) : (decoded, PartRaw);
    }

    public static byte[] UnpackPart(byte[] data, int codec) => codec switch
    {
        PartRaw => data,
        PartBrotli => Unbrotli(data),
        PartZstd => Unzstd(data),
        _ => throw new InvalidDataException(CoreText.T("Storage_UnknownCompression")),
    };

    static byte[] Unzstd(byte[] data)
    {
        using var d = new Decompressor();
        return d.Unwrap(data).ToArray();
    }

    public static byte[] Unbrotli(byte[] data)
    {
        using var input = new BrotliStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream(data.Length * 4);
        input.CopyTo(ms);
        return ms.ToArray();
    }
}
