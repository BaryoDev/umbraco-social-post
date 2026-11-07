using BaryoDev.Umbraco.SocialPost.Publishing;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;

namespace BaryoDev.Umbraco.SocialPost.IntegrationTests;

[TestFixture]
public class SnapshotUrlTests
{
    // The queue worker and scheduled publishing have no web request, so no Umbraco context exists
    // unless the reader asks for one. The real provider throws without it.
    [Test]
    public void The_link_is_resolved_inside_an_ensured_Umbraco_context()
    {
        bool inContext = false;
        var factory = Stub.Make<IUmbracoContextFactory>(("EnsureUmbracoContext", _ =>
        {
            inContext = true;
            return new UmbracoContextReference(null!, false, null!);
        }));
        var urls = Stub.Make<IPublishedUrlProvider>(("GetUrl", _ =>
            inContext ? "https://site.test/post" : throw new InvalidOperationException("Wasn't able to get an UmbracoContext")));
        var reader = new ContentSnapshotReader(
            Options.Create(new SocialPostOptions()), urls, factory, Stub.Make<IMediaService>(), Stub.Make<ILanguageService>());

        Assert.That(reader.ResolveUrl(Guid.NewGuid()), Is.EqualTo("https://site.test/post"));
    }
}
