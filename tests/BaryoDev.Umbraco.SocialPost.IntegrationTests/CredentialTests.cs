using BaryoDev.Umbraco.SocialPost.Connectors;
using BaryoDev.Umbraco.SocialPost.Credentials;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Cms.Tests.Common.Testing;

namespace BaryoDev.Umbraco.SocialPost.IntegrationTests;

[TestFixture]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerTest)]
public class CredentialTests : SocialPostFixture
{
    private CredentialStore StoreWith(params (string Key, string Value)[] config) => new(
        new ConfigurationBuilder().AddInMemoryCollection(config.ToDictionary(c => $"BaryoDev:SocialPost:Networks:{c.Key}", c => (string?)c.Value)).Build(),
        GetRequiredService<IScopeProvider>(),
        GetRequiredService<IDataProtectionProvider>(),
        GetRequiredService<ILogger<CredentialStore>>());

    [Test]
    public void Config_secret_locks_the_endpoint()
    {
        ISocialConnector bluesky = Connectors.Single(c => c.Name == "Bluesky");
        Store.Save(bluesky, true, new Dictionary<string, string?> { ["Handle"] = "me.bsky.social", ["ServiceUrl"] = "https://stored.example" }, []);

        // Without a configured secret the stored address is used.
        Assert.That(Store.Resolve(bluesky)["ServiceUrl"], Is.EqualTo("https://stored.example"));

        // With the app password in configuration, the stored address is ignored and the default is used.
        CredentialStore locked = StoreWith(("Bluesky:AppPassword", "fake-app-password"));
        Dictionary<string, string> resolved = locked.Resolve(bluesky);
        Assert.That(resolved["ServiceUrl"], Is.EqualTo("https://bsky.social"));
        Assert.That(resolved["AppPassword"], Is.EqualTo("fake-app-password"));
        Assert.That(locked.Describe(bluesky).Fields.Single(f => f.Key == "ServiceUrl").Source, Is.EqualTo("locked"));

        // The dashboard cannot point a configured secret at a new address.
        Assert.Throws<ArgumentException>(() => locked.Save(
            bluesky, null, new Dictionary<string, string?> { ["ServiceUrl"] = "https://other.example" }, []));
        Assert.That(Store.Resolve(bluesky)["ServiceUrl"], Is.EqualTo("https://stored.example"));
    }

    [Test]
    public void A_new_endpoint_clears_the_secrets_stored_for_the_old_one()
    {
        ISocialConnector mastodon = Connectors.Single(c => c.Name == "Mastodon");
        Store.Save(mastodon, true, new Dictionary<string, string?> { ["InstanceUrl"] = "https://one.example", ["AccessToken"] = "fake-token" }, []);
        Assert.That(Store.Resolve(mastodon)["AccessToken"], Is.EqualTo("fake-token"));

        Store.Save(mastodon, null, new Dictionary<string, string?> { ["InstanceUrl"] = "https://two.example" }, []);

        Dictionary<string, string> resolved = Store.Resolve(mastodon);
        Assert.That(resolved["InstanceUrl"], Is.EqualTo("https://two.example"));
        Assert.That(resolved.ContainsKey("AccessToken"), Is.False);
    }

    [Test]
    public void The_dashboard_state_never_carries_a_stored_secret()
    {
        ISocialConnector mastodon = Connectors.Single(c => c.Name == "Mastodon");
        Store.Save(mastodon, true, new Dictionary<string, string?> { ["InstanceUrl"] = "https://one.example", ["AccessToken"] = "fake-token" }, []);

        NetworkState state = Store.Describe(mastodon);

        FieldState token = state.Fields.Single(f => f.Key == "AccessToken");
        Assert.That(token.IsSet, Is.True);
        Assert.That(token.Value, Is.Null);
    }

    [Test]
    public void Everything_the_package_injects_is_registered()
    {
        // Each Umbraco type the package asks for, as the concrete registered interface.
        Type[] umbraco =
        [
            typeof(IContentService), typeof(IContentPermissionService), typeof(IMediaService), typeof(ILanguageService),
            typeof(global::Umbraco.Cms.Core.Routing.IPublishedUrlProvider), typeof(global::Umbraco.Cms.Core.Security.IBackOfficeSecurityAccessor),
            typeof(IRuntimeState), typeof(IServerRoleAccessor), typeof(IScopeProvider),
        ];
        foreach (Type type in umbraco)
        {
            Assert.That(Services.GetService(type), Is.Not.Null, type.Name);
        }

        // And the package's own types build from the container, so no constructor argument is missing.
        Assert.That(ActivatorUtilities.CreateInstance<Api.SocialPostController>(Services), Is.Not.Null);
        Assert.That(ActivatorUtilities.CreateInstance<Queue.QueueProcessor>(Services), Is.Not.Null);
        Assert.That(GetRequiredService<Queue.QueueService>(), Is.Not.Null);
        Assert.That(GetRequiredService<Publishing.ContentSnapshotReader>(), Is.Not.Null);
        Assert.That(GetRequiredService<Net.ImageFetcher>(), Is.Not.Null);
    }
}
