using EvmMatching.Core;

namespace EvmMatching.Data;

public sealed record ExperimentConfig
{
    public string ConfigVersion { get; init; } = "1.0";
    public string SeriesId { get; init; } = "pilot_v1";
    public string Experiment { get; init; } = "pilot";
    public string Scenario { get; init; } = "real-mainnet";
    public string Method { get; init; } = "M1";
    public int Replicate { get; init; } = 1;
    public int Seed { get; init; } = 240001;
    public string DatasetPath { get; init; } = "";
    public string DatasetSha256 { get; init; } = "";
    public string WatchlistPath { get; init; } = "";
    public string WatchlistSha256 { get; init; } = "";
    public string UpdatesPath { get; init; } = "";
    public string UpdatesSha256 { get; init; } = "";
    public int WSize { get; init; }
    public double TargetHitRate { get; init; } = .01;
    public double UpdateRatePerSecond { get; init; }
    public double DeleteRatio { get; init; } = .5;
    public string DynamicFilterType { get; init; } = "CountingBloom32";
    public string StaticFilterType { get; init; } = "Bloom12bits7hashes";
    public int DynamicFilterCapacity { get; init; } = 1250000;
    public double M3RebuildThreshold { get; init; } = .05;
    public int ControlIntervalMs { get; init; } = 1000;
    public double ShadowRate { get; init; } = .01;
    public double PredictionHorizon { get; init; } = 10;
    public int HysteresisIntervals { get; init; } = 3;
    public double SwitchMarginNs { get; init; }
    public double RebuildHorizon { get; init; } = 30;
    public double RebuildMarginNs { get; init; }
    public double FixedRebuildSeconds { get; init; } = 10;
    public long MemoryLimitMb { get; init; } = 4096;
    public double LatencyP99LimitMs { get; init; } = 5;
    public double WarmupSeconds { get; init; } = 30;
    public double MeasureSeconds { get; init; } = 300;
    public int Threads { get; init; } = 1;
    public string GcMode { get; init; } = "server";
    public string Partition { get; init; } = "measurement";
    public string ReplayPolicy { get; init; } = "finite";
    public double QueryRatePerSecond { get; init; } = 10000;
    [System.Text.Json.Serialization.JsonIgnore]
    public string RunId => $"{Experiment}_{Scenario}_{Method}_r{Replicate:00}_s{Seed}";
    [System.Text.Json.Serialization.JsonIgnore]
    public ControllerOptions Controller => new()
    {
        HysteresisIntervals = HysteresisIntervals, PredictionHorizon = PredictionHorizon, SwitchMarginNs = SwitchMarginNs,
        RebuildHorizon = RebuildHorizon, RebuildMarginNs = RebuildMarginNs, FixedRebuildSeconds = FixedRebuildSeconds,
        MemoryLimitBytes = MemoryLimitMb * 1024 * 1024, LatencyLimitNs = LatencyP99LimitMs * 1_000_000
    };
    public void Validate()
    {
        if (Method is not ("M1" or "M2" or "M3" or "M4" or "M5" or "M6")) throw new InvalidDataException("Unknown method.");
        foreach (var name in new[] { SeriesId, Experiment, Scenario }) if (name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))) throw new InvalidDataException("Unsafe identifier.");
        foreach (var path in new[] { DatasetPath, WatchlistPath, UpdatesPath }) if (Path.IsPathRooted(path) || path.Split('/', '\\').Contains("..")) throw new InvalidDataException("Input paths must be relative and remain inside the repository.");
        if (WSize < 1 || DynamicFilterCapacity < WSize || Threads < 1 || Threads > Environment.ProcessorCount || ControlIntervalMs < 1 || WarmupSeconds < 0 || MeasureSeconds <= 0 || HysteresisIntervals < 1 || ShadowRate <= 0 || ShadowRate > 1 || TargetHitRate < 0 || TargetHitRate > 1 || MemoryLimitMb < 1 || LatencyP99LimitMs <= 0 || M3RebuildThreshold <= 0 || FixedRebuildSeconds <= 0) throw new InvalidDataException("Invalid experiment parameters.");
        if (GcMode is not ("server" or "workstation") || DynamicFilterType != "CountingBloom32" || StaticFilterType != "Bloom12bits7hashes") throw new InvalidDataException("Unsupported GC or filter type.");
        if (ReplayPolicy is not ("finite" or "scheduled") || !double.IsFinite(QueryRatePerSecond) || QueryRatePerSecond <= 0) throw new InvalidDataException("Unsupported replay policy or query rate.");
    }
}
