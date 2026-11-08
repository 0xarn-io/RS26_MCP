using System.Diagnostics;
using RobotStudioOffline.Core;
using Events = ABB.Robotics.Controllers.EventLogDomain;

namespace RobotStudioOffline.HeadlessRecorder;

// Copy native event handles during the callback. Only the sampling thread mutates the run.
internal sealed class NativeEventBuffer
{
    private sealed record Observed(double TimeMs, DateTimeOffset Utc, DateTimeOffset SourceUtc, int Number, int Sequence, string Text);
    private readonly object gate = new();
    private readonly Queue<Observed> pending = new();
    private readonly Events.EventLog log;
    private readonly Stopwatch clock;
    private bool stopped;
    private int lost;
    private int? previousSequence;

    public NativeEventBuffer(Events.EventLog log, Stopwatch clock)
    {
        this.log = log;
        this.clock = clock;
        log.MessageWrittenNoCache += Receive;
    }
    private void Receive(object? sender, Events.MessageWrittenEventArgs args)
    {
        lock (gate)
        {
            if (stopped) return;
            if (pending.Count >= 256) { lost++; return; }
            try
            {
                var message = args.Message;
                var body = message.Title + " " + message.Body;
                pending.Enqueue(new(clock.Elapsed.TotalMilliseconds, DateTimeOffset.UtcNow, new DateTimeOffset(message.Timestamp).ToUniversalTime(),
                    message.Number, args.SequenceNumber, body[..Math.Min(body.Length, 1024)]));
            }
            catch (Exception) { lost++; }
        }
    }
    public void Drain(RunCaptureStore store, string runId, string? phase, RunPointer? pp, RunPointer? mp)
    {
        Observed[] batch;
        int skipped;
        lock (gate)
        {
            if (pending.Count == 0 && lost == 0) return;
            batch = pending.ToArray(); pending.Clear();
            skipped = lost; lost = 0;
        }
        if (skipped > 0)
        {
            store.NoteDroppedEvents(runId, skipped);
            store.AddEvent(runId, new(clock.Elapsed.TotalMilliseconds, DateTimeOffset.UtcNow, "vc_event_loss",
                $"{skipped} events exceeded the callback buffer or became unreadable.", phase));
        }
        foreach (var entry in batch)
        {
            var lag = Math.Max(0, clock.Elapsed.TotalMilliseconds - entry.TimeMs);
            if (previousSequence is int previous && entry.Sequence > (long)previous + 1)
                store.AddEvent(runId, new(entry.TimeMs, entry.Utc, "vc_sequence_gap",
                    $"Observed controller sequence {previous} followed by {entry.Sequence}.", phase));
            previousSequence = entry.Sequence;
            store.AddEvent(runId, new(entry.TimeMs, entry.Utc, "vc_event",
                $"{entry.Text} [sample delivery lag {lag:F1} ms; pointers sampled at delivery]", phase,
                ProgramPointer: pp, MotionPointer: mp, EventNumber: entry.Number, SequenceNumber: entry.Sequence, SourceUtc: entry.SourceUtc));
        }
    }
    public void Stop()
    {
        lock (gate) stopped = true;
        log.MessageWrittenNoCache -= Receive;
    }
}
