using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using EvmMatching.Core;
using EvmMatching.Data;

namespace EvmMatching.Runner;

internal static class RunOne
{
    private const string Header = "run_id,interval_index,start_ns,end_ns,active_mode,query_count,update_count,throughput_per_second,latency_p50_ns,latency_p95_ns,latency_p99_ns,exact_checks,filter_maybe,filter_false_positive,switch_count,switch_time_ns,rebuild_count,rebuild_time_ns,rss_bytes,fn_count,final_fp_count";
    public static int Execute(string path)
    {
        var config = Files.ReadJson<ExperimentConfig>(path); config.Validate();
        string dir = $"results/raw/{config.SeriesId}/{config.Experiment}/{config.Scenario}/{config.RunId}";
        if (Directory.Exists(dir)) throw new IOException("Run directory already exists; use a new seriesId or replicate.");
        Directory.CreateDirectory(dir); File.Copy(path, dir + "/config.json");
        File.WriteAllText(dir + "/stdout.log", "Run started\n"); File.WriteAllText(dir + "/stderr.log", "");
        try
        {
            Files.SaveJson(dir + "/environment.json", new { framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), logical_cores = Environment.ProcessorCount, server_gc = GCSettings.IsServerGC, gc_latency_mode = GCSettings.LatencyMode.ToString(), stopwatch_frequency = Stopwatch.Frequency, process_id = Environment.ProcessId, utc_started = DateTimeOffset.UtcNow, code_sha256 = Files.Sha(typeof(Matcher).Assembly.Location), runner_sha256 = Files.Sha(typeof(RunOne).Assembly.Location), data_sha256 = Files.Sha(typeof(Files).Assembly.Location), threads = config.Threads });
            if (GCSettings.IsServerGC != (config.GcMode == "server")) throw new InvalidDataException("GC mode does not match configuration; use run or run-series launcher.");
            Files.Verify(config.DatasetPath, config.DatasetSha256); Files.Verify(config.WatchlistPath, config.WatchlistSha256); Files.Verify(config.UpdatesPath, config.UpdatesSha256);
            var queries = Files.ReadQueries(config.DatasetPath); var initial = Files.ReadAddresses(config.WatchlistPath); var updates = Files.ReadUpdates(config.UpdatesPath);
            if (queries.Length == 0 || initial.Length != config.WSize || initial.Distinct().Count() != initial.Length) throw new InvalidDataException("Input counts or unique watchlist mismatch.");
            if (updates.Any(u => u.Sequence < queries[0].Sequence || u.Sequence > queries[^1].Sequence)) throw new InvalidDataException("Update outside query range.");
            var expected = Oracle(initial, queries, updates);
            using (var verification = Create(config))
            {
                verification.Initialize(initial); int u = 0;
                for (int i = 0; i < queries.Length; i++)
                {
                    while (u < updates.Length && updates[u].Sequence <= queries[i].Sequence) Apply(verification, updates[u++]);
                    if (verification.Contains(queries[i].Address) != expected[i]) throw new InvalidDataException($"correctness_error: sequence={queries[i].Sequence}, address={queries[i].Address}, expected={expected[i]}");
                }
                verification.WaitForRebuild();
            }
            // Warm all code paths using throwaway state; no warmup counters enter the result.
            using (var warm = Create(config))
            {
                warm.Initialize(initial); var timer = Stopwatch.StartNew();
                while (timer.Elapsed.TotalSeconds < config.WarmupSeconds)
                {
                    int u = 0;
                    for (int i = 0; i < queries.Length; i++) { while (u < updates.Length && updates[u].Sequence <= queries[i].Sequence) Apply(warm, updates[u++]); warm.Contains(queries[i].Address); if (config.Method is "M4" or "M5" or "M6") for (int m = 0; m < 3; m++) warm.Probe(queries[i].Address, (SearchMode)m); }
                    warm.Initialize(initial);
                }
            }
            using var matcher = Create(config); matcher.Initialize(initial); matcher.ResetMetrics();
            bool scheduled = config.ReplayPolicy == "scheduled";
            if (scheduled && Math.Abs(queries.Length / config.QueryRatePerSecond - config.MeasureSeconds) > 1 / config.QueryRatePerSecond) throw new InvalidDataException("Scheduled query count and measurement duration differ.");
            var cleanCost = CalibrateCleanS2(matcher, queries, config.Method);
            var totalLatency = new Histogram(); var intervalLatency = new Histogram(); var shadowHist = new[] { new Histogram(), new Histogram(), new Histogram() }; long[] shadowTicks = new long[3], shadowCounts = new long[3];
            var rows = new List<string>(); long elapsedNs = 0, totalQueries = 0, totalUpdates = 0, totalAdds = 0, totalHits = 0, fn = 0, fp = 0;
            long intervalStart = 0, intervalQueries = 0, intervalUpdates = 0, priorIntervalHits = 0; int intervalIndex = 0, pass = 0;
            var process = Process.GetCurrentProcess(); long peakRss = process.WorkingSet64;
            long allocatedBefore = GC.GetTotalAllocatedBytes(true); var old = matcher.Counters; long sw = 0, swt = 0, rb = 0, rbt = 0;
            string activeMode = matcher.ActiveMode.ToString();
            long scheduledOrigin = Stopwatch.GetTimestamp(), maximumScheduleLag = 0;
            while (elapsedNs < config.MeasureSeconds * 1e9)
            {
                // State restoration between full replay passes is outside the algorithm timer.
                if (pass > 0) matcher.Initialize(initial);
                int qi = 0, ui = 0;
                while (qi < queries.Length)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    while (ui < updates.Length && updates[ui].Sequence <= queries[qi].Sequence) { if (scheduled) WaitUntil(updates[ui].TimestampNs); Apply(matcher, updates[ui]); if (updates[ui].Kind == UpdateKind.Add) totalAdds++; ui++; intervalUpdates++; totalUpdates++; }
                    int end = Math.Min(queries.Length, qi + 4096);
                    if (scheduled) end = Math.Min(end, Math.Max(qi + 1, (int)Math.Min(int.MaxValue, Math.Ceiling((intervalStart + config.ControlIntervalMs * 1_000_000L) / 1e9 * config.QueryRatePerSecond))));
                    if (ui < updates.Length) { int next = qi; while (next < end && queries[next].Sequence < updates[ui].Sequence) next++; end = next; }
                    int begin = qi, batchEnd = end;
                    if (config.Threads == 1)
                    {
                        for (int j = begin; j < batchEnd; j++) Check(j, intervalLatency, shadowHist, shadowTicks, shadowCounts);
                    }
                    else
                    {
                        var workerHist = new Histogram[config.Threads]; var workerShadow = new Histogram[config.Threads][]; var workerTicks = new long[config.Threads][]; var workerCounts = new long[config.Threads][];
                        Parallel.For(0, config.Threads, new ParallelOptions { MaxDegreeOfParallelism = config.Threads }, worker =>
                        {
                            var hist = new Histogram(); var sh = new[] { new Histogram(), new Histogram(), new Histogram() }; var ticks = new long[3]; var counts = new long[3];
                            int lo = begin + (batchEnd - begin) * worker / config.Threads, hi = begin + (batchEnd - begin) * (worker + 1) / config.Threads;
                            for (int j = lo; j < hi; j++) Check(j, hist, sh, ticks, counts);
                            workerHist[worker] = hist; workerShadow[worker] = sh; workerTicks[worker] = ticks; workerCounts[worker] = counts;
                        });
                        for (int worker = 0; worker < config.Threads; worker++) { intervalLatency.Merge(workerHist[worker]); for (int m = 0; m < 3; m++) { shadowHist[m].Merge(workerShadow[worker][m]); shadowTicks[m] += workerTicks[worker][m]; shadowCounts[m] += workerCounts[worker][m]; } }
                    }
                    intervalQueries += end - qi; totalQueries += end - qi; qi = end;
                    elapsedNs = scheduled ? Matcher.Ns(Stopwatch.GetTimestamp() - scheduledOrigin) : elapsedNs + Matcher.Ns(Stopwatch.GetTimestamp() - t0);
                    if (elapsedNs - intervalStart >= config.ControlIntervalMs * 1_000_000L) FlushInterval();
                }
                pass++;
                if (scheduled) { WaitUntil((long)(config.MeasureSeconds * 1e9)); elapsedNs = Matcher.Ns(Stopwatch.GetTimestamp() - scheduledOrigin); break; }
            }
            long drainStart = Stopwatch.GetTimestamp(); matcher.WaitForRebuild(); elapsedNs += Matcher.Ns(Stopwatch.GetTimestamp() - drainStart);
            if (intervalQueries > 0) FlushInterval(false);
            long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
            File.WriteAllLines(dir + "/interval_metrics.csv", new[] { Header }.Concat(rows)); Files.SaveJson(dir + "/controller_events.json", matcher.Events);
            var count = matcher.Counters;
            var summary = new Dictionary<string, object?>
            {
                ["run_id"] = config.RunId, ["series_id"] = config.SeriesId, ["experiment"] = config.Experiment, ["scenario"] = config.Scenario, ["method"] = config.Method, ["replicate"] = config.Replicate, ["seed"] = config.Seed, ["status"] = "SUCCESS",
                ["dataset_sha256"] = config.DatasetSha256, ["watchlist_sha256"] = config.WatchlistSha256, ["updates_sha256"] = config.UpdatesSha256, ["w_size"] = config.WSize, ["actual_hit_rate"] = totalHits / (double)totalQueries,
                ["query_count"] = totalQueries, ["update_count"] = totalUpdates, ["add_count"] = totalAdds, ["remove_count"] = totalUpdates - totalAdds, ["threads"] = config.Threads,
                ["warmup_seconds"] = config.WarmupSeconds, ["measure_seconds"] = config.MeasureSeconds, ["elapsed_ns"] = elapsedNs, ["throughput_per_second"] = totalQueries / (elapsedNs / 1e9),
                ["latency_p50_ns"] = totalLatency.Percentile(.5), ["latency_p95_ns"] = totalLatency.Percentile(.95), ["latency_p99_ns"] = totalLatency.Percentile(.99), ["peak_rss_bytes"] = Math.Max(peakRss, process.PeakWorkingSet64), ["allocated_bytes"] = allocated,
                ["exact_checks"] = count.ExactChecks, ["filter_maybe"] = count.FilterMaybe, ["filter_false_positive"] = count.FilterFalsePositive, ["switch_count"] = matcher.SwitchCount, ["switch_time_ns"] = matcher.SwitchTimeNs, ["rebuild_count"] = matcher.RebuildCount, ["rebuild_time_ns"] = matcher.RebuildTimeNs,
                ["fn_count"] = fn, ["final_fp_count"] = fp, ["excluded_reason"] = "", ["dataset_passes"] = pass, ["actual_updates_per_second"] = totalUpdates / (elapsedNs / 1e9), ["latency_method"] = "all queries, 1/16-octave bucket upper bounds", ["measurement_policy"] = "whole deterministic replay passes; state restoration excluded; shadow probes, dispatch and correctness comparisons included; measureSeconds is minimum accumulated algorithm time; update timestamps are metadata, not wall-clock pacing"
            };
            summary["replay_policy"] = config.ReplayPolicy; summary["maximum_schedule_lag_ns"] = maximumScheduleLag;
            summary["clean_s2_positive_ns"] = cleanCost.Positive; summary["clean_s2_negative_ns"] = cleanCost.Negative;
            if (scheduled) summary["measurement_policy"] = "one continuous real-time scheduled replay; query arrivals and updates paced; elapsed includes pacing and drain; latency measures service time; schedule lag reported separately";
            if (fn != 0 || fp != 0) throw new InvalidDataException("correctness_error");
            Files.SaveJson(dir + "/run_summary.json", summary); File.WriteAllText(dir + "/SUCCESS", "");
            File.AppendAllText(dir + "/stdout.log", $"SUCCESS queries={totalQueries} FN={fn} FPfinal={fp}\n"); Console.WriteLine($"SUCCESS {config.RunId}"); return 0;

