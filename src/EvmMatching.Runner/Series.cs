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
}
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
        var templates = plan.ConfigPaths.Select(Files.ReadJson<ExperimentConfig>).ToArray();
        if (templates.Any(c => c.Partition != "measurement") && !plan.SeriesId.StartsWith("pilot", StringComparison.Ordinal)) throw new InvalidDataException("Final series must use measurement partition.");
        var identity = templates[0] with { SeriesId = plan.SeriesId }; identity.Validate();
        string directory = $"results/raw/{plan.SeriesId}";
        if (Directory.Exists(directory)) throw new IOException("Series already exists. Create a new seriesId; never overwrite previous runs.");
        Directory.CreateDirectory(directory); File.Copy(planPath, directory + "/series_plan.json");
        string cfgDirectory = $"configs/generated/{plan.SeriesId}"; Directory.CreateDirectory(cfgDirectory);
        var random = new Random(plan.OrderSeed); var schedule = new List<object>(); int failed = 0;
        for (int replicate = 1; replicate <= plan.Replicates; replicate++)
        {
            foreach (var template in templates)
            {
                var methods = (plan.MethodsByExperiment.TryGetValue(template.Experiment, out var selected) ? selected : plan.Methods).ToArray(); random.Shuffle(methods);
                foreach (string method in methods)
                {
                    var config = template with { SeriesId = plan.SeriesId, Method = method, Replicate = replicate, Seed = template.Seed, WarmupSeconds = plan.WarmupSeconds ?? template.WarmupSeconds, MeasureSeconds = plan.MeasureSeconds ?? template.MeasureSeconds };
                    string configPath = $"{cfgDirectory}/{config.RunId}.json"; config.Validate(); Files.SaveJson(configPath, config);
                    schedule.Add(new { replicate, config.Scenario, method, seed = config.Seed, config_path = configPath });
                }
            }
        }
        Files.SaveJson(directory + "/method_order.json", schedule);
        foreach (var item in schedule)
        {
            var json = JsonSerializer.SerializeToElement(item); string path = json.GetProperty("config_path").GetString()!;
            if (Launch(Files.ReadJson<ExperimentConfig>(path), path) != 0) { failed++; break; }
        }
        Files.SaveJson(directory + "/series_status.json", new { series_id = plan.SeriesId, purpose = plan.Purpose, planned_runs = schedule.Count, successful_runs = Directory.GetFiles(directory, "SUCCESS", SearchOption.AllDirectories).Length, failed_runs = Directory.GetFiles(directory, "failure.json", SearchOption.AllDirectories).Length, status = failed == 0 ? "COMPLETE" : "STOPPED_AFTER_FAILURE" });
        Handoff(); return failed == 0 ? 0 : 1;
    }
    public static void Handoff()
    {
        var inputFiles = new[] { "src", "tests", "scripts", "configs", "data" }.Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*", SearchOption.AllDirectories)).Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj")).Concat(new[] { "README.md", "global.json", "Directory.Build.props", "EvmAddressExperiment.sln" }.Where(File.Exists)).Order().ToArray();
        var runs = Directory.GetFiles("results/raw", "config.json", SearchOption.AllDirectories).Select(p => Path.GetDirectoryName(p)!).Order().ToArray();
        string codeVersion = Files.Sha(typeof(Series).Assembly.Location);
        Files.SaveJson("handoff_manifest.json", new { generated_utc = DateTimeOffset.UtcNow, code_version = codeVersion, scope = "See EXPERIMENT_STATUS.md for completed work and full-study limitations.", series_ids = Directory.GetDirectories("results/raw").Select(Path.GetFileName).ToArray(), directories = runs.Select(d => new { path = d.Replace('\\', '/'), status = File.Exists(d + "/SUCCESS") ? "SUCCESS" : "INCOMPLETE_OR_FAILED" }), successful_runs = runs.Count(d => File.Exists(d + "/SUCCESS")), failed_runs = runs.Count(d => File.Exists(d + "/failure.json")), incomplete_runs = runs.Count(d => !File.Exists(d + "/SUCCESS") && !File.Exists(d + "/failure.json")), files = inputFiles.Concat(Directory.GetFiles("results/raw", "*", SearchOption.AllDirectories)).Distinct().Order().Select(p => new { path = p.Replace('\\', '/'), sha256 = Files.Sha(p) }) });
    }
}
