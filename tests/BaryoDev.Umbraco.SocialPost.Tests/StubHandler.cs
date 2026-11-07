using System.Net;
using System.Text;

namespace BaryoDev.Umbraco.SocialPost.Tests;

public sealed record Sent(HttpMethod Method, Uri Uri, string Body);

// Answers from a function and records what was sent. Nothing here reaches the network.
public sealed class StubHandler(Func<Sent, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<Sent> Requests { get; } = [];

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var sent = new Sent(request.Method, request.RequestUri!, body);
        Requests.Add(sent);
        return answer(sent);
    }
}
