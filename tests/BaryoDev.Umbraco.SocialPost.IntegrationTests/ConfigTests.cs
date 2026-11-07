using BaryoDev.Umbraco.SocialPost.Connectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Umbraco.Cms.Tests.Common.Testing;

namespace BaryoDev.Umbraco.SocialPost.IntegrationTests;

[TestFixture]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerTest)]
public class ConfigTests : SocialPostFixture
{
    [TestCase(0)]
    [TestCase(-5)]
    public void A_timeout_of_zero_or_less_does_not_break_the_connector_clients(int seconds)
    {
        GetRequiredService<IOptions<SocialPostOptions>>().Value.HttpTimeoutSeconds = seconds;

        Assert.DoesNotThrow(() => GetRequiredService<BlueskyConnector>());
        Assert.DoesNotThrow(() => GetRequiredService<MastodonConnector>());
        Assert.DoesNotThrow(() => GetRequiredService<LinkedInConnector>());
    }
}
