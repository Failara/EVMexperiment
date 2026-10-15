using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;
using EvmMatching.Core;

namespace EvmMatching.Data;

public static class Files
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public static string Sha(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    public static void SaveJson(string path, object value) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); File.WriteAllText(path, JsonSerializer.Serialize(value, Json)); }
    public static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException(path);
    public static void WriteAddresses(string path, IEnumerable<Address20> addresses)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path); Span<byte> buffer = stackalloc byte[20];
        foreach (var a in addresses) { a.Write(buffer); stream.Write(buffer); }
    }
    public static Address20[] ReadAddresses(string path)
    {
        var bytes = File.ReadAllBytes(path); if (bytes.Length % 20 != 0) throw new InvalidDataException("Truncated watchlist.");
        var result = new Address20[bytes.Length / 20]; for (int i = 0; i < result.Length; i++) result[i] = new(bytes.AsSpan(i * 20, 20)); return result;
    }
    public static void WriteQueries(string path, IEnumerable<Query> queries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        Stream output = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionLevel.SmallestSize, leaveOpen: true)
            : file;
        using var writer = new BinaryWriter(output); Span<byte> buffer = stackalloc byte[20];
        foreach (var q in queries) { writer.Write(q.Sequence); writer.Write(q.EventId); q.Address.Write(buffer); writer.Write(buffer); }
    }
    public static Query[] ReadQueries(string path)
    {
        using var file = File.OpenRead(path);
        Stream input = file;
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            if (file.Length < 18) throw new InvalidDataException("Truncated compressed query file.");
            file.Position = file.Length - sizeof(uint);
            using var trailer = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true);
            uint uncompressedLength = trailer.ReadUInt32();
            if (uncompressedLength == 0 || uncompressedLength % 36 != 0) throw new InvalidDataException("Invalid compressed query length.");
            file.Position = 0;
            input = new GZipStream(file, CompressionMode.Decompress, leaveOpen: true);
            var result = new Query[checked((int)(uncompressedLength / 36))];
            using var compressedReader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
            ReadQueryRecords(compressedReader, result);
            if (compressedReader.BaseStream.ReadByte() != -1) throw new InvalidDataException("Unexpected data in compressed query file.");
            return result;
        }

        if (file.Length % 36 != 0) throw new InvalidDataException("Truncated query file.");
        var plainResult = new Query[checked((int)(file.Length / 36))];
        using var reader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
        ReadQueryRecords(reader, plainResult);
        return plainResult;
    }
    private static void ReadQueryRecords(BinaryReader reader, Query[] result)
    {
        for (int i = 0; i < result.Length; i++) { long seq = reader.ReadInt64(), evt = reader.ReadInt64(); result[i] = new(seq, new(reader.ReadBytes(20)), evt); }
        for (int i = 1; i < result.Length; i++) if (result[i].Sequence <= result[i - 1].Sequence) throw new InvalidDataException("Non-monotonic query sequence.");
    }
    public static Update[] ReadUpdates(string path)
    {
        var result = File.ReadLines(path).Skip(1).Where(s => s.Length > 0).Select(line =>
        {
            var p = line.Split(','); if (p.Length != 4) throw new InvalidDataException("Invalid update row.");
            var kind = Enum.Parse<UpdateKind>(p[2]); if (!Enum.IsDefined(kind)) throw new InvalidDataException("Invalid update operation.");
            return new Update(long.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), kind, Address20.Parse(p[3]), long.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));
        }).ToArray();
        for (int i = 1; i < result.Length; i++) if (result[i].Sequence < result[i - 1].Sequence || result[i].TimestampNs < result[i - 1].TimestampNs) throw new InvalidDataException("Non-monotonic updates.");
        return result;
    }
    public static void Verify(string path, string expected)
    {
        if (expected.Length != 64 || !Sha(path).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("hash_mismatch: " + path);
    }
}
