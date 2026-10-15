using System.Diagnostics;

namespace EvmMatching.Core;

public readonly record struct SearchCounters(long ExactChecks, long FilterMaybe, long FilterFalsePositive);
public sealed record MatcherEvent(string Kind, string Reason, long StartNs, long EndNs, string Mode);
public sealed class Matcher : IAddressMatcher, IDisposable
{
    private readonly ReaderWriterLockSlim gate = new();
    private HashSet<Address20> h = [];
    private HashSet<Address20> hd = [];
    private CountingBloom? fd;
    private StaticBloom? fs;
    private readonly int capacity;
    private readonly double threshold;
    private readonly ControllerOptions options;
    private readonly AdaptiveController? controller;
    private SearchMode active;
    private Task? rebuild;
    private int rebuilding;
    private long exactChecks, maybe, falsePositives;
    private long lastRebuild;
    private long rebuildCount, rebuildTime, switchCount, switchTime;
    private long origin = Stopwatch.GetTimestamp();
    private readonly List<MatcherEvent> events = [];
    public string Method { get; }
    public SearchMode ActiveMode => active;
    public IReadOnlyList<MatcherEvent> Events { get { lock (events) return events.ToArray(); } }
    public long RebuildCount => Interlocked.Read(ref rebuildCount);
    public long RebuildTimeNs => Interlocked.Read(ref rebuildTime);
    public long SwitchCount => Interlocked.Read(ref switchCount);
    public long SwitchTimeNs => Interlocked.Read(ref switchTime);
    public double LastRebuildCostNs { get; private set; } = 1_000_000;
    public int DeltaCount { get { gate.EnterReadLock(); try { return hd.Count; } finally { gate.ExitReadLock(); } } }
    public long EstimatedBytes { get { gate.EnterReadLock(); try { return h.Count * 40L + hd.Count * 40L + (fd?.Bytes ?? 0) + (fs?.Bytes ?? 0); } finally { gate.ExitReadLock(); } } }
    public Matcher(string method, int filterCapacity = 1_250_000, double rebuildThreshold = .05, ControllerOptions? options = null)
    {
        if (method is not ("M1" or "M2" or "M3" or "M4" or "M5" or "M6")) throw new ArgumentException("Unknown method.");
        Method = method; capacity = filterCapacity; threshold = rebuildThreshold; this.options = options ?? new();
        active = method == "M2" ? SearchMode.S1 : method == "M3" ? SearchMode.S2 : SearchMode.S0;
        if (method is "M4" or "M5" or "M6") controller = new(this.options, method == "M5");
    }
    public void Initialize(ReadOnlySpan<Address20> initialSet)
    {
        WaitForRebuild(); gate.EnterWriteLock();
        try
        {
            h = new(initialSet.ToArray()); hd = [];
            if (Method is "M2" or "M4" or "M5" or "M6") { fd = new(capacity); foreach (var a in h) fd.Insert(a); }
            if (Method != "M1" && Method != "M2") { long start = Stopwatch.GetTimestamp(); fs = new(h, h.Count); LastRebuildCostNs = Ns(Stopwatch.GetTimestamp() - start); }
        }
        finally { gate.ExitWriteLock(); }
    }
    public bool Contains(Address20 address)
    {
        gate.EnterReadLock();
        try
        {
            bool filtered = active != SearchMode.S0;
            if (active == SearchMode.S1 && !fd!.MightContain(address)) return false;
            if (active == SearchMode.S2 && !hd.Contains(address) && !fs!.MightContain(address)) return false;
            Interlocked.Increment(ref exactChecks);
            if (filtered) Interlocked.Increment(ref maybe);
            bool result = h.Contains(address);
            if (filtered && !result) Interlocked.Increment(ref falsePositives);
            return result;
        }
        finally { gate.ExitReadLock(); }
    }
    // Shadow calls are excluded from primary counters but included in measured wall time.
    public bool Probe(Address20 a, SearchMode mode)
    {
        gate.EnterReadLock();
        try { return mode switch { SearchMode.S0 => h.Contains(a), SearchMode.S1 => fd!.MightContain(a) && h.Contains(a), _ => (hd.Contains(a) || fs!.MightContain(a)) && h.Contains(a) }; }
        finally { gate.ExitReadLock(); }
    }
    public void Add(Address20 address)
    {
        gate.EnterWriteLock();
        try { if (h.Contains(address)) return; fd?.Insert(address); if (fs is not null) hd.Add(address); h.Add(address); }
        finally { gate.ExitWriteLock(); }
        MaybeFixedThresholdRebuild();
    }
    public void Remove(Address20 address)
    {
        gate.EnterWriteLock();
        try { if (!h.Remove(address)) return; fd?.Delete(address); hd.Remove(address); }
        finally { gate.ExitWriteLock(); }
    }
    private void MaybeFixedThresholdRebuild()
    {
        if (Method != "M3") return;
        gate.EnterReadLock(); bool due;
        try { due = hd.Count >= Math.Max(1, h.Count * threshold); } finally { gate.ExitReadLock(); }
        if (due) StartRebuild("delta_threshold");
    }
    public bool StartRebuild(string reason)
    {
        gate.EnterReadLock();
        try
        {
            long retained = h.Count * 40L + hd.Count * 40L + (fd?.Bytes ?? 0) + (fs?.Bytes ?? 0);
            long transient = h.Count * 82L; // snapshot, reconciled delta, and new bit array; conservative estimate
            if (retained + transient > options.MemoryLimitBytes) { long now = Stopwatch.GetTimestamp(); Log("rebuild_skipped", "transient_memory_budget", now, now); return false; }
        }
        finally { gate.ExitReadLock(); }
        if (fs is null || Interlocked.CompareExchange(ref rebuilding, 1, 0) != 0) return false;
        rebuild = Task.Run(() =>
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                HashSet<Address20> snapshot;
                gate.EnterReadLock(); try { snapshot = new(h); } finally { gate.ExitReadLock(); }
                var replacement = new StaticBloom(snapshot, snapshot.Count);
                gate.EnterWriteLock();
                try
                {
                    // Reconcile every update made during construction under the publication lock.
                    var delta = new HashSet<Address20>(h); delta.ExceptWith(snapshot);
                    fs = replacement; hd = delta;
                }
                finally { gate.ExitWriteLock(); }
                long end = Stopwatch.GetTimestamp(); LastRebuildCostNs = Ns(end - start);
                Interlocked.Increment(ref rebuildCount); Interlocked.Add(ref rebuildTime, Ns(end - start));
                Interlocked.Exchange(ref lastRebuild, end); Log("rebuild", reason, start, end);
            }
            finally { Volatile.Write(ref rebuilding, 0); }
        });
        return true;
    }
    public void Control(IReadOnlyList<ModeEstimate> estimates, double rebuildSavingNsPerSecond)
    {
        if (controller is null) return;
        long start = Stopwatch.GetTimestamp(); var next = controller.Decide(estimates, Math.Max(100, SwitchCount == 0 ? 100 : SwitchTimeNs / SwitchCount));
        bool changed = next != active;
        gate.EnterWriteLock(); try { active = next; } finally { gate.ExitWriteLock(); }
        long end = Stopwatch.GetTimestamp();
        if (changed) { Interlocked.Increment(ref switchCount); Interlocked.Add(ref switchTime, Ns(end - start)); }
        Log(changed ? "switch" : "decision", controller.LastReason, start, end);
        if (Method == "M6")
        {
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastRebuild)).TotalSeconds >= options.FixedRebuildSeconds) StartRebuild("fixed_period");
        }
        else if (DeltaCount > 0 && controller.ShouldRebuild(rebuildSavingNsPerSecond, LastRebuildCostNs)) StartRebuild("predicted_rebuild_payback");
    }
    private void Log(string kind, string reason, long start, long end) { lock (events) events.Add(new(kind, reason, Ns(start - origin), Ns(end - origin), active.ToString())); }
    public SearchCounters Counters => new(Interlocked.Read(ref exactChecks), Interlocked.Read(ref maybe), Interlocked.Read(ref falsePositives));
    public void ResetMetrics()
    {
        WaitForRebuild(); exactChecks = maybe = falsePositives = rebuildCount = rebuildTime = switchCount = switchTime = 0;
        lock (events) events.Clear(); origin = Stopwatch.GetTimestamp(); lastRebuild = origin;
    }
    public void WaitForRebuild() => rebuild?.GetAwaiter().GetResult();
    public static long Ns(long ticks) => (long)(ticks * (1_000_000_000.0 / Stopwatch.Frequency));
    public void Dispose() { WaitForRebuild(); gate.Dispose(); }
}
