namespace EvmMatching.Core;

public enum SearchMode { S0, S1, S2 }
public sealed record ControllerOptions
{
    public int HysteresisIntervals { get; init; } = 3;
    public double PredictionHorizon { get; init; } = 10;
    public double SwitchMarginNs { get; init; }
    public double RebuildHorizon { get; init; } = 30;
    public double RebuildMarginNs { get; init; }
    public long MemoryLimitBytes { get; init; } = 4096L * 1024 * 1024;
    public double LatencyLimitNs { get; init; } = 5_000_000;
    public double FixedRebuildSeconds { get; init; } = 10;
}
public readonly record struct ModeEstimate(SearchMode Mode, double CostNsPerSecond, long MemoryBytes, double P99Ns);
public sealed class AdaptiveController(ControllerOptions options, bool noHysteresis = false)
{
    private SearchMode? pending;
    private int consecutive;
    public SearchMode Active { get; private set; }
    public string LastReason { get; private set; } = "initial";
    public SearchMode Decide(IReadOnlyList<ModeEstimate> estimates, double switchCostNs)
    {
        var available = estimates.Where(e => e.MemoryBytes <= options.MemoryLimitBytes && e.P99Ns <= options.LatencyLimitNs).OrderBy(e => e.CostNsPerSecond).ToArray();
        if (available.Length == 0) throw new InvalidOperationException("resource_constraints: no mode is available");
        var best = available[0]; var current = estimates.Single(e => e.Mode == Active);
        bool currentUnavailable = current.MemoryBytes > options.MemoryLimitBytes || current.P99Ns > options.LatencyLimitNs;
        bool worthwhile = best.Mode != Active && (currentUnavailable || (current.CostNsPerSecond - best.CostNsPerSecond) * options.PredictionHorizon > switchCostNs + options.SwitchMarginNs);
        if (!worthwhile) { pending = null; consecutive = 0; LastReason = "keep_active"; return Active; }
        consecutive = pending == best.Mode ? consecutive + 1 : 1; pending = best.Mode;
        if (currentUnavailable || consecutive >= (noHysteresis ? 1 : options.HysteresisIntervals))
        {
            Active = best.Mode; LastReason = currentUnavailable ? "active_mode_unavailable" : "predicted_saving_exceeds_switch_cost";
            pending = null; consecutive = 0;
        }
        else LastReason = "hysteresis_pending";
        return Active;
    }
    public bool ShouldRebuild(double savingNsPerSecond, double costNs) => savingNsPerSecond * options.RebuildHorizon > costNs + options.RebuildMarginNs;
}
