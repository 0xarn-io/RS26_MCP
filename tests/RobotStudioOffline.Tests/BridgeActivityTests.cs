using System.Text.Json;
using RobotStudioOffline.Core;

namespace RobotStudioOffline.Tests;

internal static partial class Program
{
    private static void AddBridgeActivityTests(List<(string Name, Action Run)> tests)
    {
        tests.Add(("bridge activity records pending work and preserves the returned value", () => ActivityPendingAsync().GetAwaiter().GetResult()));
        tests.Add(("bridge activity rethrows failures without retaining private payloads", () => ActivityPrivacyAsync().GetAwaiter().GetResult()));
        tests.Add(("bridge activity distinguishes cancellation without swallowing it", () => ActivityCancellationAsync().GetAwaiter().GetResult()));
        tests.Add(("bridge activity bounds history and returns detached snapshots", () => ActivityBoundedAsync().GetAwaiter().GetResult()));
        tests.Add(("clearing bridge history preserves in-flight work and lifetime counters", () => ActivityClearAsync().GetAwaiter().GetResult()));
        tests.Add(("bridge activity prioritizes pending rows while retaining an absolute capacity", () => ActivityEvictionAsync().GetAwaiter().GetResult()));
        tests.Add(("bridge activity counters remain consistent under concurrent updates", () => ActivityConcurrentAsync().GetAwaiter().GetResult()));
        tests.Add(("bridge activity rejects an invalid history capacity", () =>
        {
            Throws<ArgumentOutOfRangeException>(() => new BridgeActivity(0));
            Throws<ArgumentOutOfRangeException>(() => new BridgeActivity(-1));
        }));
    }

    private static async Task ActivityPendingAsync()
    {
        var activity = new BridgeActivity();
        var source = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracked = activity.TrackAsync("station", () => source.Task);
        var pending = activity.Snapshot();
        Equal(1L, pending.Total);
        Equal(1L, pending.Pending);
        Equal(BridgeActivityState.Pending, pending.Entries.Single().State);
        True(pending.Entries[0].EndUtc is null && pending.Entries[0].ElapsedMs is null);
        var result = new object();
        source.SetResult(result);
        True(ReferenceEquals(result, await tracked));
        var finished = activity.Snapshot();
        Equal(1L, finished.Completed);
        Equal(0L, finished.Pending);
        Equal(BridgeActivityState.Completed, finished.Entries.Single().State);
        True(finished.Entries[0].EndUtc is not null && finished.Entries[0].ElapsedMs is >= 0);
        True(finished.Entries[0].ErrorCode is null);
    }

    private static async Task ActivityPrivacyAsync()
    {
        var activity = new BridgeActivity();
        const string secret = "PRIVATE_TOKEN_AND_STATION_PAYLOAD";
        var expected = new BridgeException("invalid_request", secret);
        try
        {
            await activity.TrackAsync("io_write", () => Task.FromException<object>(expected));
            throw new Exception("The original failure was swallowed.");
        }
        catch (Exception observed) { True(ReferenceEquals(expected, observed)); }
        var snapshot = activity.Snapshot();
        Equal(1L, snapshot.Failed);
        Equal("invalid_request", snapshot.Entries.Single().ErrorCode!);
        True(!JsonSerializer.Serialize(snapshot).Contains(secret));

        var malformedCode = new BridgeException("error message\n" + secret, secret);
        try
        {
            await activity.TrackAsync("endpoint/" + secret, () => throw malformedCode);
            throw new Exception("The synchronous failure was swallowed.");
        }
        catch (Exception observed) { True(ReferenceEquals(malformedCode, observed)); }
        snapshot = activity.Snapshot();
        Equal("unknown", snapshot.Entries[0].Operation);
        Equal("sdk_error", snapshot.Entries[0].ErrorCode!);
        True(!JsonSerializer.Serialize(snapshot).Contains(secret));

        try { await activity.TrackAsync("reach", () => Task.FromException<object>(new InvalidOperationException(secret))); }
        catch (InvalidOperationException) { }
        Equal("sdk_error", activity.Snapshot().Entries[0].ErrorCode!);
    }

