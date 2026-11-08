using System.Diagnostics;

namespace RobotStudioOffline.Core;

public enum BridgeActivityState
{
    /// <summary>The request is queued for the dispatcher or is executing.</summary>
    Pending,
    Completed,
    Failed
}

public sealed record BridgeActivityEntry(long Id, string Operation, DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc, double? ElapsedMs, BridgeActivityState State, string? ErrorCode);

public sealed record BridgeActivitySnapshot(BridgeActivityEntry[] Entries, long Total,
    long Completed, long Failed, long Pending);

/// <summary>
/// Thread-safe, bounded request metadata. Arguments, credentials, responses and exception messages
/// are never retained. Counters cover all requests, including entries evicted from history.
/// </summary>
public sealed class BridgeActivity
{
    private static readonly HashSet<string> KnownOperations = new(
        BridgeOperations.All, StringComparer.Ordinal);

    private readonly object gate = new();
    private readonly Dictionary<long, BridgeActivityEntry> entries = new();
    private readonly int capacity;
    private long total;
    private long completed;
    private long failed;
    private long pending;

    public BridgeActivity(int capacity = 100)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public async Task<object> TrackAsync(string operation, Func<Task<object>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var started = DateTimeOffset.UtcNow;
        var timestamp = Stopwatch.GetTimestamp();
        long id;
        lock (gate)
        {
            id = ++total;
            pending++;
            if (entries.Count >= capacity)
            {
                // Preserve in-flight entries where possible. If every row is in flight,
                // evict the oldest row while continuing to count its eventual outcome.
                var oldest = entries.Values.Where(entry => entry.State != BridgeActivityState.Pending).MinBy(entry => entry.Id)
                    ?? entries.Values.MinBy(entry => entry.Id)!;
                entries.Remove(oldest.Id);
            }
            entries.Add(id, new(id, KnownOperations.Contains(operation) ? operation : "unknown", started,
                null, null, BridgeActivityState.Pending, null));
        }

        try
        {
            var result = await action().ConfigureAwait(false);
            Finish(id, timestamp, null);
            return result;
        }
        catch (Exception exception)
        {
            var code = exception switch
            {
                OperationCanceledException => "canceled",
                BridgeException bridge => SafeErrorCode(bridge.Code),
                _ => "sdk_error"
            };
            Finish(id, timestamp, code);
            throw;
        }
    }

    /// <summary>Returns detached immutable entries, ordered by newest request first.</summary>
    public BridgeActivitySnapshot Snapshot()
    {
        lock (gate)
            return new(entries.Values.OrderByDescending(entry => entry.Id).ToArray(), total, completed, failed, pending);
    }

    /// <summary>Clears finished rows only. In-flight rows and lifetime counters are preserved.</summary>
    public void ClearHistory()
    {
        lock (gate)
        {
            foreach (var id in entries.Values.Where(entry => entry.State != BridgeActivityState.Pending).Select(entry => entry.Id).ToArray())
                entries.Remove(id);
        }
    }

    private void Finish(long id, long timestamp, string? errorCode)
    {
        var ended = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
        lock (gate)
        {
            pending--;
            if (errorCode is null) completed++;
            else failed++;
            if (entries.TryGetValue(id, out var entry))
                entries[id] = entry with
                {
                    EndUtc = ended,
                    ElapsedMs = elapsed,
                    State = errorCode is null ? BridgeActivityState.Completed : BridgeActivityState.Failed,
                    ErrorCode = errorCode
                };
        }
    }

    private static string SafeErrorCode(string? code) =>
        code is { Length: > 0 and <= 64 } && code.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            ? code : "sdk_error";
}
