using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using FlashSale.Infrastructure.Messaging;

namespace Order.Api;

/// <summary>
/// In-process API metrics (Phase III). Real <see cref="Meter"/> instruments, so
/// any OpenTelemetry exporter can consume them later, plus a small JSON snapshot
/// at <c>/internal/metrics</c> for the interview demo and for tests.
/// </summary>
/// <remarks>
/// Deliberately NOT a Prometheus exporter. The requirement is a scrape-free,
/// dependency-free view of what this process has observed, and a
/// <see cref="MeterListener"/> aggregates exactly the instruments an exporter
/// would — so there is still only one source of truth for the numbers.
/// </remarks>
public sealed class ApiMetrics : IDisposable
{
    public const string MeterName = "FlashSale.Api";
    public const string Version = "1.0.0";

    /// <summary>
    /// Meters whose instruments this snapshot aggregates.
    ///
    /// <para>
    /// The outbox dispatcher (ADR-019) runs inside THIS process as well as in
    /// the worker, and its counters live on the <c>FlashSale.Worker</c> meter
    /// (<c>WorkerTelemetry</c>). Filtering on the API meter only would have
    /// kept them out of the scrape, so the worker meter is aggregated here
    /// too: an operator sees one view of the outbox pipeline instead of two
    /// half-blind ones.
    /// </para>
    /// </summary>
    private static readonly string[] AggregatedMeters =
    [
        MeterName,
        WorkerTelemetry.MeterName
    ];

    private static readonly Meter Meter = new(MeterName, Version);

    // ---------------------------------------------------------------
    // Order pipeline. Counters are named for the question they answer, and
    // each one is incremented at exactly ONE place in the API.
    // ---------------------------------------------------------------

    /// <summary>Orders accepted into the queue, or processed synchronously as a fallback.</summary>
    public static readonly Counter<long> OrdersAccepted = Meter.CreateCounter<long>(
        "flashsale.orders.accepted",
        unit: "{order}",
        description: "Orders accepted for fulfillment.");

    /// <summary>Orders refused, tagged with the reason.</summary>
    public static readonly Counter<long> OrdersRejected = Meter.CreateCounter<long>(
        "flashsale.orders.rejected",
        unit: "{order}",
        description: "Orders refused, tagged by reason.");

    /// <summary>Orders that ended up persisted as Completed.</summary>
    public static readonly Counter<long> OrdersCompleted = Meter.CreateCounter<long>(
        "flashsale.orders.completed",
        unit: "{order}",
        description: "Orders persisted with status Completed.");

    /// <summary>
    /// The conditional UPDATE matched zero rows. Not an error: it is the
    /// oversell guard working (ADR-002). This is the metric to watch during a
    /// flash sale, which is why it is separate from <see cref="OrdersRejected"/>.
    /// </summary>
    public static readonly Counter<long> StockDrift = Meter.CreateCounter<long>(
        "flashsale.orders.stock_drift",
        unit: "{event}",
        description: "Stock guard fired: conditional UPDATE matched zero rows.");

    // ---------------------------------------------------------------
    // Auth (ADR-013). Failures are one counter with a reason tag rather than
    // four counters: the alerting question is "is the rate up?", and the tag
    // answers "why?" without a cardinality explosion.
    // ---------------------------------------------------------------

    public static readonly Counter<long> AuthRegistrations = Meter.CreateCounter<long>(
        "flashsale.auth.registrations",
        unit: "{request}",
        description: "Successful registrations.");

    public static readonly Counter<long> AuthLogins = Meter.CreateCounter<long>(
        "flashsale.auth.logins",
        unit: "{request}",
        description: "Successful logins.");

    public static readonly Counter<long> AuthRefreshes = Meter.CreateCounter<long>(
        "flashsale.auth.refreshes",
        unit: "{request}",
        description: "Refresh-token rotations that won the compare-and-swap.");

    public static readonly Counter<long> AuthFailures = Meter.CreateCounter<long>(
        "flashsale.auth.failures",
        unit: "{request}",
        description: "Rejected auth attempts, tagged by reason (invalid_credentials, replay, validation...).");

