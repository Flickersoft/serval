namespace Serval.Ai;

/// <summary>
/// Decides how much inference each camera may spend, against one budget shared by all of them.
///
/// <para>The budget is global because there is one detector and, on an accelerator, one device:
/// throughput is a property of the host, and a per-camera ceiling cannot express it. Ten cameras
/// each allowed five frames a second is fifty inferences a second whatever the host can manage.</para>
///
/// <para><b>The floor is never what gets shed.</b> A whole-frame pass every
/// <see cref="RegionOptions.FloorSeconds"/> is what keeps presence a measurement rather than an
/// inference from silence, and it is tiny — a fifth of an inference per second per camera at the
/// default. It is reserved first and motion crops are admitted from what is left, so a host under
/// pressure degrades into the behaviour it would have with regions switched off.</para>
///
/// <para>Fair share rather than first-come: the budget divided by how many cameras are active, so a
/// driveway onto a main road cannot spend the back door's inference. Unused share does not
/// accumulate beyond a small burst, or a camera quiet for a minute would arrive able to monopolise
/// the detector.</para>
///
/// <para>Pure and clock-injected, so the policy can be pinned in tests without a model, a camera or
/// a wait.</para>
/// </summary>
public sealed class InferenceScheduler(double budgetPerSecond, double burstSeconds = 2.0)
{
    private readonly Dictionary<string, Bucket> _cameras = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Inferences a second across every camera, or zero for no ceiling.</summary>
    public double BudgetPerSecond { get; private set; } = Math.Max(budgetPerSecond, 0);

    /// <summary>Inferences dropped for want of budget, for the log and for diagnostics.</summary>
    public long Shed { get; private set; }

    /// <summary>
    /// Revises the budget when the host's capacity has actually changed — a backend whose lanes are
    /// hardware losing one, or getting it back.
    ///
    /// <para><b>Set on this instance rather than by building a new scheduler</b>, because the per-camera
    /// token buckets are rate accounting that should survive a capacity change. Discarding them would
    /// hand every camera a fresh burst allowance at the moment the host just got smaller, which is the
    /// opposite of what a capacity drop calls for.</para>
    ///
    /// <para><see cref="Admit"/> reads the share on every call, so a revised figure takes effect
    /// immediately with nothing else to update.</para>
    /// </summary>
    public void Rebudget(double budgetPerSecond)
    {
        lock (_gate)
        {
            BudgetPerSecond = Math.Max(budgetPerSecond, 0);
        }
    }

    /// <summary>
    /// Admits as much of one camera's plan as the budget allows, cheapest to lose last.
    ///
    /// The returned list is a prefix of what the planner wanted in priority order, never a
    /// reordering: a caller runs what it is given and reports nothing about what it was not.
    /// </summary>
    /// <param name="cameraId">Which camera is asking. Registers it as active.</param>
    /// <param name="planned">What the planner proposed for this frame.</param>
    /// <param name="now">The frame's own timestamp, not the wall clock — the same media clock
    /// everything else is dated from, so a replay is scheduled exactly as the live stream was.</param>
    public IReadOnlyList<PlannedRegion> Admit(
        string cameraId, IReadOnlyList<PlannedRegion> planned, DateTimeOffset now)
    {
        if (planned.Count == 0)
        {
            return planned;
        }

        if (BudgetPerSecond <= 0)
        {
            // No ceiling configured: every plan runs, as on a host sized by hand.
            Touch(cameraId, now);
            return planned;
        }

        lock (_gate)
        {
            Bucket bucket = Register(cameraId, now);

            // Reserved outside the budget rather than paid for out of it, because both are coverage
            // guarantees rather than opportunistic looks. Bounded at one inference per camera per frame:
            // a whole-frame floor runs once per floor interval, and a tile sweep emits exactly one tile
            // per frame — so neither can be what overruns a host, and MaxPerFrame cannot silently cap a
            // guarantee either.
            int reserved = planned.Count(IsReserved);

            double share = BudgetPerSecond / Math.Max(_cameras.Count, 1);
            bucket.Refill(now, share, burstSeconds);

            int discretionary = planned.Count - reserved;
            int affordable = Math.Min(discretionary, (int)Math.Floor(bucket.Tokens));

            if (affordable >= discretionary)
            {
                bucket.Take(discretionary);
                return planned;
            }

            bucket.Take(affordable);
            Shed += discretionary - affordable;

            // Reserved regions first, then as many of the rest as were afforded. Both groups keep
            // the planner's own order within themselves.
            //
            // Partitioned by the same predicate the count above uses, and that is deliberate: counting
            // a region as reserved and then shedding it as discretionary is a silent halving of the
            // coverage guarantee, and it is exactly what happened when these were two separate
            // conditions.
            List<PlannedRegion> admitted = [.. planned.Where(IsReserved)];

            admitted.AddRange(planned.Where(static region => !IsReserved(region)).Take(affordable));

            return admitted;
        }
    }

    /// <summary>
    /// Whether a region is a coverage guarantee rather than an opportunistic look.
    ///
    /// The floor and a tile of a floor sweep are both promises that the frame gets examined; motion and
    /// track crops are attempts to spend spare capacity well. Only the second kind is ever shed.
    /// </summary>
    private static bool IsReserved(PlannedRegion region) =>
        region.Reason is RegionReason.Floor or RegionReason.Tile;

    /// <summary>
    /// Forgets a camera, so the cameras still running get its share. Without this a server that has
    /// had twenty cameras over its life divides the budget twenty ways forever.
    /// </summary>
    public void Forget(string cameraId)
    {
        lock (_gate)
        {
            _cameras.Remove(cameraId);
        }
    }

    private void Touch(string cameraId, DateTimeOffset now)
    {
        lock (_gate)
        {
            Register(cameraId, now);
        }
    }

    private Bucket Register(string cameraId, DateTimeOffset now)
    {
        if (_cameras.TryGetValue(cameraId, out Bucket? existing))
        {
            return existing;
        }

        // A camera arriving mid-run starts empty rather than full: a full bucket would let it fire
        // its whole burst immediately, which is exactly the spike a restart storm produces.
        var bucket = new Bucket(now);
        _cameras[cameraId] = bucket;
        return bucket;
    }

    /// <summary>One camera's share of the budget, as a token bucket.</summary>
    private sealed class Bucket(DateTimeOffset start)
    {
        private DateTimeOffset _last = start;

        public double Tokens { get; private set; }

        public void Refill(DateTimeOffset now, double perSecond, double burstSeconds)
        {
            double elapsed = (now - _last).TotalSeconds;
            if (elapsed <= 0)
            {
                return;
            }

            _last = now;

            // Capped, so quiet time does not become credit. A camera that has seen nothing for a
            // minute must not arrive able to spend a minute of everyone's budget at once.
            Tokens = Math.Min(Tokens + (elapsed * perSecond), perSecond * Math.Max(burstSeconds, 1));
        }

        public void Take(int count) => Tokens = Math.Max(0, Tokens - count);
    }
}