    private static async Task ActivityCancellationAsync()
    {
        var activity = new BridgeActivity();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var expected = new OperationCanceledException("Do not store this cancellation message.", cancellation.Token);
        try
        {
            await activity.TrackAsync("reach", () => Task.FromException<object>(expected));
            throw new Exception("Cancellation was swallowed.");
        }
        catch (OperationCanceledException observed) { True(ReferenceEquals(expected, observed)); }
        var snapshot = activity.Snapshot();
        Equal(0L, snapshot.Pending);
        Equal(1L, snapshot.Failed);
        Equal("canceled", snapshot.Entries.Single().ErrorCode!);
    }

    private static async Task ActivityBoundedAsync()
    {
        var activity = new BridgeActivity(3);
        for (var index = 0; index < 7; index++)
            await activity.TrackAsync("station", () => Task.FromResult<object>(new { privateResponse = "not retained" }));
        var snapshot = activity.Snapshot();
        Equal(7L, snapshot.Total);
        Equal(7L, snapshot.Completed);
        True(snapshot.Entries.Select(entry => entry.Id).SequenceEqual(new long[] { 7, 6, 5 }));
        snapshot.Entries[0] = snapshot.Entries[0] with { Operation = "mutated by caller" };
        Equal("station", activity.Snapshot().Entries[0].Operation);
        True(!JsonSerializer.Serialize(activity.Snapshot()).Contains("privateResponse"));
    }

    private static async Task ActivityClearAsync()
    {
        var activity = new BridgeActivity();
        await activity.TrackAsync("station", () => Task.FromResult<object>(new object()));
        var source = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracked = activity.TrackAsync("reach", () => source.Task);
        activity.ClearHistory();
        var cleared = activity.Snapshot();
        Equal(2L, cleared.Total);
        Equal(1L, cleared.Completed);
        Equal(1L, cleared.Pending);
        Equal(2L, cleared.Entries.Single().Id);
        source.SetResult(new object());
        await tracked;
        Equal(BridgeActivityState.Completed, activity.Snapshot().Entries.Single().State);
        activity.ClearHistory();
        Equal(0, activity.Snapshot().Entries.Length);
        Equal(2L, activity.Snapshot().Completed);
    }

    private static async Task ActivityEvictionAsync()
    {
        var activity = new BridgeActivity(2);
        var firstSource = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = activity.TrackAsync("reach", () => firstSource.Task);
        await activity.TrackAsync("station", () => Task.FromResult<object>(new object()));
        var thirdSource = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = activity.TrackAsync("reach", () => thirdSource.Task);
        True(activity.Snapshot().Entries.Select(entry => entry.Id).SequenceEqual(new long[] { 3, 1 }));
        var fourthSource = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourth = activity.TrackAsync("reach", () => fourthSource.Task);
        var full = activity.Snapshot();
        Equal(3L, full.Pending);
        True(full.Entries.Select(entry => entry.Id).SequenceEqual(new long[] { 4, 3 }));
        firstSource.SetResult(new object());
        await first;
        Equal(2, activity.Snapshot().Entries.Length);
        thirdSource.SetResult(new object());
        fourthSource.SetResult(new object());
        await Task.WhenAll(third, fourth);
        Equal(4L, activity.Snapshot().Completed);
        Equal(0L, activity.Snapshot().Pending);
    }

    private static async Task ActivityConcurrentAsync()
    {
        var activity = new BridgeActivity(25);
        var work = Task.WhenAll(Enumerable.Range(0, 250).Select(_ => Task.Run(() =>
            activity.TrackAsync("io_read", async () => { await Task.Yield(); return new object(); }))));
        while (!work.IsCompleted)
        {
            var snapshot = activity.Snapshot();
            True(snapshot.Entries.Length <= 25);
            Equal(snapshot.Total, snapshot.Completed + snapshot.Failed + snapshot.Pending);
            True(snapshot.Pending >= 0);
            await Task.Yield();
        }
        await work;
        var finished = activity.Snapshot();
        Equal(250L, finished.Total);
        Equal(250L, finished.Completed);
        Equal(0L, finished.Pending);
        Equal(25, finished.Entries.Length);
        Equal(25, finished.Entries.Select(entry => entry.Id).Distinct().Count());
    }
}
