using System.Net;
using System.Net.Sockets;
using BaryoDev.Umbraco.SocialPost.Connectors;
using BaryoDev.Umbraco.SocialPost.Net;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.SocialPost.Tests;

public class ConnectorOutcomeTests
{
    private static ImageFetcher NoImages() => new(new HttpClient(new StubHandler(_ => throw new InvalidOperationException("no request expected"))), Options.Create(new SocialPostOptions()));

    private static readonly SocialMessage Message = new("text https://baryo.dev/x", "title", "https://baryo.dev/x", null, null, "1-0");

    [Fact]
    public async Task Bluesky_2xx_without_a_post_uri_is_an_unknown_outcome_that_is_not_retryable()
    {
        var handler = new StubHandler(sent => sent.Uri.AbsolutePath switch
        {
            "/xrpc/com.atproto.server.createSession" => StubHandler.Json("""{"did":"did:plc:fake","accessJwt":"fake-jwt"}"""),
            _ => StubHandler.Json("{}"),
        });
        var connector = new BlueskyConnector(new HttpClient(handler), NoImages());
        var settings = new Dictionary<string, string> { ["Handle"] = "me.bsky.social", ["AppPassword"] = "fake-app-password" };

        var e = await Assert.ThrowsAsync<SocialPostException>(() => connector.PostAsync(Message, settings, TestContext.Current.CancellationToken));

        Assert.True(e.OutcomeUnknown);
        Assert.False(e.Retryable);
        Assert.Contains(SocialPostException.UnknownOutcome, e.Message);
    }

    [Fact]
    public async Task LinkedIn_2xx_without_a_post_id_is_an_unknown_outcome_that_is_not_retryable()
    {
        var connector = new LinkedInConnector(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created))));
        var settings = new Dictionary<string, string> { ["AuthorUrn"] = "urn:li:person:fake1", ["AccessToken"] = "fake-token" };

        var e = await Assert.ThrowsAsync<SocialPostException>(() => connector.PostAsync(Message, settings, TestContext.Current.CancellationToken));

        Assert.True(e.OutcomeUnknown);
        Assert.False(e.Retryable);
        Assert.Contains(SocialPostException.UnknownOutcome, e.Message);
    }

    private static readonly IPAddress A = IPAddress.Parse("203.0.113.1");
    private static readonly IPAddress B = IPAddress.Parse("203.0.113.2");

    [Fact]
    public async Task Pinned_connect_falls_back_to_the_next_public_address()
    {
        var tried = new List<IPAddress>();
        using var stream = new MemoryStream();

        Stream result = await ImageFetcher.ConnectPinned(
            "host.test", 443,
            (_, _) => Task.FromResult(new[] { A, B }),
            (endpoint, _) =>
            {
                tried.Add(endpoint.Address);
                return endpoint.Address.Equals(A) ? throw new SocketException() : ValueTask.FromResult<Stream>(stream);
            },
            TestContext.Current.CancellationToken);

        Assert.Same(stream, result);
        Assert.Equal([A, B], tried);
    }

    [Fact]
    public async Task Pinned_connect_fails_when_every_address_fails()
    {
        await Assert.ThrowsAsync<HttpRequestException>(async () => await ImageFetcher.ConnectPinned(
            "host.test", 443,
            (_, _) => Task.FromResult(new[] { A, B }),
            (_, _) => throw new SocketException(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pinned_connect_refuses_when_any_address_is_not_public_and_never_connects()
    {
        bool connected = false;

        await Assert.ThrowsAsync<HttpRequestException>(async () => await ImageFetcher.ConnectPinned(
            "host.test", 443,
            (_, _) => Task.FromResult(new[] { A, IPAddress.Parse("10.0.0.1") }),
            (_, _) =>
            {
                connected = true;
                return ValueTask.FromResult<Stream>(new MemoryStream());
            },
            TestContext.Current.CancellationToken));

        Assert.False(connected);
    }
}
