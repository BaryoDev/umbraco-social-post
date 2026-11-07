using BaryoDev.Umbraco.SocialPost.Connectors;
using BaryoDev.Umbraco.SocialPost.Credentials;
using BaryoDev.Umbraco.SocialPost.Messages;
using BaryoDev.Umbraco.SocialPost.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Sync;

namespace BaryoDev.Umbraco.SocialPost.Queue;

/// <summary>Drains due rows. Retries with exponential backoff up to MaxAttempts.</summary>
public sealed class QueueProcessor(
    IServiceScopeFactory scopes,
    QueueRepository repository,
    CredentialStore credentials,
    QueueSignal signal,
    IRuntimeState runtime,
    IServerRoleAccessor serverRole,
    IOptions<SocialPostOptions> options,
    TimeProvider clock,
    ILogger<QueueProcessor> logger) : BackgroundService
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromHours(6);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (options.Value.Enabled && runtime.Level == RuntimeLevel.Run
                        && serverRole.CurrentServerRole is ServerRole.Single or ServerRole.SchedulingPublisher)
                    {
                        await RunOnceAsync(stoppingToken);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogError(e, "Social post queue run failed");
                }

                try
                {
                    await signal.WaitAsync(TimeSpan.FromSeconds(Math.Max(5, options.Value.PollSeconds)), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        DateTime now = clock.GetUtcNow().UtcDateTime;

        // A row stuck in "sending" may or may not have gone out. Do not guess, do not repost.
        repository.FailStale(now - StaleAfter, now, "Interrupted while sending. Check the network for the post before retrying.");

        using IServiceScope scope = scopes.CreateScope();
        var connectors = scope.ServiceProvider.GetRequiredService<IEnumerable<ISocialConnector>>()
            .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var contentService = scope.ServiceProvider.GetRequiredService<IContentService>();
        var reader = scope.ServiceProvider.GetRequiredService<ContentSnapshotReader>();

        foreach (QueueRow due in repository.Due(now, options.Value.BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            if (!repository.Transition(due.Id, QueueStatus.Pending, QueueStatus.Sending, now))
            {
                continue;
            }

            // The batch was read earlier. Read the row again so a force or edit since then is what gets sent.
            QueueRow? row = repository.Get(due.Id);
            if (row is null)
            {
                continue;
            }

            await SendAsync(row, connectors.GetValueOrDefault(row.Network), contentService, reader, ct);
        }
    }

    private async Task SendAsync(QueueRow row, ISocialConnector? connector, IContentService contentService, ContentSnapshotReader reader, CancellationToken ct)
    {
        DateTime now = clock.GetUtcNow().UtcDateTime;
        Dictionary<string, string> settings = [];

        try
        {
            if (connector is null)
            {
                throw new SocialPostException($"There is no connector named {row.Network}.", false);
            }

            settings = credentials.Resolve(connector);
            if (!credentials.IsEnabled(connector.Name) || !CredentialStore.IsConfigured(connector, settings))
            {
                throw new SocialPostException("The network is not enabled or not configured.", false);
            }

            // A delayed or retried row may belong to content that was unpublished or deleted since.
            IContent? content = contentService.GetById(row.ContentKey);
            if (content is not { Published: true, Trashed: false })
            {
                row.Status = QueueStatus.Cancelled;
                row.LastError = "The content is no longer published.";
                Finish(row, now);
                return;
            }

            string url = reader.ResolveUrl(row.ContentKey) ?? row.Url;
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new SocialPostException("The content has no published link yet.", true);
            }

            string text = MessageComposer.Compose(
                settings.GetValueOrDefault(CredentialStore.TemplateKey) ?? connector.DefaultTemplate,
                connector, row.Title, url, row.Summary, row.Tags);
            var message = new SocialMessage(text, row.Title, url, row.Summary, row.ImageUrl, $"{row.Id}-{row.Generation}");

            PostResult result = await connector.PostAsync(message, settings, ct);
            row.Status = QueueStatus.Sent;
            row.Attempts++;
            row.SentUtc = now;
            row.RemoteId = Cut(result.RemoteId, 512);
            row.RemoteUrl = Cut(result.RemoteUrl, 1024);
            row.LastError = null;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            bool retryable = e is SocialPostException { Retryable: true } || e is not SocialPostException;
            row.Attempts++;
            row.LastError = ErrorText.Clean(
                e is SocialPostException ? e.Message : "Unexpected error. The details are in the server log.", settings.Values);
            if (e is not SocialPostException)
            {
                logger.LogError(e, "Posting {Title} to {Network} failed", row.Title, row.Network);
            }

            if (!retryable || row.Attempts >= options.Value.MaxAttempts)
            {
                row.Status = QueueStatus.Failed;
            }
            else
            {
                row.Status = QueueStatus.Pending;
                row.NextAttemptUtc = now + Backoff(row.Attempts);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown mid-send. Leave the row as it is; the stale check handles it.
            return;
        }

        Finish(row, now);
    }

    // Applies only if the row is still the same send. A force made meanwhile wins.
    private void Finish(QueueRow row, DateTime now)
    {
        if (!repository.Finish(row, now))
        {
            logger.LogInformation("Row {Id} changed while it was being sent; the result was not written", row.Id);
        }
    }

    internal TimeSpan Backoff(int attempts)
    {
        double seconds = Math.Max(1, options.Value.BaseDelaySeconds) * Math.Pow(2, Math.Max(0, attempts - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
    }

    private static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
