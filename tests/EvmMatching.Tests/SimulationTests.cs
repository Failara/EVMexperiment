using EvmMatching.Core;
using EvmMatching.Data;

namespace EvmMatching.Tests;

public sealed class SimulationTests
{
    [Fact] public void ProfilesAreReproducibleAndAchieveScheduledHitAndUpdateCounts()
    {
        string original = Environment.CurrentDirectory, directory = Path.Combine(Path.GetTempPath(), "evm-profile-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            Environment.CurrentDirectory = directory;
            Query[] input = Enumerable.Range(1, 30).Select(i => new Query(i, Address20.Parse(i.ToString("x40")), i)).ToArray();
            Files.WriteQueries("data/processed/queries.bin", input); Files.WriteQueries("data/processed/measurement.bin", input);
            Directory.CreateDirectory("data/updates");
            var plan = new SimulationPlan { WSize = 5, QueryRatePerSecond = 100, Phases = [new("P1", 0, 20, .1), new("P2", 1, 20, .1)] };
            var config = Simulation.Prepare(plan); var queries = Files.ReadQueries(config.DatasetPath); var updates = Files.ReadUpdates(config.UpdatesPath);
            Assert.Equal(20, queries.Length); Assert.Equal(4, updates.Length);
            var state = Files.ReadAddresses(config.WatchlistPath).ToHashSet(); int u = 0;
            for (int i = 0; i < queries.Length; i++)
            {
                while (u < updates.Length && updates[u].Sequence <= queries[i].Sequence) { var op = updates[u++]; Assert.True(op.Kind == UpdateKind.Add ? state.Add(op.Address) : state.Remove(op.Address)); }
                Assert.Equal(i >= 10, state.Contains(queries[i].Address));
            }
            Assert.Equal(new long[] { 0, 50_000_000, 100_000_000, 150_000_000 }, updates.Select(v => v.TimestampNs));
            var repeated = Simulation.Prepare(plan);
            Assert.Equal(config.DatasetSha256, repeated.DatasetSha256); Assert.Equal(config.WatchlistSha256, repeated.WatchlistSha256); Assert.Equal(config.UpdatesSha256, repeated.UpdatesSha256);
        }
        finally { Environment.CurrentDirectory = original; Directory.Delete(directory, true); }
    }
    [Fact] public void M6RebuildsByFixedPeriodAndHonorsTransientMemoryLimit()
    {
        var a = Address20.Parse(new string('1', 40));
        using var matcher = new Matcher("M6", 10, options: new() { FixedRebuildSeconds = .001 }); matcher.Initialize([a]);
        Thread.Sleep(5); matcher.Control([new(SearchMode.S0, 1, 10, 10), new(SearchMode.S1, 2, 10, 10), new(SearchMode.S2, 3, 10, 10)], 0); matcher.WaitForRebuild();
        Assert.Contains(matcher.Events, e => e.Kind == "rebuild" && e.Reason == "fixed_period");
        using var limited = new Matcher("M3", 10, options: new() { MemoryLimitBytes = 1 }); limited.Initialize([a]);
        Assert.False(limited.StartRebuild("test")); Assert.Contains(limited.Events, e => e.Reason == "transient_memory_budget");
    }
}
