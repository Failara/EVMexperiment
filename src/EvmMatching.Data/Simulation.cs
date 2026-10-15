using EvmMatching.Core;

namespace EvmMatching.Data;

public sealed record WorkloadPhase(string Name, double HitRate, double UpdateRatePerSecond, double Seconds);
public sealed record SimulationPlan
{
    public string Name { get; init; } = "scheduled";
    public string Partition { get; init; } = "measurement";
    public string DataDirectory { get; init; } = "data/processed";
    public int WSize { get; init; } = 100;
    public int Seed { get; init; } = 240001;
    public double QueryRatePerSecond { get; init; } = 10000;
    public WorkloadPhase[] Phases { get; init; } = [new("P1", .01, 10, 1)];
}

public static class Simulation
{
    private sealed class IndexedRandomSet<T> where T : notnull
    {
        private readonly List<T> _items = [];
        private readonly Dictionary<T, int> _indices = [];

        public IndexedRandomSet(IEnumerable<T> items)
        {
            foreach (var item in items) Add(item);
        }

        public int Count => _items.Count;

        public void Add(T item)
        {
            if (_indices.TryAdd(item, _items.Count)) _items.Add(item);
        }

        public bool Remove(T item)
        {
            if (!_indices.Remove(item, out var index)) return false;
            var lastIndex = _items.Count - 1;
            if (index != lastIndex)
            {
                var last = _items[lastIndex];
                _items[index] = last;
                _indices[last] = index;
            }
            _items.RemoveAt(lastIndex);
            return true;
        }

        public T RandomItem(Random random) => _items[random.Next(_items.Count)];
    }

