using System.Threading.Channels;

namespace FocusGarden.Core;

/// <summary>One background owner for synchronous database operations. Accepted work is never abandoned on shutdown.</summary>
public sealed class SerialWorkQueue : IAsyncDisposable
{
    private readonly Channel<Action> work = Channel.CreateBounded<Action>(new BoundedChannelOptions(256)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    private readonly Task runner;
    private int closed;

    public SerialWorkQueue()
    {
        runner = Task.Run(async () =>
        {
            await foreach (var action in work.Reader.ReadAllAsync().ConfigureAwait(false)) action();
        });
    }

    public Task<T> RunAsync<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Volatile.Read(ref closed) != 0)
            return Task.FromException<T>(new ObjectDisposedException(nameof(SerialWorkQueue)));
        if (!work.Writer.TryWrite(() =>
        {
            try { result.TrySetResult(operation()); }
            catch (Exception error) { result.TrySetException(error); }
        })) result.TrySetException(new InvalidOperationException("后台保存队列繁忙或正在关闭，请稍后重试。"));
        return result.Task;
    }

    public Task RunAsync(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunAsync(() => { operation(); return true; });
    }

    public Task DrainAsync() => Volatile.Read(ref closed) != 0 ? runner : RunAsync(() => { });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) == 0) work.Writer.TryComplete();
        await runner.ConfigureAwait(false);
    }
}
