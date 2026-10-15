using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using EvmMatching.Core;
using EvmMatching.Data;

namespace EvmMatching.Runner;

internal static class Calibration
{
    public static void Execute(string seriesId, int replicate = 1, string datasetPath = "data/processed/tuning.bin")
    {
        if (seriesId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))) throw new ArgumentException("Invalid series id.");
        var addresses = Files.ReadQueries(datasetPath).Select(q => q.Address).Distinct().Take(1000).ToArray();
        var h = addresses.ToHashSet(); var hd = addresses.Take(50).ToHashSet(); var fd = new CountingBloom(addresses.Length + 1); foreach (var a in addresses) fd.Insert(a); var fs = new StaticBloom(addresses, addresses.Length);
        string runId = $"E0_primitives_r{replicate:00}_s240001", dir = $"results/raw/{seriesId}/E0/primitives/{runId}";
        if (Directory.Exists(dir)) throw new IOException("Calibration already exists."); Directory.CreateDirectory(dir);
        Files.SaveJson(dir + "/config.json", new { seriesId, experiment = "E0", scenario = "primitives", method = "primitives", replicate, seed = 240001, datasetPath, datasetSha256 = Files.Sha(datasetPath), samples_per_operation = 1000, timing_policy = "Stopwatch surrounds delegate invocation; setup and cleanup excluded; samples are raw and include timer/delegate overhead" });
        Files.SaveJson(dir + "/environment.json", new { framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription, server_gc = GCSettings.IsServerGC, logical_cores = Environment.ProcessorCount, code_sha256 = Files.Sha(typeof(Matcher).Assembly.Location), runner_sha256 = Files.Sha(typeof(Calibration).Assembly.Location), stopwatch_frequency = Stopwatch.Frequency });
        File.WriteAllText(dir + "/stdout.log", "E0 calibration started\n"); File.WriteAllText(dir + "/stderr.log", "");
        try
        {
            var samples = new List<string> { "operation,sample_index,duration_ns" }; long elapsed = 0; int sampleIndex = 0;
            using var adaptive = new Matcher("M5", addresses.Length + 1); adaptive.Initialize(addresses);
            using var snapshot = new Matcher("M3", addresses.Length + 1); snapshot.Initialize(addresses);
            string allQueriesPath = Path.Combine(Path.GetDirectoryName(datasetPath)!, "queries.bin");
            Address20 extra = Files.ReadQueries(allQueriesPath).Select(q => q.Address).First(a => !h.Contains(a));
            Measure("H.Contains", i => { _ = h.Contains(addresses[i % addresses.Length]); });
            Measure("Fd.MightContain", i => { _ = fd.MightContain(addresses[i % addresses.Length]); });
            Measure("Fs.MightContain", i => { _ = fs.MightContain(addresses[i % addresses.Length]); });
            Measure("Hd.Contains", i => { _ = hd.Contains(addresses[i % addresses.Length]); });
            Measure("H.Add", i => { h.Add(extra); }, i => h.Remove(extra));
            Measure("H.Remove", i => { h.Remove(extra); }, i => h.Add(extra), i => h.Add(extra)); h.Remove(extra);
            Measure("Fd.Insert", i => fd.Insert(extra), i => fd.Delete(extra));
            Measure("Fd.Delete", i => fd.Delete(extra), null, i => fd.Insert(extra));
            Measure("Fs.Build", i => { _ = new StaticBloom(addresses, addresses.Length).Bytes; });
            Measure("switch", i => { int best = i % 2 == 0 ? 1 : 0; adaptive.Control(Enumerable.Range(0, 3).Select(m => new ModeEstimate((SearchMode)m, m == best ? 0 : 1e9, 100, 10)).ToArray(), 0); });
            Measure("rebuild", i => { snapshot.StartRebuild("E0"); snapshot.WaitForRebuild(); });
            File.WriteAllLines(dir + "/samples.csv", samples);
            File.WriteAllText(dir + "/interval_metrics.csv", "run_id,interval_index,start_ns,end_ns,active_mode,query_count,update_count,throughput_per_second,latency_p50_ns,latency_p95_ns,latency_p99_ns,exact_checks,filter_maybe,filter_false_positive,switch_count,switch_time_ns,rebuild_count,rebuild_time_ns,rss_bytes,fn_count,final_fp_count\n");
            Files.SaveJson(dir + "/run_summary.json", new { run_id = runId, series_id = seriesId, experiment = "E0", scenario = "primitives", method = "primitives", replicate, seed = 240001, status = "SUCCESS", sample_count = sampleIndex, elapsed_ns = elapsed, fn_count = 0, final_fp_count = 0, excluded_reason = "", raw_samples = "samples.csv" });
            File.WriteAllText(dir + "/SUCCESS", ""); File.AppendAllText(dir + "/stdout.log", "SUCCESS\n");
            void Measure(string name, Action<int> operation, Action<int>? cleanup = null, Action<int>? setup = null)
            {
                for (int i = 0; i < 100; i++) { setup?.Invoke(i); operation(i); cleanup?.Invoke(i); }
                for (int i = 0; i < 1000; i++) { setup?.Invoke(i); long t0 = Stopwatch.GetTimestamp(); operation(i); long ns = Matcher.Ns(Stopwatch.GetTimestamp() - t0); cleanup?.Invoke(i); samples.Add($"{name},{i},{ns}"); sampleIndex++; elapsed += ns; }
            }
        }
        catch (Exception e) { Files.SaveJson(dir + "/failure.json", new { status = "FAILED", excluded_reason = "crash", error = e.Message }); File.AppendAllText(dir + "/stderr.log", e.Message); throw; }
    }
}
