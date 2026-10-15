using System.Runtime.InteropServices;
using EvmMatching.Core;
using EvmMatching.Data;

namespace EvmMatching.Tests;

public sealed class CorrectnessTests
{
    private static Address20 A(int n) { Span<byte> bytes = stackalloc byte[20]; bytes.Clear(); BitConverter.TryWriteBytes(bytes, n); return new(bytes); }
    [Fact] public void AddressHasExactlyTwentyBytesAndCanonicalEquality()
    {
        Assert.Equal(20, Marshal.SizeOf<Address20>());
        var a = Address20.Parse("0xABCDEF0123456789abcdef0123456789ABCDEF01");
        var b = Address20.Parse("abcdef0123456789ABCDEF0123456789abcdef01");
        Assert.Equal(a, b); Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal("0xabcdef0123456789abcdef0123456789abcdef01", a.ToString());
    }
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("0x01")] [InlineData("gggggggggggggggggggggggggggggggggggggggg")] [InlineData("00000000000000000000000000000000000000000")] [InlineData(" 0000000000000000000000000000000000000000")]
    public void InvalidAddressesAreRejected(string? text) => Assert.False(Address20.TryParse(text, out _));
    [Theory]
    [InlineData("M1")] [InlineData("M2")] [InlineData("M3")] [InlineData("M4")] [InlineData("M5")] [InlineData("M6")]
    public void RandomReplayMatchesExactOracle(string method)
    {
        var random = new Random(240001); var oracle = Enumerable.Range(0, 1000).Select(A).ToHashSet();
        using var matcher = new Matcher(method, 10000, .02); matcher.Initialize(oracle.ToArray());
        for (int i = 0; i < 30000; i++)
        {
            var address = A(random.Next(3000));
            switch (random.Next(4))
            {
                case 0: oracle.Add(address); matcher.Add(address); break;
                case 1: oracle.Remove(address); matcher.Remove(address); break;
                default: Assert.Equal(oracle.Contains(address), matcher.Contains(address)); break;
            }
            if (i % 1000 == 0 && method is "M4" or "M5" or "M6")
            {
                int best = i / 1000 % 3;
                matcher.Control(Enumerable.Range(0, 3).Select(m => new ModeEstimate((SearchMode)m, m == best ? 0 : 1e9, 100, 10)).ToArray(), 1e9);
            }
        }
        matcher.WaitForRebuild(); for (int i = 0; i < 3000; i++) Assert.Equal(oracle.Contains(A(i)), matcher.Contains(A(i)));
    }
    [Fact] public void CountingBloomDeletionPreservesCollidingMembers()
    {
        var filter = new CountingBloom(2000); for (int i = 0; i < 2000; i++) filter.Insert(A(i));
        for (int i = 0; i < 1000; i++) filter.Delete(A(i));
        for (int i = 1000; i < 2000; i++) Assert.True(filter.MightContain(A(i)));
    }
    [Fact] public void OverflowFailsBeforePublishingAddress()
    {
        using var matcher = new Matcher("M2", 1); matcher.Initialize([A(1)]);
        Assert.Contains("filter_overflow", Assert.Throws<InvalidOperationException>(() => matcher.Add(A(2))).Message);
        Assert.False(matcher.Contains(A(2))); Assert.True(matcher.Contains(A(1)));
        matcher.Remove(A(999)); matcher.Add(A(1)); matcher.Remove(A(1)); matcher.Remove(A(1)); matcher.Add(A(2)); Assert.True(matcher.Contains(A(2)));
    }
    [Fact] public async Task SnapshotReconcilesConcurrentUpdatesAndKeepsQueriesSafe()
    {
        using var matcher = new Matcher("M3", 200000, 1); matcher.Initialize(Enumerable.Range(0, 100000).Select(A).ToArray());
        Assert.True(matcher.StartRebuild("test"));
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() => { for (int i = 0; i < 10000; i++) Assert.True(matcher.Contains(A(50000 + i))); })).ToArray();
        for (int i = 0; i < 10000; i++) { matcher.Remove(A(i)); matcher.Add(A(100000 + i)); }
        await Task.WhenAll(readers); matcher.WaitForRebuild();
        for (int i = 0; i < 10000; i++) { Assert.False(matcher.Contains(A(i))); Assert.True(matcher.Contains(A(100000 + i))); }
        matcher.StartRebuild("test_clean_delta"); matcher.WaitForRebuild(); Assert.Equal(0, matcher.DeltaCount);
    }
    [Theory] [InlineData("M1")] [InlineData("M2")] [InlineData("M3")] [InlineData("M4")] [InlineData("M5")] [InlineData("M6")]
    public async Task ParallelQueriesAndWritesPreserveStableMembers(string method)
    {
        using var matcher = new Matcher(method, 10000); matcher.Initialize(Enumerable.Range(0, 1000).Select(A).ToArray());
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() => { for (int i = 0; i < 5000; i++) Assert.True(matcher.Contains(A(i % 1000))); })).ToArray();
        var writer = Task.Run(() => { for (int i = 1000; i < 5000; i++) { matcher.Add(A(i)); matcher.Remove(A(i)); } });
        await Task.WhenAll(readers.Append(writer)); matcher.WaitForRebuild();
        for (int i = 1000; i < 5000; i++) Assert.False(matcher.Contains(A(i)));
    }
    [Fact] public void ContainsDoesNotAllocate()
    {
        using var matcher = new Matcher("M2", 100); matcher.Initialize([A(1)]); var address = A(1);
        for (int i = 0; i < 10000; i++) matcher.Contains(address);
        long before = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 10000; i++) matcher.Contains(address);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
    [Fact] public void HysteresisRequiresSameCandidateInConsecutiveIntervals()
    {
        var controller = new AdaptiveController(new() { HysteresisIntervals = 3 });
        ModeEstimate[] Estimates(int best) => Enumerable.Range(0, 3).Select(m => new ModeEstimate((SearchMode)m, m == best ? 1 : 100, 10, 10)).ToArray();
        Assert.Equal(SearchMode.S0, controller.Decide(Estimates(1), 0)); Assert.Equal(SearchMode.S0, controller.Decide(Estimates(2), 0));
        Assert.Equal(SearchMode.S0, controller.Decide(Estimates(1), 0)); Assert.Equal(SearchMode.S0, controller.Decide(Estimates(1), 0)); Assert.Equal(SearchMode.S1, controller.Decide(Estimates(1), 0));
    }
    [Fact] public void M5SwitchesImmediatelyAndRespectsStrictPaybackBoundary()
    {
        var controller = new AdaptiveController(new() { PredictionHorizon = 10, HysteresisIntervals = 99 }, true);
        ModeEstimate[] estimates = [new(SearchMode.S0, 10, 10, 10), new(SearchMode.S1, 0, 10, 10), new(SearchMode.S2, 20, 10, 10)];
        Assert.Equal(SearchMode.S0, controller.Decide(estimates, 100)); Assert.Equal(SearchMode.S1, controller.Decide(estimates, 99));
    }
    [Fact] public void ResourceConstraintsExcludeUnavailableModes()
    {
        var controller = new AdaptiveController(new() { MemoryLimitBytes = 100, LatencyLimitNs = 100 }, true);
        Assert.Equal(SearchMode.S2, controller.Decide([new(SearchMode.S0, 50, 10, 10), new(SearchMode.S1, 0, 101, 10), new(SearchMode.S2, 1, 10, 10)], 0));
        Assert.Throws<InvalidOperationException>(() => controller.Decide([new(SearchMode.S0, 1, 101, 1), new(SearchMode.S1, 1, 10, 101), new(SearchMode.S2, 1, 101, 1)], 0));
    }
    [Fact] public void RebuildPaybackUsesStrictInequality()
    {
        var controller = new AdaptiveController(new() { RebuildHorizon = 10, RebuildMarginNs = 2 });
        Assert.False(controller.ShouldRebuild(1, 8)); Assert.True(controller.ShouldRebuild(1.01, 8));
    }
    [Fact] public void BinaryFormatsRoundTripAndRejectTruncation()
    {
        string dir = Path.Combine(Path.GetTempPath(), "evm-test-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "queries.bin"); Query[] queries = [new(1, A(1), 10), new(2, A(2), 20)]; Files.WriteQueries(path, queries); Assert.Equal(queries, Files.ReadQueries(path));
            Assert.Equal(72, new FileInfo(path).Length); Files.Verify(path, Files.Sha(path)); Assert.Throws<InvalidDataException>(() => Files.Verify(path, new string('0', 64)));
            File.WriteAllBytes(path, [1]); Assert.Throws<InvalidDataException>(() => Files.ReadQueries(path));
            Files.WriteAddresses(path, [A(1), A(2)]); Assert.Equal(new[] { A(1), A(2) }, Files.ReadAddresses(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
