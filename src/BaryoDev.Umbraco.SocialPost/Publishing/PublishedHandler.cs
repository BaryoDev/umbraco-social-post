using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BaryoDev.Umbraco.SocialPost.Queue;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;

namespace BaryoDev.Umbraco.SocialPost.Publishing;

/// <summary>Queues content the first time it is published. A later publish finds the queue row and does nothing.</summary>
public sealed class PublishedHandler(
    IOptions<SocialPostOptions> options,
    QueueService queue,
    ILogger<PublishedHandler> logger) : INotificationHandler<ContentPublishedNotification>
{
    public void Handle(ContentPublishedNotification notification)
    {
        SocialPostOptions o = options.Value;
        if (!o.Enabled || !o.PostOnFirstPublish || o.DocumentTypes.Count == 0)
        {
            return;
        }

        foreach (IContent content in notification.PublishedEntities)
        {
            if (!o.DocumentTypes.Contains(content.ContentType.Alias, StringComparer.OrdinalIgnoreCase)
                || !content.WasPropertyDirty(nameof(IContent.Published)))
            {
                continue;
            }

            try
            {
                var snap = queue.Enqueue(content, null, force: false, explicitRequest: false);
                logger.LogInformation("Queued {Name} for social posting: {Results}", content.Name, string.Join(", ", snap.Select(r => $"{r.Network} {r.Outcome}")));
            }
            catch (Exception e)
            {
                // Publishing must not fail because a social post could not be queued.
                logger.LogError(e, "Could not queue {Name} for social posting", content.Name);
            }
        }
    }
}
