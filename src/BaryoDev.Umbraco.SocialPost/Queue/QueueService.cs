using BaryoDev.Umbraco.SocialPost.Connectors;
using BaryoDev.Umbraco.SocialPost.Credentials;
using BaryoDev.Umbraco.SocialPost.Publishing;
using Umbraco.Cms.Core.Models;

namespace BaryoDev.Umbraco.SocialPost.Queue;

public sealed record EnqueueResult(string Network, string Outcome, string? Reason);

/// <summary>Queues content for networks. Never queues the same content and network twice unless forced.</summary>
public sealed class QueueService(
    QueueRepository repository,
    IEnumerable<ISocialConnector> connectors,
    CredentialStore credentials,
    ContentSnapshotReader reader,
    QueueSignal signal,
    TimeProvider clock)
{
    // networks null means every enabled and configured network.
    // explicitRequest is true for a person asking from the dashboard, which may refresh a pending row.
    public List<EnqueueResult> Enqueue(IContent content, IReadOnlyCollection<string>? networks, bool force, bool explicitRequest)
    {
        var results = new List<EnqueueResult>();
        Snapshot? snap = reader.Read(content);
        if (snap is null)
        {
            return [new EnqueueResult("", "skipped", "The content has no title.")];
        }

        DateTime now = clock.GetUtcNow().UtcDateTime;

        foreach (ISocialConnector c in connectors)
        {
            if (networks is not null && !networks.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!credentials.IsEnabled(c.Name) || !CredentialStore.IsConfigured(c, credentials.Resolve(c)))
            {
                if (networks is not null)
                {
                    results.Add(new EnqueueResult(c.Name, "skipped", "The network is not enabled or not configured."));
                }

                continue;
            }

            results.Add(EnqueueOne(content.Key, c.Name, snap, now, force, explicitRequest));
        }

        signal.Notify();
        return results;
    }

    private EnqueueResult EnqueueOne(Guid key, string network, Snapshot snap, DateTime now, bool force, bool explicitRequest)
    {
        QueueRow? row = repository.Find(key, network);
        if (row is null)
        {
            var created = new QueueRow { ContentKey = key, Network = network, CreatedUtc = now, UpdatedUtc = now, NextAttemptUtc = now };
            Apply(created, snap);
            return repository.TryInsert(created)
                ? new EnqueueResult(network, "queued", null)
                : new EnqueueResult(network, "exists", "Already queued by another request.");
        }

        if (row.Status == QueueStatus.Sending)
        {
            return new EnqueueResult(network, "exists", "A post is being sent right now.");
        }

        var fresh = new QueueRow();
        Apply(fresh, snap);
        if (force)
        {
            return repository.Force(row.Id, now, now, fresh)
                ? new EnqueueResult(network, "requeued", null)
                : new EnqueueResult(network, "exists", "A post is being sent right now.");
        }

        if (row.Status == QueueStatus.Pending && explicitRequest)
        {
            return repository.Reschedule(row.Id, now, now, fresh)
                ? new EnqueueResult(network, "queued", "Updated the pending post.")
                : new EnqueueResult(network, "exists", "The row changed while you were asking. Try again.");
        }

        return row.Status == QueueStatus.Sent
            ? new EnqueueResult(network, "exists", "Already posted. Use force to post again.")
            : new EnqueueResult(network, "exists", $"A row exists with status {row.Status}.");
    }

    public bool Retry(int id)
    {
        if (!repository.Retry(id, clock.GetUtcNow().UtcDateTime))
        {
            return false;
        }

        signal.Notify();
        return true;
    }

    public bool Cancel(int id) =>
        repository.Transition(id, QueueStatus.Pending, QueueStatus.Cancelled, clock.GetUtcNow().UtcDateTime);

    private static void Apply(QueueRow row, Snapshot s)
    {
        row.Title = s.Title;
        row.Url = s.Url;
        row.Summary = s.Summary;
        row.ImageUrl = s.ImageUrl;
        row.Tags = s.Tags;
    }
}
