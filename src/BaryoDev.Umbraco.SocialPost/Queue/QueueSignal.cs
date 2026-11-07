namespace BaryoDev.Umbraco.SocialPost.Queue;

/// <summary>Wakes the background service when something is queued, so it does not wait out the poll interval.</summary>
public sealed class QueueSignal
{
    private readonly SemaphoreSlim _gate = new(0, 1);

    public void Notify()
    {
        try
        {
            _gate.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _gate.WaitAsync(timeout, ct);
}
