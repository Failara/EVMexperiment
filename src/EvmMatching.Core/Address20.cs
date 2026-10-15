using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace EvmMatching.Core;

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 20)]
public readonly struct Address20 : IEquatable<Address20>
{
    private readonly ulong a;
    private readonly ulong b;
    private readonly uint c;
    public Address20(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 20) throw new ArgumentException("An address must contain 20 bytes.");
        a = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        b = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        c = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
    }
    public static Address20 Parse(string value) => TryParse(value, out var address) ? address : throw new FormatException("Invalid EVM address.");
    public static bool TryParse(string? value, out Address20 address)
    {
        address = default;
        if (value is null) return false;
        var hex = value.AsSpan();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length != 40) return false;
        Span<byte> bytes = stackalloc byte[20];
        for (int i = 0; i < 20; i++)
        {
            int hi = Digit(hex[i * 2]), lo = Digit(hex[i * 2 + 1]);
            if (hi < 0 || lo < 0) return false;
            bytes[i] = (byte)((hi << 4) | lo);
        }
        address = new(bytes); return true;
    }
    private static int Digit(char c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
    public void Write(Span<byte> bytes)
    {
        if (bytes.Length < 20) throw new ArgumentException("Destination too short.");
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, a);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], b);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[16..], c);
    }
    public ulong Hash64(ulong seed = 0)
    {
        ulong x = a ^ Rotate(b, 23) ^ ((ulong)c << 17) ^ seed;
        x ^= x >> 30; x *= 0xbf58476d1ce4e5b9UL; x ^= x >> 27; x *= 0x94d049bb133111ebUL;
        return x ^ (x >> 31);
    }
    private static ulong Rotate(ulong x, int n) => (x << n) | (x >> (64 - n));
    public bool Equals(Address20 other) => a == other.a && b == other.b && c == other.c;
    public override bool Equals(object? obj) => obj is Address20 other && Equals(other);
    public override int GetHashCode() => unchecked((int)Hash64());
    public override string ToString() { Span<byte> bytes = stackalloc byte[20]; Write(bytes); return "0x" + Convert.ToHexStringLower(bytes); }
}

public readonly record struct Query(long Sequence, Address20 Address, long EventId = 0);
public enum UpdateKind { Add, Remove }
public readonly record struct Update(long Sequence, UpdateKind Kind, Address20 Address, long TimestampNs = 0);
public interface IAddressMatcher
{
    void Initialize(ReadOnlySpan<Address20> initialSet);
    bool Contains(Address20 address);
    void Add(Address20 address);
    void Remove(Address20 address);
}
