using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EvmMatching.Core;

namespace EvmMatching.Data;

public static class Preparation
{
    public static async Task Fetch(string endpoint, long from, long to)
    {
        if (from < 0 || to < from) throw new ArgumentException("Invalid block range.");
        Directory.CreateDirectory("data/raw"); using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var chain = await Rpc(http, endpoint, "eth_chainId", []);
        long chainId = Convert.ToInt64(chain.RootElement.GetProperty("result").GetString(), 16);
        var retrieval = new { chain_id = chainId, block_from = from, block_to = to, endpoint = new Uri(endpoint).GetLeftPart(UriPartial.Authority), retrieval_utc = DateTimeOffset.UtcNow, fields = new[] { "transaction.from", "transaction.to" }, api_reference = "https://ethereum.github.io/execution-apis/api/methods/eth_getBlockByNumber/" };
        Files.SaveJson("data/raw/source.json", retrieval);
        File.AppendAllText("data/raw/fetch_log.jsonl", JsonSerializer.Serialize(retrieval) + Environment.NewLine);
        for (long block = from; block <= to; block++)
        {
            var path = $"data/raw/block_{block}.json";
            var compressedPath = path + ".gz";
            if (File.Exists(path) || File.Exists(compressedPath)) continue;
            using var json = await Rpc(http, endpoint, "eth_getBlockByNumber", ["0x" + block.ToString("x", CultureInfo.InvariantCulture), true]);
            if (json.RootElement.GetProperty("result").ValueKind == JsonValueKind.Null) throw new InvalidDataException("Missing block " + block);
            var temporaryPath = compressedPath + ".tmp";
            using (var output = new GZipStream(File.Create(temporaryPath), CompressionLevel.Optimal))
                output.Write(Encoding.UTF8.GetBytes(json.RootElement.GetRawText()));
            File.Move(temporaryPath, compressedPath, overwrite: true);
            Console.WriteLine($"Saved block {block}");
        }
    }
    private static async Task<JsonDocument> Rpc(HttpClient http, string endpoint, string method, object[] args)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = args });
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await http.PostAsync(endpoint, new StringContent(body, Encoding.UTF8, "application/json"));
                if (((int)response.StatusCode >= 500 || (int)response.StatusCode == 429) && attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                    continue;
                }
                response.EnsureSuccessStatusCode();
                var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (json.RootElement.TryGetProperty("error", out var error)) throw new InvalidDataException(error.GetRawText());
                return json;
            }
            catch (HttpRequestException) when (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            }
            catch (TaskCanceledException) when (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            }
        }
    }
    public static void Normalize()
    {
        using var source = JsonDocument.Parse(File.ReadAllText("data/raw/source.json")); var metadata = source.RootElement;
        long from = metadata.GetProperty("block_from").GetInt64(), to = metadata.GetProperty("block_to").GetInt64();
        var queries = new List<Query>(); var events = new StringBuilder(); var rejected = new List<object>(); long sequence = 0, eventId = 0;
        string? previousHash = null;
        for (long block = from; block <= to; block++)
        {
            var rawPath = $"data/raw/block_{block}.json";
            var blockPath = File.Exists(rawPath) ? rawPath : rawPath + ".gz";
            using var input = File.OpenRead(blockPath);
            using Stream blockStream = blockPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? new GZipStream(input, CompressionMode.Decompress)
                : input;
            using var doc = JsonDocument.Parse(blockStream); var root = doc.RootElement.TryGetProperty("result", out var result) ? result : doc.RootElement;
            if (Convert.ToInt64(root.GetProperty("number").GetString(), 16) != block) throw new InvalidDataException("Block number mismatch.");
            if (previousHash is not null && root.GetProperty("parentHash").GetString() != previousHash) throw new InvalidDataException("Non-contiguous block hashes.");
            previousHash = root.GetProperty("hash").GetString();
            foreach (var tx in root.GetProperty("transactions").EnumerateArray())
            {
                var addresses = new List<string>(); eventId++;
                foreach (string field in new[] { "from", "to" })
                {
                    var text = tx.GetProperty(field).GetString();
                    if (text is null && field == "to") continue; // Contract creation: no receipt-derived address is guessed.
                    if (!Address20.TryParse(text, out var a)) { rejected.Add(new { block, event_id = eventId, field, reason = "invalid_address" }); continue; }
                    addresses.Add(a.ToString()); queries.Add(new(++sequence, a, eventId));
                }
                events.AppendLine(JsonSerializer.Serialize(new { sequence = eventId, block_number = block, tx_hash = tx.GetProperty("hash").GetString(), addresses }));
            }
        }
        if (queries.Count < 10) throw new InvalidDataException("Dataset too small.");
        Directory.CreateDirectory("data/processed");
        WriteRawZstd("data/processed/events.jsonl.zst", Encoding.UTF8.GetBytes(events.ToString()));
        Files.WriteQueries("data/processed/queries.bin", queries);
        // Partition on transaction boundaries to prevent one transaction leaking across splits.
        long tuneEnd = (long)(eventId * .3), validEnd = (long)(eventId * .4);
        var parts = new Dictionary<string, Query[]> { ["tuning"] = queries.Where(q => q.EventId <= tuneEnd).ToArray(), ["validation"] = queries.Where(q => q.EventId > tuneEnd && q.EventId <= validEnd).ToArray(), ["measurement"] = queries.Where(q => q.EventId > validEnd).ToArray() };
        foreach (var (name, values) in parts) Files.WriteQueries($"data/processed/{name}.bin", values);
        Files.SaveJson("data/processed/rejected_records.json", rejected);
        var paths = Directory.GetFiles("data/processed").Where(p => !p.EndsWith("dataset_manifest.json", StringComparison.Ordinal)).Order().ToArray();
        Files.SaveJson("data/processed/dataset_manifest.json", new
        {
            chain_id = metadata.GetProperty("chain_id").GetInt64(), block_from = from, block_to = to,
            extraction_rules = new[] { "Transactions in canonical block and transaction order; from followed by non-null to.", "Only explicitly typed 20-byte address fields; no topic decoding or guessed creation addresses.", "30/10/60 split by transaction order, rounded down; no shuffle." },
            counts = new { events = eventId, queries = queries.Count, unique_addresses = queries.Select(q => q.Address).Distinct().Count(), rejected = rejected.Count },
            partitions = parts.ToDictionary(p => p.Key, p => new { count = p.Value.Length, first_sequence = p.Value.FirstOrDefault().Sequence, last_sequence = p.Value.LastOrDefault().Sequence }),
            files = paths.Select(p => new { path = p.Replace('\\', '/'), sha256 = Files.Sha(p) }),
            raw_files = Directory.GetFiles("data/raw", "*.json").Concat(Directory.GetFiles("data/raw", "*.json.gz")).Order().Select(p => new { path = p.Replace('\\', '/'), sha256 = Files.Sha(p) })
        });
    }
    // Valid Zstandard frame using raw blocks. No native codec or network dependency needed.
    private static void WriteRawZstd(string path, byte[] bytes)
    {
        using var writer = new BinaryWriter(File.Create(path)); writer.Write(0xFD2FB528u); writer.Write((byte)0xE0); writer.Write((ulong)bytes.LongLength);
        int offset = 0;
        do { int length = Math.Min(128 * 1024, bytes.Length - offset); bool last = offset + length == bytes.Length; int header = (length << 3) | (last ? 1 : 0); writer.Write((byte)header); writer.Write((byte)(header >> 8)); writer.Write((byte)(header >> 16)); writer.Write(bytes, offset, length); offset += length; } while (offset < bytes.Length);
    }
    public static ExperimentConfig PrepareScenario(string partition, string name, int wSize, double targetHitRate, double updateRate, double duration, int seed, double deleteRatio = .5)
    {
        if (partition is not ("tuning" or "validation" or "measurement") || wSize < 1 || targetHitRate < 0 || targetHitRate > 1 || updateRate < 0 || duration <= 0 || deleteRatio < 0 || deleteRatio > 1 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))) throw new ArgumentException("Invalid scenario.");
        var input = Files.ReadQueries($"data/processed/{partition}.bin");
        var all = Files.ReadQueries("data/processed/queries.bin").Select(q => q.Address).Distinct().ToArray();
        var frequencies = input.GroupBy(q => q.Address).Select(g => (Address: g.Key, Count: g.Count())).ToArray();
        var random = new Random(seed); random.Shuffle(frequencies);
        double desired = targetHitRate * input.Length; int hits = 0; var chosen = new HashSet<Address20>();
        foreach (var item in frequencies.OrderByDescending(f => f.Count))
            if (chosen.Count < wSize && hits + item.Count <= Math.Round(desired)) { chosen.Add(item.Address); hits += item.Count; }
        var inputSet = input.Select(q => q.Address).ToHashSet();
        var absent = all.Where(a => !inputSet.Contains(a)).ToArray(); random.Shuffle(absent);
        foreach (var a in absent) { if (chosen.Count == wSize) break; chosen.Add(a); }
        if (chosen.Count != wSize) throw new InvalidDataException($"Insufficient real absent addresses for W={wSize}, p={targetHitRate}. Import more blocks or select a feasible scenario.");
        double actual = (double)hits / input.Length;
        // Reject an unattainable target rather than silently changing the requested experiment.
        if (Math.Abs(actual - targetHitRate) > Math.Max(1.0 / input.Length, targetHitRate * .05)) throw new InvalidDataException($"Unattainable targetHitRate={targetHitRate}; closest greedy construction={actual}. More data or a documented tolerance is needed.");
        string prefix = $"data/processed/{name}"; Files.WriteQueries(prefix + "_queries.bin", input);
        string watch = $"data/watchlists/{name}.bin", updatesPath = $"data/updates/{name}.csv";
        Files.WriteAddresses(watch, chosen.OrderBy(a => a.ToString(), StringComparer.Ordinal));
        var mutable = new HashSet<Address20>(chosen); var updates = new List<Update>();
        int totalUpdates = checked((int)(updateRate * duration));
        // Use indexed candidate lists for reproducibility; Add and Remove are always valid.
        var present = chosen.OrderBy(a => a.ToString(), StringComparer.Ordinal).ToList(); var missing = all.Where(a => !mutable.Contains(a)).ToList();
        for (int i = 0; i < totalUpdates; i++)
        {
            bool remove = random.NextDouble() < deleteRatio;
            if ((remove && present.Count == 0) || (!remove && missing.Count == 0)) throw new InvalidDataException("Insufficient real addresses for requested update schedule.");
            var pool = remove ? present : missing; int index = random.Next(pool.Count); var a = pool[index]; pool[index] = pool[^1]; pool.RemoveAt(pool.Count - 1); (remove ? missing : present).Add(a);
            long queryIndex = Math.Min(input.Length - 1, (long)((i + 1.0) / (totalUpdates + 1) * input.Length));
            updates.Add(new(input[queryIndex].Sequence, remove ? UpdateKind.Remove : UpdateKind.Add, a, (long)((i + 1) / updateRate * 1e9)));
        }
        Directory.CreateDirectory("data/updates");
        File.WriteAllLines(updatesPath, new[] { "sequence,timestamp_ns,operation,address_hex" }.Concat(updates.Select(u => $"{u.Sequence},{u.TimestampNs},{u.Kind},{u.Address}")));
        var state = new HashSet<Address20>(chosen); int up = 0, replayHits = 0;
        foreach (var q in input) { while (up < updates.Count && updates[up].Sequence <= q.Sequence) { var u = updates[up++]; if (u.Kind == UpdateKind.Add) state.Add(u.Address); else state.Remove(u.Address); } if (state.Contains(q.Address)) replayHits++; }
        Files.SaveJson(prefix + "_manifest.json", new { seed, partition, w_size = wSize, target_hit_rate = targetHitRate, initial_hit_rate = actual, actual_hit_rate = (double)replayHits / input.Length, query_count = input.Length, update_count = updates.Count, add_count = updates.Count(u => u.Kind == UpdateKind.Add), remove_count = updates.Count(u => u.Kind == UpdateKind.Remove), files = new[] { prefix + "_queries.bin", watch, updatesPath }.Select(p => new { path = p, sha256 = Files.Sha(p) }) });
        return new() { Scenario = name, Seed = seed, DatasetPath = prefix + "_queries.bin", DatasetSha256 = Files.Sha(prefix + "_queries.bin"), WatchlistPath = watch, WatchlistSha256 = Files.Sha(watch), UpdatesPath = updatesPath, UpdatesSha256 = Files.Sha(updatesPath), WSize = wSize, TargetHitRate = targetHitRate, UpdateRatePerSecond = updateRate, DeleteRatio = deleteRatio, MeasureSeconds = duration, DynamicFilterCapacity = Math.Max(wSize + updates.Count(u => u.Kind == UpdateKind.Add) + 1, (int)Math.Ceiling(wSize * 1.25)), Partition = partition };
    }
}
