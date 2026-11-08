using System.Text.Json;

namespace RobotStudioOffline.Core;

/// <summary>Serializes complete SDK operations, including their asynchronous continuations.</summary>
public sealed class MainThreadDispatcher(Action<Action> post, Func<string, JsonElement, Task<object>> execute)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<object> ExecuteAsync(string operation, JsonElement arguments, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            // 0 queued, 1 executing, 2 canceled before execution. Once started, wait for completion:
            // returning a timeout while a queued write later executes would be misleading.
            var state = 0;
            using var registration = cancellationToken.Register(() =>
            {
                if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                    completion.TrySetCanceled(cancellationToken);
            });
            post(async () =>
            {
                if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
                try { completion.TrySetResult(await execute(operation, arguments)); }
                catch (OperationCanceledException)
                {
                    completion.TrySetException(new BridgeException("sdk_canceled",
                        "The SDK canceled an operation after execution began. Inspect station state before retrying.", 409));
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            return await completion.Task.ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
