namespace EvmMatching.Core;

// Counting Bloom is deliberately used instead of Cuckoo: deletion is guarded by H.
// Each of k positions is counted, including coincident positions. Overflow is fatal.
public sealed class CountingBloom
{
    private readonly uint[] counters;
    private readonly int capacity;
    private int count;
    private const int K = 7;
    public long Bytes => counters.LongLength * sizeof(uint);
    public CountingBloom(int capacity)
    {
        if (capacity < 1 || capacity > int.MaxValue / 12) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity; counters = new uint[Math.Max(64, capacity * 12)];
    }
    private int Position(Address20 a, int i) => (int)(a.Hash64((ulong)(i + 1) * 0x9e3779b97f4a7c15UL) % (ulong)counters.Length);
    public void Insert(Address20 a)
    {
        if (count >= capacity) throw new InvalidOperationException("filter_overflow");
        for (int i = 0; i < K; i++) if (counters[Position(a, i)] > uint.MaxValue - K) throw new InvalidOperationException("filter_overflow");
        for (int i = 0; i < K; i++) counters[Position(a, i)]++;
        count++;
    }
    public void Delete(Address20 a)
    {
        for (int i = 0; i < K; i++) counters[Position(a, i)]--;
        count--;
    }
    public bool MightContain(Address20 a)
    {
        for (int i = 0; i < K; i++) if (counters[Position(a, i)] == 0) return false;
        return true;
    }
}

public sealed class StaticBloom
{
    private readonly ulong[] bits;
    public long Bytes => bits.LongLength * 8;
    public StaticBloom(IEnumerable<Address20> addresses, int count)
    {
        bits = new ulong[Math.Max(1, checked((count * 12 + 63) / 64))];
        foreach (var a in addresses) for (int i = 0; i < 7; i++)
        {
            int p = Position(a, i); bits[p >> 6] |= 1UL << (p & 63);
        }
    }
    private int Position(Address20 a, int i) => (int)(a.Hash64((ulong)(i + 1) * 0x9e3779b97f4a7c15UL) % (ulong)(bits.Length * 64));
    public bool MightContain(Address20 a)
    {
        for (int i = 0; i < 7; i++) { int p = Position(a, i); if ((bits[p >> 6] & (1UL << (p & 63))) == 0) return false; }
        return true;
    }
}
