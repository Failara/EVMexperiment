namespace EvmMatching.Runner;

// Fixed storage; buckets have 1/16-octave resolution. Percentiles are upper bounds.
internal sealed class Histogram
{
    private readonly long[] buckets = new long[1024];
    public long Count { get; private set; }
    public void Add(long ns) { int i = ns <= 0 ? 0 : Math.Min(1023, (int)Math.Ceiling(Math.Log2(ns + 1) * 16)); buckets[i]++; Count++; }
    public void Merge(Histogram other) { for (int i = 0; i < buckets.Length; i++) buckets[i] += other.buckets[i]; Count += other.Count; }
    public long Percentile(double p) { long rank = (long)Math.Ceiling(Count * p), sum = 0; if (rank == 0) return 0; for (int i = 0; i < buckets.Length; i++) { sum += buckets[i]; if (sum >= rank) return (long)Math.Ceiling(Math.Pow(2, i / 16.0) - 1); } return long.MaxValue; }
}
