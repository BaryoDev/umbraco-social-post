using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using BaryoDev.Umbraco.SocialPost.Connectors;
using BaryoDev.Umbraco.SocialPost.Messages;
using BaryoDev.Umbraco.SocialPost.Net;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.SocialPost.Tests;

public class NetworkTextTests
{
    private static ImageFetcher NoImages() => new(new HttpClient(new StubHandler(_ => throw new InvalidOperationException("no request expected"))), Options.Create(new SocialPostOptions()));

    private static BlueskyConnector Bluesky(StubHandler handler) => new(new HttpClient(handler), NoImages());

    private static MastodonConnector Mastodon() => new(new HttpClient(), NoImages());

    private static int CodePointsWithLinksAt23(string text)
    {
        int total = 0;
        int last = 0;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"https?://\S+"))
        {
            total += text[last..m.Index].EnumerateRunes().Count() + 23;
            last = m.Index + m.Length;
        }

        return total + text[last..].EnumerateRunes().Count();
    }

    [Fact]
    public async Task Bluesky_link_facet_offsets_count_utf8_bytes()
    {
        var handler = new StubHandler(sent => sent.Uri.AbsolutePath switch
        {
            "/xrpc/com.atproto.server.createSession" => StubHandler.Json("""{"did":"did:plc:fake","accessJwt":"fake-jwt"}"""),
            "/xrpc/com.atproto.repo.createRecord" => StubHandler.Json("""{"uri":"at://did:plc:fake/app.bsky.feed.post/3kfake"}"""),
            _ => throw new InvalidOperationException(sent.Uri.ToString()),
        });
        const string url = "https://baryo.dev/x";
        string text = "\U0001F98A new post " + url;
        var settings = new Dictionary<string, string> { ["Handle"] = "me.bsky.social", ["AppPassword"] = "fake-app-password" };

        PostResult result = await Bluesky(handler).PostAsync(new SocialMessage(text, "new post", url, null, null, "1-0"), settings, TestContext.Current.CancellationToken);

        JsonNode record = JsonNode.Parse(handler.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("createRecord")).Body)!["record"]!;
        JsonNode index = record["facets"]![0]!["index"]!;
        // The fox is 4 bytes, so the link starts at byte 14. 12 would be the UTF-16 index, 11 the code point index.
        Assert.Equal(14, (int)index["byteStart"]!);
        Assert.Equal(14 + Encoding.UTF8.GetByteCount(url), (int)index["byteEnd"]!);
        Assert.Equal(url, (string)record["facets"]![0]!["features"]![0]!["uri"]!);
        Assert.Equal("https://bsky.app/profile/did:plc:fake/post/3kfake", result.RemoteUrl);
    }

    [Fact]
    public void Mastodon_text_is_trimmed_to_500_code_points_with_urls_at_23()
    {
        string url = "https://baryo.dev/" + new string('p', 150);
        string text = MessageComposer.Compose(Mastodon().DefaultTemplate, Mastodon(), "Title", url, new string('a', 2000), "one,two");

        Assert.Contains(url, text);
        Assert.Equal(500, CodePointsWithLinksAt23(text));
        // The 150 character link was counted as 23, so the raw text is longer than 500.
        Assert.True(text.Length > 500);
    }

    [Fact]
    public void Mastodon_counts_code_points_not_characters_or_graphemes()
    {
        // "e" plus a combining accent is two code points, one grapheme, two UTF-16 chars.
        string accented = string.Concat(Enumerable.Repeat("e\u0301", 600));
        string text = MessageComposer.Compose("{title}\n\n{summary}", Mastodon(), "T", "https://baryo.dev/x", accented, null);

        Assert.True(CodePointsWithLinksAt23(text) <= 500, "code points: " + CodePointsWithLinksAt23(text));
        Assert.Equal(500, Mastodon().Measure(new string('a', 500)));
        Assert.Equal(1, new StringInfo("e\u0301").LengthInTextElements);
        Assert.Equal(2, Mastodon().Measure("e\u0301"));
    }

    [Fact]
    public void Bluesky_text_is_trimmed_to_300_graphemes()
    {
        BlueskyConnector connector = Bluesky(new StubHandler(_ => throw new InvalidOperationException()));
        const string url = "https://baryo.dev/x";
        // Each family emoji is one grapheme made of 8 UTF-16 chars.
        string title = string.Concat(Enumerable.Repeat("\U0001F468\u200D\U0001F469\u200D\U0001F467", 400));

        string text = MessageComposer.Compose(connector.DefaultTemplate, connector, title, url, null, null);

        Assert.Equal(300, new StringInfo(text).LengthInTextElements);
        Assert.EndsWith(url, text);
        Assert.Contains("\u2026", text);
    }

    [Fact]
    public async Task LinkedIn_posts_as_member_only()
    {
        var handler = new StubHandler(_ =>
        {
            var response = StubHandler.Json("", System.Net.HttpStatusCode.Created);
            response.Headers.Add("x-restli-id", "urn:li:share:fake1");
            return response;
        });
        var connector = new LinkedInConnector(new HttpClient(handler));
        var message = new SocialMessage("hello", "Title", "https://baryo.dev/x", "Summary", null, "1-0");

        PostResult ok = await connector.PostAsync(message, new Dictionary<string, string> { ["AuthorUrn"] = "urn:li:person:fake1", ["AccessToken"] = "fake-token" }, TestContext.Current.CancellationToken);

        Assert.Equal("urn:li:share:fake1", ok.RemoteId);
        JsonNode body = JsonNode.Parse(handler.Requests.Single().Body)!;
        Assert.StartsWith("urn:li:person:", (string)body["author"]!);

        // An organization URN is refused before anything is sent.
        handler.Requests.Clear();
        var refused = await Assert.ThrowsAsync<SocialPostException>(() => connector.PostAsync(
            message, new Dictionary<string, string> { ["AuthorUrn"] = "urn:li:organization:42", ["AccessToken"] = "fake-token" }, TestContext.Current.CancellationToken));
        Assert.False(refused.Retryable);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Only_the_free_networks_are_connectors()
    {
        string[] names = typeof(ISocialConnector).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ISocialConnector).IsAssignableFrom(t))
            .Select(t => t.Name).Order().ToArray();
        Assert.Equal(new[] { "BlueskyConnector", "LinkedInConnector", "MastodonConnector" }, names);
    }

    [Theory]
    [InlineData("10.0.0.1", false)]
    [InlineData("10.255.255.255", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("2002::1", false)]
    [InlineData("2002:c000:204::1", false)]
    [InlineData("2001::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Private_addresses_are_not_public(string address, bool expected) =>
        Assert.Equal(expected, ImageFetcher.IsPublic(System.Net.IPAddress.Parse(address)));
}
