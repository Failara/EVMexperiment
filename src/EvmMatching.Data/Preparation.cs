using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EvmMatching.Core;

namespace EvmMatching.Data;

public static class Preparation
{
    // Indexed address fields are decoded only when topic0 and the standard event's
    // indexed argument layout match. Unknown topics are retained in raw receipts.
    private static readonly Dictionary<string, int[]> IndexedAddressTopics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef"] = [1, 2], // ERC-20/721 Transfer
        ["0x8c5be1e5ebec7d5bd14f71427d1e84f3dd0314c0f7b2291e5b200ac8c7c3b925"] = [1, 2], // ERC-20/721 Approval
        ["0x17307eab39ab6107e8899845ad3d59bd9653f200f220920489ca2b5937696c31"] = [1, 2], // ERC-721/1155 ApprovalForAll
        ["0xc3d58168c5ae7397731d063d5bbf3d657854427343f4c083240f7aacaa2d0f62"] = [1, 2, 3], // ERC-1155 TransferSingle
        ["0x4a39dc06d4c0dbc64b70af90fd698a2331e43f909423b9fbe0ea4c4337871e3f"] = [1, 2, 3] // ERC-1155 TransferBatch
    };

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

    public static async Task FetchReceipts(string endpoint, long from, long to)
    {
        if (from < 0 || to < from) throw new ArgumentException("Invalid block range.");
        Directory.CreateDirectory("data/raw"); using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        using var chain = await Rpc(http, endpoint, "eth_chainId", []);
        long chainId = Convert.ToInt64(chain.RootElement.GetProperty("result").GetString(), 16);
        var retrieval = new { chain_id = chainId, block_from = from, block_to = to, endpoint = new Uri(endpoint).GetLeftPart(UriPartial.Authority), retrieval_utc = DateTimeOffset.UtcNow, method = "eth_getBlockReceipts", api_reference = "https://ethereum.github.io/execution-apis/api/methods/eth_getBlockReceipts/" };
        Files.SaveJson("data/raw/receipt_source.json", retrieval);
        File.AppendAllText("data/raw/fetch_log.jsonl", JsonSerializer.Serialize(retrieval) + Environment.NewLine);
        for (long block = from; block <= to; block++)
        {
            var path = $"data/raw/receipts_{block}.json.gz";
            if (File.Exists(path)) continue;
            using var json = await Rpc(http, endpoint, "eth_getBlockReceipts", ["0x" + block.ToString("x", CultureInfo.InvariantCulture)]);
            var result = json.RootElement.GetProperty("result");
            if (result.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Provider did not return a receipt array for block " + block);
            var temporaryPath = path + ".tmp";
            using (var output = new GZipStream(File.Create(temporaryPath), CompressionLevel.Optimal))
                output.Write(Encoding.UTF8.GetBytes(result.GetRawText()));
            File.Move(temporaryPath, path, overwrite: true);
            if (block % 100 == 0 || block == to) Console.WriteLine($"Saved receipts for block {block}");
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
    public static void Normalize(string outputDirectory = "data/processed")
    {
        using var source = JsonDocument.Parse(File.ReadAllText("data/raw/source.json")); var metadata = source.RootElement;
        long from = metadata.GetProperty("block_from").GetInt64(), to = metadata.GetProperty("block_to").GetInt64();
        var queries = new List<Query>(); var rejected = new List<object>(); long sequence = 0, eventId = 0;
        Directory.CreateDirectory(outputDirectory);
        using var eventFile = File.Create(Path.Combine(outputDirectory, "events.jsonl.gz"));
        using var eventGzip = new GZipStream(eventFile, CompressionLevel.Optimal);
        using var eventWriter = new StreamWriter(eventGzip, new UTF8Encoding(false));
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
            var receiptPath = $"data/raw/receipts_{block}.json.gz";
            if (!File.Exists(receiptPath)) throw new FileNotFoundException("Missing block receipts required by the data protocol.", receiptPath);
            using var receiptFile = File.OpenRead(receiptPath);
            using var receiptStream = new GZipStream(receiptFile, CompressionMode.Decompress);
            using var receiptDoc = JsonDocument.Parse(receiptStream);
            var receipts = receiptDoc.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("transactionHash").GetString()!, StringComparer.OrdinalIgnoreCase);
            foreach (var tx in root.GetProperty("transactions").EnumerateArray())
            {
                string txHash = tx.GetProperty("hash").GetString()!;
                if (!receipts.TryGetValue(txHash, out var receipt)) throw new InvalidDataException($"No receipt for transaction {txHash} in block {block}.");
                var addresses = new List<object>(); eventId++;
                foreach (string field in new[] { "from", "to" })
                {
                    var text = tx.GetProperty(field).GetString();
                    if (text is null && field == "to") continue; // Contract creation: no receipt-derived address is guessed.
                    if (!Address20.TryParse(text, out var a)) { rejected.Add(new { block, event_id = eventId, field, reason = "invalid_address" }); continue; }
                    addresses.Add(new { role = field, address = a.ToString(), provenance = "transaction." + field }); queries.Add(new(++sequence, a, eventId));
                }
                var creation = receipt.TryGetProperty("contractAddress", out var contractAddress) && contractAddress.ValueKind == JsonValueKind.String ? contractAddress.GetString() : null;
                if (creation is not null)
                {
                    if (Address20.TryParse(creation, out var created)) { addresses.Add(new { role = "created_contract", address = created.ToString(), provenance = "receipt.contractAddress" }); queries.Add(new(++sequence, created, eventId)); }
                    else rejected.Add(new { block, event_id = eventId, field = "receipt.contractAddress", reason = "invalid_address" });
                }
                var decodedLogs = new List<object>();
                foreach (var log in receipt.GetProperty("logs").EnumerateArray())
                {
                    string? emitterText = log.GetProperty("address").GetString();
                    var logAddresses = new List<object>();
                    if (Address20.TryParse(emitterText, out var emitter))
                    {
                        logAddresses.Add(new { role = "emitter", address = emitter.ToString(), provenance = "log.address" });
                        queries.Add(new(++sequence, emitter, eventId));
                    }
                    else rejected.Add(new { block, event_id = eventId, field = "log.address", reason = "invalid_address" });
                    var topics = log.GetProperty("topics").EnumerateArray().Select(t => t.GetString()!).ToArray();
                    if (topics.Length > 0 && IndexedAddressTopics.TryGetValue(topics[0], out var positions))
                    {
                        bool expectedTopicCount = topics.Length == positions.Length + 1 ||
                            (positions.Length == 2 && topics.Length == 4); // ERC-721 adds indexed tokenId.
                        if (expectedTopicCount)
                        {
                            foreach (int topicIndex in positions)
                            {
                                string topic = topics[topicIndex];
                                if (topic.Length == 66 && topic.StartsWith("0x000000000000000000000000", StringComparison.OrdinalIgnoreCase) && Address20.TryParse("0x" + topic[^40..], out var decoded))
                                {
                                    var provenance = $"log.topics[{topicIndex}]/{topics[0]}";
                                    logAddresses.Add(new { role = "indexed_address", address = decoded.ToString(), provenance });
                                    addresses.Add(new { role = "event_address", address = decoded.ToString(), provenance = $"log[{log.GetProperty("logIndex").GetString()}].{provenance}" });
                                    queries.Add(new(++sequence, decoded, eventId));
                                }
                                else rejected.Add(new { block, event_id = eventId, field = $"log.topics[{topicIndex}]", reason = "invalid_indexed_address_encoding" });
                            }
                        }
                    }
                    decodedLogs.Add(new { log_index = log.GetProperty("logIndex").GetString(), emitter = emitterText, topic0 = topics.FirstOrDefault(), decoded_addresses = logAddresses });
                }
                eventWriter.WriteLine(JsonSerializer.Serialize(new { sequence = eventId, block_number = block, tx_hash = txHash, addresses, logs = decodedLogs }));
            }
        }
        if (queries.Count < 10) throw new InvalidDataException("Dataset too small.");
        eventWriter.Flush();
        Files.WriteQueries(Path.Combine(outputDirectory, "queries.bin"), queries);
        // Partition on transaction boundaries to prevent one transaction leaking across splits.
        long tuneEnd = (long)(eventId * .3), validEnd = (long)(eventId * .4);
        var parts = new Dictionary<string, Query[]> { ["tuning"] = queries.Where(q => q.EventId <= tuneEnd).ToArray(), ["validation"] = queries.Where(q => q.EventId > tuneEnd && q.EventId <= validEnd).ToArray(), ["measurement"] = queries.Where(q => q.EventId > validEnd).ToArray() };
        foreach (var (name, values) in parts) Files.WriteQueries(Path.Combine(outputDirectory, $"{name}.bin"), values);
        Files.SaveJson(Path.Combine(outputDirectory, "rejected_records.json"), rejected);
        var paths = Directory.GetFiles(outputDirectory).Where(p => !p.EndsWith("dataset_manifest.json", StringComparison.Ordinal)).Order().ToArray();
        Files.SaveJson(Path.Combine(outputDirectory, "dataset_manifest.json"), new
        {
            chain_id = metadata.GetProperty("chain_id").GetInt64(), block_from = from, block_to = to,
            extraction_rules = new[] { "Transactions in canonical block and transaction order; from, non-null to, receipt contractAddress, and log emitter addresses.", "Indexed topics are decoded only for registered ERC-20/721 Transfer/Approval and ERC-1155 TransferSingle/TransferBatch signatures and ABI layouts; arbitrary topic suffixes are never interpreted as addresses.", "Raw receipt JSON is retained separately with block provenance; rejected fields are recorded.", "30/10/60 split by transaction order, rounded down; no shuffle." },
            counts = new { events = eventId, queries = queries.Count, unique_addresses = queries.Select(q => q.Address).Distinct().Count(), rejected = rejected.Count },
            partitions = parts.ToDictionary(p => p.Key, p => new { count = p.Value.Length, first_sequence = p.Value.FirstOrDefault().Sequence, last_sequence = p.Value.LastOrDefault().Sequence }),
            files = paths.Select(p => new { path = p.Replace('\\', '/'), sha256 = Files.Sha(p) }),
            raw_files = Directory.GetFiles("data/raw", "*.json").Concat(Directory.GetFiles("data/raw", "*.json.gz")).Order().Select(p => new { path = p.Replace('\\', '/'), sha256 = Files.Sha(p) })
        });
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
