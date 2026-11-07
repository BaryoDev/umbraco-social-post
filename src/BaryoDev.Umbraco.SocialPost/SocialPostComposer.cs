using System.Net;
using System.Net.Http.Headers;
using BaryoDev.Umbraco.SocialPost.Connectors;
using BaryoDev.Umbraco.SocialPost.Credentials;
using BaryoDev.Umbraco.SocialPost.Net;
using BaryoDev.Umbraco.SocialPost.Publishing;
using BaryoDev.Umbraco.SocialPost.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Notifications;

namespace BaryoDev.Umbraco.SocialPost;

public sealed class SocialPostComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        IServiceCollection services = builder.Services;
        services.AddOptions<SocialPostOptions>().BindConfiguration(SocialPostOptions.Section);

        services.AddHttpClient<ImageFetcher>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectCallback = ImageFetcher.ConnectPinned })
            .ConfigureHttpClient((sp, client) =>
            {
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, sp.GetRequiredService<IOptions<SocialPostOptions>>().Value.HttpTimeoutSeconds));
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(ImageFetcher.UserAgent, null));
            });
        AddConnector<LinkedInConnector>(services);
        // Another package adds a network by registering a typed HttpClient for its connector and the connector as an ISocialConnector.
        // The server address is configurable for these two, so they connect only to public addresses.
        AddConnector<BlueskyConnector>(services, pinned: true);
        AddConnector<MastodonConnector>(services, pinned: true);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<QueueRepository>();
        services.AddSingleton<QueueSignal>();
        services.AddSingleton<CredentialStore>();
        services.AddTransient<ContentSnapshotReader>();
        services.AddTransient<QueueService>();
        services.AddHostedService<QueueProcessor>();
        builder.AddNotificationHandler<ContentPublishedNotification, PublishedHandler>();
    }

    private static void AddConnector<T>(IServiceCollection services, bool pinned = false) where T : class, ISocialConnector
    {
        AddClient<T>(services, pinned);
        services.AddTransient<ISocialConnector>(sp => sp.GetRequiredService<T>());
    }

    // No redirects, so an Authorization header is never forwarded to another host.
    private static void AddClient<T>(IServiceCollection services, bool pinned = false) where T : class =>
        services.AddHttpClient<T>()
            .ConfigurePrimaryHttpMessageHandler(() => pinned
                ? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectCallback = ImageFetcher.ConnectPinned }
                : new HttpClientHandler { AllowAutoRedirect = false })
            .ConfigureHttpClient((sp, client) =>
            {
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, sp.GetRequiredService<IOptions<SocialPostOptions>>().Value.HttpTimeoutSeconds));
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(ImageFetcher.UserAgent, null));
            });
}