            void Check(int j, Histogram hist, Histogram[] sh, long[] ticks, long[] counts)
            {
                if (scheduled) WaitUntil((long)(j / config.QueryRatePerSecond * 1e9));
                long ts = Stopwatch.GetTimestamp(); bool actual = matcher.Contains(queries[j].Address); long te = Stopwatch.GetTimestamp(); hist.Add(Matcher.Ns(te - ts));
                if (expected[j]) Interlocked.Increment(ref totalHits);
                if (actual != expected[j]) { if (expected[j]) Interlocked.Increment(ref fn); else Interlocked.Increment(ref fp); throw new InvalidDataException($"correctness_error: sequence={queries[j].Sequence}, address={queries[j].Address}, expected={expected[j]}, actual={actual}"); }
                if (config.Method is "M4" or "M5" or "M6" && (ulong)j % (ulong)Math.Max(1, (int)(1 / config.ShadowRate)) == 0)
                    for (int m = 0; m < 3; m++) { ts = Stopwatch.GetTimestamp(); bool probe = matcher.Probe(queries[j].Address, (SearchMode)m); te = Stopwatch.GetTimestamp(); if (probe != expected[j]) throw new InvalidDataException("correctness_error: shadow"); ticks[m] += te - ts; counts[m]++; sh[m].Add(Matcher.Ns(te - ts)); }
            }
            void FlushInterval(bool runControl = true)
            {
                long controlStart = Stopwatch.GetTimestamp();
                if (runControl && config.Method is "M4" or "M5" or "M6")
                {
                    double seconds = Math.Max(1e-9, (elapsedNs - intervalStart) / 1e9);
                    long retained = matcher.EstimatedBytes;
                    var estimates = Enumerable.Range(0, 3).Select(m => new ModeEstimate((SearchMode)m, shadowCounts[m] == 0 ? double.MaxValue : Matcher.Ns(shadowTicks[m]) / (double)shadowCounts[m] * intervalQueries / seconds, retained, shadowHist[m].Percentile(.99))).ToArray();
                    double s2 = shadowCounts[2] > 0 ? Matcher.Ns(shadowTicks[2]) / (double)shadowCounts[2] : 0;
                    double observedHitRate = intervalQueries == 0 ? 0 : (totalHits - priorIntervalHits) / (double)intervalQueries;
                    double predictedClean = cleanCost.Negative * (1 - observedHitRate) + cleanCost.Positive * observedHitRate;
                    matcher.Control(estimates, Math.Max(0, s2 - predictedClean) * intervalQueries / seconds);
                }
                elapsedNs += Matcher.Ns(Stopwatch.GetTimestamp() - controlStart);
                process.Refresh(); peakRss = Math.Max(peakRss, process.WorkingSet64);
                if (process.WorkingSet64 > config.MemoryLimitMb * 1024 * 1024) throw new InvalidOperationException("resource_constraints: process RSS exceeds budget");
                var now = matcher.Counters;
                rows.Add(string.Join(',', new object[] { config.RunId, intervalIndex++, intervalStart, elapsedNs, activeMode, intervalQueries, intervalUpdates, (intervalQueries / Math.Max(1e-9, (elapsedNs - intervalStart) / 1e9)).ToString("R", CultureInfo.InvariantCulture), intervalLatency.Percentile(.5), intervalLatency.Percentile(.95), intervalLatency.Percentile(.99), now.ExactChecks - old.ExactChecks, now.FilterMaybe - old.FilterMaybe, now.FilterFalsePositive - old.FilterFalsePositive, matcher.SwitchCount - sw, matcher.SwitchTimeNs - swt, matcher.RebuildCount - rb, matcher.RebuildTimeNs - rbt, process.WorkingSet64, fn, fp }));
                totalLatency.Merge(intervalLatency); intervalLatency = new(); shadowHist = [new(), new(), new()]; shadowTicks = new long[3]; shadowCounts = new long[3];
                intervalStart = elapsedNs; intervalQueries = intervalUpdates = 0; activeMode = matcher.ActiveMode.ToString(); old = now; sw = matcher.SwitchCount; swt = matcher.SwitchTimeNs; rb = matcher.RebuildCount; rbt = matcher.RebuildTimeNs;
                priorIntervalHits = totalHits;
            }
            void WaitUntil(long targetNs)
            {
                long remaining;
                while ((remaining = targetNs - Matcher.Ns(Stopwatch.GetTimestamp() - scheduledOrigin)) > 0)
                {
                    if (remaining > 2_000_000) Thread.Sleep(1); else Thread.SpinWait(32);
                }
                long lag = Math.Max(0, Matcher.Ns(Stopwatch.GetTimestamp() - scheduledOrigin) - targetNs), prior;
                do { prior = Interlocked.Read(ref maximumScheduleLag); if (lag <= prior) break; } while (Interlocked.CompareExchange(ref maximumScheduleLag, lag, prior) != prior);
            }
        }
        catch (Exception e)
        {
            string reason = e.ToString().Contains("hash_mismatch", StringComparison.Ordinal) ? "hash_mismatch" : e.ToString().Contains("filter_overflow", StringComparison.Ordinal) ? "filter_overflow" : e.ToString().Contains("correctness_error", StringComparison.Ordinal) ? "correctness_error" : "crash";
            Files.SaveJson(dir + "/failure.json", new { run_id = config.RunId, status = "FAILED", excluded_reason = reason, error = e.Message }); File.AppendAllText(dir + "/stderr.log", e + "\n"); Console.Error.WriteLine(e.Message); return 1;
        }
    }
    internal static Matcher Create(ExperimentConfig c) => new(c.Method, c.DynamicFilterCapacity, c.M3RebuildThreshold, c.Controller);
    private static (double Positive, double Negative) CalibrateCleanS2(Matcher matcher, Query[] queries, string method)
    {
        if (method is not ("M4" or "M5" or "M6")) return (0, 0);
        long positiveTicks = 0, negativeTicks = 0, positives = 0, negatives = 0;
        for (int repeat = 0; repeat < 20; repeat++) for (int i = 0; i < Math.Min(1024, queries.Length); i++)
        {
            long start = Stopwatch.GetTimestamp(); bool hit = matcher.Probe(queries[i].Address, SearchMode.S2); long ticks = Stopwatch.GetTimestamp() - start;
            if (repeat == 0) continue;
            if (hit) { positiveTicks += ticks; positives++; } else { negativeTicks += ticks; negatives++; }
        }
        double negative = negatives == 0 ? 100 : Matcher.Ns(negativeTicks) / (double)negatives;
        return (positives == 0 ? negative * 2 : Matcher.Ns(positiveTicks) / (double)positives, negative);
    }
    private static void Apply(IAddressMatcher matcher, Update update) { if (update.Kind == UpdateKind.Add) matcher.Add(update.Address); else matcher.Remove(update.Address); }
    private static bool[] Oracle(Address20[] initial, Query[] queries, Update[] updates)
    {
        var h = new HashSet<Address20>(initial); var answers = new bool[queries.Length]; int u = 0;
        for (int i = 0; i < queries.Length; i++)
        {
            while (u < updates.Length && updates[u].Sequence <= queries[i].Sequence)
            {
                var update = updates[u++]; bool changed = update.Kind == UpdateKind.Add ? h.Add(update.Address) : h.Remove(update.Address);
                if (!changed) throw new InvalidDataException("Invalid duplicate Add or missing Remove in workload.");
            }
            answers[i] = h.Contains(queries[i].Address);
        }
        return answers;
    }
}
