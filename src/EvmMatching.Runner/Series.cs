using System.Diagnostics;
using System.Text.Json;
using EvmMatching.Data;

namespace EvmMatching.Runner;

internal sealed record SeriesPlan
{
    public string SeriesId { get; init; } = "qualification_v1";
    public string Purpose { get; init; } = "Limited real-data qualification; not the full E0-E6 experiment.";
    public string[] ConfigPaths { get; init; } = [];
    public string[] Methods { get; init; } = ["M1", "M2", "M3", "M4", "M5", "M6"];
    public Dictionary<string, string[]> MethodsByExperiment { get; init; } = [];
    public int Replicates { get; init; } = 30;
    public int OrderSeed { get; init; } = 240001;
    public double? WarmupSeconds { get; init; }
    public double? MeasureSeconds { get; init; }
    public int BatchSize { get; init; }
}
internal sealed record ScheduledRun(int Replicate, string Scenario, string Method, int Seed, string ConfigPath, string ConfigSha256);
internal static class Series
{
    public static int Launch(ExperimentConfig config, string path)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(Series).Assembly.Location); start.ArgumentList.Add("run-one"); start.ArgumentList.Add(path);
        start.Environment["DOTNET_gcServer"] = config.GcMode == "server" ? "1" : "0";
        using var process = Process.Start(start) ?? throw new IOException("Could not start child process.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(); Task.WaitAll(stdout, stderr);
        Console.Write(stdout.Result); if (stderr.Result.Length > 0) Console.Error.Write(stderr.Result);
        return process.ExitCode;
    }
    public static int Execute(string planPath)
    {
        var plan = Files.ReadJson<SeriesPlan>(planPath);
        if (plan.ConfigPaths.Length == 0 || plan.Methods.Length == 0 || plan.Replicates < 1) throw new InvalidDataException("Empty series.");
        string directory = $"results/raw/{plan.SeriesId}";
        string planHashPath = directory + "/series_plan.sha256";
        List<ScheduledRun> schedule;
        if (Directory.Exists(directory))
        {
            if (!File.Exists(planHashPath) || !File.ReadAllText(planHashPath).Trim().Equals(Files.Sha(planPath), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Existing series plan differs or has no resume checkpoint. Preserve it and use a new seriesId.");
            if (!File.Exists(directory + "/method_order.json")) throw new InvalidDataException("Series checkpoint is missing method_order.json.");
            schedule = Files.ReadJson<List<ScheduledRun>>(directory + "/method_order.json");
            foreach (var item in schedule)
            {
                if (!File.Exists(item.ConfigPath) || !Files.Sha(item.ConfigPath).Equals(item.ConfigSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Generated run configuration changed or is missing: " + item.ConfigPath);
            }
        }
        else
        {
            var templates = plan.ConfigPaths.Select(Files.ReadJson<ExperimentConfig>).ToArray();
            if (templates.Any(c => c.Partition != "measurement") && !plan.SeriesId.StartsWith("pilot", StringComparison.Ordinal)) throw new InvalidDataException("Final series must use measurement partition.");
            var identity = templates[0] with { SeriesId = plan.SeriesId }; identity.Validate();
            Directory.CreateDirectory(directory); File.Copy(planPath, directory + "/series_plan.json");
            File.WriteAllText(planHashPath, Files.Sha(planPath) + Environment.NewLine);
            string cfgDirectory = $"configs/generated/{plan.SeriesId}"; Directory.CreateDirectory(cfgDirectory);
            var random = new Random(plan.OrderSeed); schedule = [];
            for (int replicate = 1; replicate <= plan.Replicates; replicate++)
            {
                foreach (var template in templates)
                {
                    var methods = (plan.MethodsByExperiment.TryGetValue(template.Experiment, out var selected) ? selected : plan.Methods).ToArray(); random.Shuffle(methods);
                    foreach (string method in methods)
                    {
                        var config = template with { SeriesId = plan.SeriesId, Method = method, Replicate = replicate, Seed = template.Seed, WarmupSeconds = plan.WarmupSeconds ?? template.WarmupSeconds, MeasureSeconds = plan.MeasureSeconds ?? template.MeasureSeconds };
                        string configPath = $"{cfgDirectory}/{config.RunId}.json"; config.Validate(); Files.SaveJson(configPath, config);
                        schedule.Add(new(replicate, config.Scenario, method, config.Seed, configPath, Files.Sha(configPath)));
                    }
                }
            }
            Files.SaveJson(directory + "/method_order.json", schedule);
        }

        var runs = schedule.Select(item => (Item: item, Config: Files.ReadJson<ExperimentConfig>(item.ConfigPath))).ToArray();
        int successful = runs.Count(r => File.Exists(RunDirectory(r.Config) + "/SUCCESS"));
        int launched = 0, failed = 0; string status = "COMPLETE";
        foreach (var run in runs)
        {
            var item = run.Item; var config = run.Config;
            string runDirectory = $"results/raw/{config.SeriesId}/{config.Experiment}/{config.Scenario}/{config.RunId}";
            if (File.Exists(runDirectory + "/SUCCESS")) continue;
            if (File.Exists(runDirectory + "/failure.json")) { failed++; status = "STOPPED_AFTER_FAILURE"; break; }
            if (Directory.Exists(runDirectory)) { failed++; status = "STOPPED_WITH_INCOMPLETE_RUN"; break; }
            if (Launch(config, item.ConfigPath) != 0 || !File.Exists(runDirectory + "/SUCCESS")) { failed++; status = "STOPPED_AFTER_FAILURE"; break; }
            launched++; successful++;
            if (plan.BatchSize > 0 && launched >= plan.BatchSize && successful < schedule.Count) { status = "PAUSED"; break; }
        }
        int failures = Directory.GetFiles(directory, "failure.json", SearchOption.AllDirectories).Length;
        Files.SaveJson(directory + "/series_status.json", new { series_id = plan.SeriesId, purpose = plan.Purpose, planned_runs = schedule.Count, successful_runs = successful, failed_runs = failures, runs_in_this_batch = launched, batch_size = plan.BatchSize, status });
        if (status == "COMPLETE") Handoff();
        Console.WriteLine($"Series {plan.SeriesId}: {status}; {successful}/{schedule.Count} runs successful; {launched} run(s) in this batch.");
        return failed == 0 ? 0 : 1;
    }
    private static string RunDirectory(ExperimentConfig c) => $"results/raw/{c.SeriesId}/{c.Experiment}/{c.Scenario}/{c.RunId}";
    public static void Handoff()
    {
        var inputFiles = new[] { "src", "tests", "scripts", "configs", "data" }.Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*", SearchOption.AllDirectories)).Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj")).Concat(new[] { "README.md", "global.json", "Directory.Build.props", "EvmAddressExperiment.sln" }.Where(File.Exists)).Order().ToArray();
        var runs = Directory.GetFiles("results/raw", "config.json", SearchOption.AllDirectories).Select(p => Path.GetDirectoryName(p)!).Order().ToArray();
        string codeVersion = Files.Sha(typeof(Series).Assembly.Location);
        Files.SaveJson("handoff_manifest.json", new { generated_utc = DateTimeOffset.UtcNow, code_version = codeVersion, scope = "See EXPERIMENT_STATUS.md for completed work and full-study limitations.", series_ids = Directory.GetDirectories("results/raw").Select(Path.GetFileName).ToArray(), directories = runs.Select(d => new { path = d.Replace('\\', '/'), status = File.Exists(d + "/SUCCESS") ? "SUCCESS" : "INCOMPLETE_OR_FAILED" }), successful_runs = runs.Count(d => File.Exists(d + "/SUCCESS")), failed_runs = runs.Count(d => File.Exists(d + "/failure.json")), incomplete_runs = runs.Count(d => !File.Exists(d + "/SUCCESS") && !File.Exists(d + "/failure.json")), files = inputFiles.Concat(Directory.GetFiles("results/raw", "*", SearchOption.AllDirectories)).Distinct().Order().Select(p => new { path = p.Replace('\\', '/'), sha256 = Files.Sha(p) }) });
    }
}
