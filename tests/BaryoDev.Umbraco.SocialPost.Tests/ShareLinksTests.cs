using BaryoDev.Umbraco.SocialPost.Sharing;

namespace BaryoDev.Umbraco.SocialPost.Tests;

public class ShareLinksTests
{
    private const string Text = "Tom & Jerry #1 100% 50/50";
    private const string Url = "https://baryo.dev/a b?x=1&y=2#top";

    [Fact]
    public void Share_links_are_encoded_per_network()
    {
        string x = ShareLinks.For("x", Text, Url);
        Assert.Equal(
            "https://x.com/intent/tweet?text=Tom%20%26%20Jerry%20%231%20100%25%2050%2F50&url=https%3A%2F%2Fbaryo.dev%2Fa%20b%3Fx%3D1%26y%3D2%23top",
            x);

        // The text survives as one query value: the & and # inside it are escaped.
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(x).Query);
        Assert.Equal(Text, query["text"]);
        Assert.Equal(Url, query["url"]);

        Assert.Equal(
            "https://www.facebook.com/sharer/sharer.php?u=https%3A%2F%2Fbaryo.dev%2Fa%20b%3Fx%3D1%26y%3D2%23top",
            ShareLinks.For("facebook", Text, Url));
        Assert.Equal(
            "https://www.linkedin.com/sharing/share-offsite/?url=https%3A%2F%2Fbaryo.dev%2Fa%20b%3Fx%3D1%26y%3D2%23top",
            ShareLinks.For("linkedin", Text, Url));
    }

    [Fact]
    public void Facebook_and_LinkedIn_links_carry_the_link_only()
    {
        Assert.DoesNotContain("Jerry", ShareLinks.For("facebook", Text, Url));
        Assert.DoesNotContain("Jerry", ShareLinks.For("linkedin", Text, Url));
    }

    [Fact]
    public void Network_names_are_case_insensitive_and_unknown_names_give_nothing()
    {
        Assert.Equal(ShareLinks.For("x", Text, Url), ShareLinks.For("X", Text, Url));
        Assert.Equal("", ShareLinks.For("myspace", Text, Url));
        Assert.Equal(new[] { "x", "facebook", "linkedin" }, ShareLinks.Networks);
    }
}