    public static ExperimentConfig Prepare(SimulationPlan plan)
    {
        if (plan.Partition is not ("tuning" or "validation" or "measurement") || plan.Name.Length == 0 || plan.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')) || string.IsNullOrWhiteSpace(plan.DataDirectory) || Path.IsPathRooted(plan.DataDirectory) || plan.DataDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is ".." or ".") || plan.WSize < 1 || plan.QueryRatePerSecond <= 0 || !double.IsFinite(plan.QueryRatePerSecond) || plan.Phases.Length == 0 || plan.Phases.Any(p => !double.IsFinite(p.HitRate) || !double.IsFinite(p.Seconds) || !double.IsFinite(p.UpdateRatePerSecond) || p.HitRate < 0 || p.HitRate > 1 || p.Seconds <= 0 || p.UpdateRatePerSecond < 0 || p.UpdateRatePerSecond > plan.QueryRatePerSecond)) throw new InvalidDataException("Invalid simulation plan.");
        var sourcePath = Path.Combine(plan.DataDirectory, $"{plan.Partition}.bin");
        var source = Files.ReadQueries(sourcePath);
        var byAddress = source.GroupBy(q => q.Address).ToDictionary(g => g.Key, g => g.First());
        var all = Files.ReadQueries(Path.Combine(plan.DataDirectory, "queries.bin")).Select(q => q.Address).Distinct().ToArray();
        if (plan.WSize >= all.Length) throw new InvalidDataException("Insufficient real addresses for requested W and updates.");
        var random = new Random(plan.Seed); random.Shuffle(all);
        var initial = all.Take(plan.WSize).ToHashSet();
        // Ensure the query partition has both a hit and a miss; all addresses remain real.
        var observed = byAddress.Keys.ToArray();
        if (!observed.Any(initial.Contains)) { initial.Remove(initial.First()); initial.Add(observed[0]); }
        if (observed.All(initial.Contains)) throw new InvalidDataException("No real negative query address remains.");
        var present = initial.OrderBy(a => a.ToString(), StringComparer.Ordinal).ToList(); var absent = all.Where(a => !initial.Contains(a)).ToList();
        var state = new HashSet<Address20>(initial); var queryHits = new IndexedRandomSet<Address20>(observed.Where(state.Contains)); var queryMisses = new IndexedRandomSet<Address20>(observed.Where(a => !state.Contains(a)));
        var queries = new List<Query>(); var updates = new List<Update>(); var phases = new List<object>();
        double start = 0; int toggles = 0, hitCount = 0, maxW = initial.Count;
        foreach (var phase in plan.Phases)
        {
            int qCount = checked((int)Math.Round(phase.Seconds * plan.QueryRatePerSecond));
            if (qCount < 1 || Math.Abs(qCount / plan.QueryRatePerSecond - phase.Seconds) > 1e-8) throw new InvalidDataException("Each phase duration must contain a whole number of scheduled queries.");
            int updateCount = checked((int)Math.Floor(phase.Seconds * phase.UpdateRatePerSecond)); int ui = 0, phaseHits = 0;
            for (int j = 0; j < qCount; j++)
            {
                double queryTime = start + j / plan.QueryRatePerSecond;
                while (ui < updateCount && start + ui / phase.UpdateRatePerSecond <= queryTime + 1e-12)
                {
                    double time = start + (ui++) / phase.UpdateRatePerSecond;
                    bool remove = toggles++ % 2 == 0;
                    var pool = remove ? present : absent;
                    // Keep one observed member and one observed nonmember for every requested profile.
                    bool Allowed(Address20 a) => !byAddress.ContainsKey(a) || (remove ? queryHits.Count : queryMisses.Count) > 1;
                    int selected = pool.Count == 0 ? -1 : random.Next(pool.Count);
                    for (int attempt = 0; selected >= 0 && !Allowed(pool[selected]) && attempt < 32; attempt++) selected = random.Next(pool.Count);
                    if (selected >= 0 && !Allowed(pool[selected])) selected = pool.FindIndex(a => Allowed(a));
                    if (selected < 0) throw new InvalidDataException("Not enough real addresses for a valid changing watchlist.");
                    var address = pool[selected];
                    pool[selected] = pool[^1]; pool.RemoveAt(pool.Count - 1); (remove ? absent : present).Add(address);
                    if (remove) state.Remove(address); else state.Add(address);
                    if (byAddress.ContainsKey(address)) { (remove ? queryHits : queryMisses).Remove(address); (remove ? queryMisses : queryHits).Add(address); }
                    updates.Add(new(queries.Count + 1, remove ? UpdateKind.Remove : UpdateKind.Add, address, (long)Math.Round(time * 1e9)));
                    maxW = Math.Max(maxW, state.Count);
                }
                bool hit = (long)Math.Round((j + 1) * phase.HitRate) > (long)Math.Round(j * phase.HitRate);
                var addresses = hit ? queryHits : queryMisses; if (addresses.Count == 0) throw new InvalidDataException("Unattainable profile hit rate.");
                var original = byAddress[addresses.RandomItem(random)];
                queries.Add(new(queries.Count + 1, original.Address, original.EventId)); if (hit) { phaseHits++; hitCount++; }
            }
            phases.Add(new { phase.Name, phase.HitRate, phase.UpdateRatePerSecond, phase.Seconds, start_seconds = start, query_count = qCount, update_count = ui, actual_hit_rate = phaseHits / (double)qCount });
            start += phase.Seconds;
        }
        string prefix = $"data/processed/{plan.Name}", watch = $"data/watchlists/{plan.Name}.bin", updatePath = $"data/updates/{plan.Name}.csv";
        Files.WriteQueries(prefix + "_queries.bin.gz", queries); Files.WriteAddresses(watch, initial.OrderBy(a => a.ToString(), StringComparer.Ordinal));
        Directory.CreateDirectory("data/updates");
        File.WriteAllLines(updatePath, new[] { "sequence,timestamp_ns,operation,address_hex" }.Concat(updates.Select(u => $"{u.Sequence},{u.TimestampNs},{u.Kind},{u.Address}")));
        Files.SaveJson(prefix + "_manifest.json", new { seed = plan.Seed, partition = plan.Partition, source = sourcePath.Replace('\\', '/'), source_sha256 = Files.Sha(sourcePath), generation = "Scheduled simulation resamples actual observed address/event pairs within one temporal partition. No synthetic address values. Updates occur before the first query at or after their timestamp, beginning at phase start. Query rate must be at least the update rate.", phases, query_count = queries.Count, update_count = updates.Count, actual_hit_rate = hitCount / (double)queries.Count, files = new[] { prefix + "_queries.bin.gz", watch, updatePath }.Select(p => new { path = p, sha256 = Files.Sha(p) }) });
        return new() { Scenario = plan.Name, Partition = plan.Partition, Seed = plan.Seed, WSize = initial.Count, DatasetPath = prefix + "_queries.bin.gz", DatasetSha256 = Files.Sha(prefix + "_queries.bin.gz"), WatchlistPath = watch, WatchlistSha256 = Files.Sha(watch), UpdatesPath = updatePath, UpdatesSha256 = Files.Sha(updatePath), DynamicFilterCapacity = Math.Max(maxW + 1, (int)Math.Ceiling(maxW * 1.25)), MeasureSeconds = start, QueryRatePerSecond = plan.QueryRatePerSecond, ReplayPolicy = "scheduled", TargetHitRate = plan.Phases[0].HitRate, UpdateRatePerSecond = updates.Count / start };
    }
}