    /// <summary>Server-side request duration, tagged with method + route TEMPLATE.</summary>
    public static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        "flashsale.http.request.duration",
        unit: "ms",
        description: "Server-side request duration by method and route template.");

    // ---------------------------------------------------------------

    private readonly ConcurrentDictionary<string, InstrumentAggregate> _aggregates = new(StringComparer.Ordinal);
    private readonly MeterListener _listener;
    private readonly ObservableGauge<long> _inFlightGauge;
    private long _inFlight;

    public ApiMetrics()
    {
        // Observable gauge, not a counter: "how many requests are in the process
        // right now" is a level, not a total, and it is the saturation signal an
        // operator wants during a flash sale.
        _inFlightGauge = Meter.CreateObservableGauge(
            "flashsale.http.in_flight",
            () => Interlocked.Read(ref _inFlight),
            unit: "{request}",
            description: "Requests currently being handled by this process.");

        // The outbox gauges live on the worker meter (WorkerTelemetry). Touching
        // one of its instruments here forces the meter to initialise in THIS
        // process, so the outbox series are present in the very first scrape
        // instead of appearing only after the dispatcher has polled once.
        GC.KeepAlive(WorkerTelemetry.OutboxPendingMessages);

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                // Only aggregate OUR meters: a listener is process-wide and would
                // otherwise pick up Kestrel/HttpClient instruments and inflate the
                // snapshot with values this endpoint does not own.
                if (AggregatedMeters.Contains(instrument.Meter.Name, StringComparer.Ordinal))
                    listener.EnableMeasurementEvents(instrument);
            },
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            Record(instrument, measurement, tags.ToArray()));
        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            Record(instrument, measurement, tags.ToArray()));

        _listener.Start();
    }

    // ---------------------------------------------------------------
    // Helpers used by the endpoints and the timing middleware.
    // ---------------------------------------------------------------

    public void RequestStarted() => Interlocked.Increment(ref _inFlight);

    public void RequestFinished() => Interlocked.Decrement(ref _inFlight);

    /// <summary>Count one order refusal with its reason.</summary>
    public static void OrderRejected(string reason) =>
        OrdersRejected.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Count one auth refusal with its reason.</summary>
    public static void AuthFailed(string reason) =>
        AuthFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));

    private void Record(Instrument instrument, double value, KeyValuePair<string, object?>[] tags)
    {
        var aggregate = _aggregates.GetOrAdd(
            Describe(instrument.Name, tags),
            _ => new InstrumentAggregate(
                IsHistogram: instrument is Histogram<double>,
                IsGauge: IsGauge(instrument),
                Unit: instrument.Unit ?? string.Empty));

        aggregate.Observe(value);
    }

    /// <summary>
    /// True when the instrument reports a LEVEL (an observable gauge) rather
    /// than a running total. A gauge's snapshot value is the last observed
    /// level; a counter's is the sum of everything recorded.
    /// </summary>
    private static bool IsGauge(Instrument instrument) =>
        instrument is ObservableGauge<long> or ObservableGauge<double>;

    /// <summary>
    /// Build the snapshot key. Tags are sorted so the same logical series always
    /// lands on the same key regardless of tag ordering at the call site.
    /// </summary>
    private static string Describe(string instrumentName, KeyValuePair<string, object?>[] tags)
    {
        if (tags.Length == 0) return instrumentName;

        var ordered = tags
            .OrderBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => $"{t.Key}={t.Value}");

        return $"{instrumentName}{{{string.Join(",", ordered)}}}";
    }

    /// <summary>
    /// Point-in-time view of everything this process has observed. Safe to call
    /// on a hot path: it reads aggregates under their own locks and never blocks
    /// instrument recording.
    /// </summary>
    public ApiMetricsSnapshot Snapshot()
    {
        // Observable instruments only report when asked, so ask now — otherwise
        // the in-flight gauge would be missing from the snapshot entirely.
        _listener.RecordObservableInstruments();

        var counters = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var gauges = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var histograms = new SortedDictionary<string, HistogramSnapshot>(StringComparer.Ordinal);

        // Iterate a copy: a concurrent Add must not mutate the dictionary mid-enumeration.
        foreach (var (key, aggregate) in _aggregates.ToArray())
        {
            var stats = aggregate.Snapshot();
            if (aggregate.IsHistogram)
            {
                histograms[key] = new HistogramSnapshot(stats.Count, stats.Sum, stats.Min, stats.Max);
            }
            else if (aggregate.IsGauge)
            {
                // A gauge is a LEVEL, not a total: the value the observer
                // reported last is the one that matters. Reporting the
                // observation COUNT here (as this class used to) would say
                // "pending = 1" no matter how many rows were actually pending.
                gauges[key] = (long)stats.Last;
            }
            else
            {
                // A counter is a TOTAL: the sum of the recorded values. The
                // count of observations is NOT the same thing — the outbox
                // dispatcher records one observation per batch, carrying the
                // number of messages in it, so the observation count would
                // report "1 message dispatched" for a batch of 20.
                counters[key] = (long)stats.Sum;
            }
        }

        return new ApiMetricsSnapshot(
            Meter: MeterName,
            Version: Version,
            Counters: counters,
            Gauges: gauges,
            Histograms: histograms,
            Meters: AggregatedMeters);
    }

    public void Dispose()
    {
        _listener.Dispose();

        // The gauge itself is not disposable: instruments are owned by their Meter
        // (static here, so it lives for the process). The field is kept because a
        // Meter holds instruments weakly — dropping the reference would let the
        // gauge be collected and silently vanish from the snapshot.
        GC.KeepAlive(_inFlightGauge);
    }

    /// <summary>
    /// Mutable aggregate for one series. Guarded by its own lock rather than
    /// interlocked ops, because sum/min/max must stay mutually consistent — an
    /// interlocked min and max can interleave into a min greater than the max.
    /// </summary>
    private sealed class InstrumentAggregate(bool IsHistogram, bool IsGauge, string Unit)
    {
        private readonly Lock _gate = new();
        private long _count;
        private double _sum;
        private double _min = double.MaxValue;
        private double _max = double.MinValue;
        private double _last;

        public bool IsHistogram { get; } = IsHistogram;

        /// <summary>True for an observable gauge: the series is a level, not a total.</summary>
        public bool IsGauge { get; } = IsGauge;

        public string Unit { get; } = Unit;

        public void Observe(double value)
        {
            lock (_gate)
            {
                _count++;
                _sum += value;
                _last = value;
                if (value < _min) _min = value;
                if (value > _max) _max = value;
            }
        }

        public AggregateSnapshot Snapshot()
        {
            lock (_gate)
            {
                // No observations yet: report zeros rather than the double.MaxValue
                // sentinels, which would be nonsense in a JSON payload.
                if (_count == 0) return new AggregateSnapshot(0, 0, 0, 0, 0);

                return new AggregateSnapshot(_count, _sum, _min, _max, _last);
            }
        }
    }

    /// <summary>Raw aggregate for one series, before it is shaped for the snapshot.</summary>
    private sealed record AggregateSnapshot(long Count, double Sum, double Min, double Max, double Last);
}

/// <summary>Aggregated count/sum/min/max for one series.</summary>
public sealed record HistogramSnapshot(long Count, double Sum, double Min, double Max);

/// <summary>The JSON payload returned by <c>GET /internal/metrics</c>.</summary>
public sealed record ApiMetricsSnapshot(
    string Meter,
    string Version,
    IReadOnlyDictionary<string, long> Counters,
    /// <summary>Current level of every observable gauge (outbox pending/stuck).</summary>
    IReadOnlyDictionary<string, long> Gauges,
    IReadOnlyDictionary<string, HistogramSnapshot> Histograms,
    /// <summary>
    /// Every meter this snapshot aggregates. More than one because the outbox
    /// dispatcher publishes its counters on the worker meter (ADR-019).
    /// </summary>
    IReadOnlyList<string> Meters);