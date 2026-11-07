using System.Net;
using System.Text.Json.Nodes;
using BaryoDev.Umbraco.SocialPost.Connectors;
using BaryoDev.Umbraco.SocialPost.Queue;
using NUnit.Framework;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Tests.Common.Testing;

namespace BaryoDev.Umbraco.SocialPost.IntegrationTests;

[TestFixture]
[UmbracoTest(Database = UmbracoTestOptions.Database.NewSchemaPerTest)]
public class QueueTests : SocialPostFixture
{
    private QueueRow NewRow(Guid key, string network) => new()
    {
        ContentKey = key,
        Network = network,
        Title = "Title",
        Url = "https://site.test/post",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
        NextAttemptUtc = DateTime.UtcNow,
    };

    private List<QueueRow> Rows() => Repository.List(null, 500);

    [Test]
    public void Queue_row_is_unique_per_content_and_network()
    {
        Guid key = Guid.NewGuid();

        Assert.That(Repository.TryInsert(NewRow(key, "LinkedIn")), Is.True);
        Assert.That(Repository.TryInsert(NewRow(key, "LinkedIn")), Is.False, "same content and network");
        Assert.That(Repository.TryInsert(NewRow(key, "Bluesky")), Is.True, "same content, other network");
        Assert.That(Repository.TryInsert(NewRow(Guid.NewGuid(), "LinkedIn")), Is.True, "other content, same network");
        Assert.That(Rows(), Has.Count.EqualTo(3));

        // Several requests at once for the same pair: exactly one wins.
        Guid contested = Guid.NewGuid();
        const int callers = 6;
        using var barrier = new Barrier(callers);
        var tasks = Enumerable.Range(0, callers).Select(_ => Task.Factory.StartNew(
            () =>
            {
                barrier.SignalAndWait();
                return Repository.TryInsert(NewRow(contested, "Mastodon"));
            },
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        bool[] results = Task.WhenAll(tasks).GetAwaiter().GetResult();

        Assert.That(results.Count(r => r), Is.EqualTo(1));
        Assert.That(Rows().Count(r => r.ContentKey == contested), Is.EqualTo(1));
    }

    [Test]
    public void First_publish_enqueues_once_and_republish_does_not()
    {
        CreateType("post");
        Enable("Fake", ("Token", "fake-token"));

        IContent item = Publish("Hello");
        QueueRow row = Rows().Single();
        Assert.That(row.ContentKey, Is.EqualTo(item.Key));
        Assert.That(row.Network, Is.EqualTo("Fake"));
        Assert.That(row.Status, Is.EqualTo(QueueStatus.Pending));
        Assert.That(row.Title, Is.EqualTo("Hello"));

        // Later, with the clock moved on, a second publish of the same item writes nothing.
        Clock.Offset = TimeSpan.FromHours(1);
        item.Name = "Hello again";
        Content.Save(item);
        Assert.That(Content.Publish(item, ["*"]).Success, Is.True);
        Assert.That(Rows().Single().UpdatedUtc, Is.EqualTo(row.UpdatedUtc));
        Assert.That(Rows().Single().Title, Is.EqualTo("Hello"));

        // Unpublish and publish again counts as a first publish to the handler, but the queue row stops it.
        Assert.That(Content.Unpublish(item).Success, Is.True);
        Assert.That(Content.Publish(item, ["*"]).Success, Is.True);
        QueueRow after = Rows().Single();
        Assert.That(after.Id, Is.EqualTo(row.Id));
        Assert.That(after.UpdatedUtc, Is.EqualTo(row.UpdatedUtc));
        Assert.That(after.Attempts, Is.EqualTo(0));
    }

    [Test]
    public void Finish_is_ignored_when_generation_changed()
    {
        Guid key = Guid.NewGuid();
        QueueRow row = NewRow(key, "LinkedIn");
        Assert.That(Repository.TryInsert(row), Is.True);
        DateTime now = DateTime.UtcNow;

        Assert.That(Repository.Transition(row.Id, QueueStatus.Pending, QueueStatus.Sending, now), Is.True);
        QueueRow oldSend = Repository.Get(row.Id)!;
        Assert.That(oldSend.Generation, Is.EqualTo(0));

        // The first send goes stale, and a person forces a repost while it is still running.
        Repository.FailStale(now.AddHours(1), now, "stale");
        Assert.That(Repository.Force(row.Id, now, now, NewRow(key, "LinkedIn")), Is.True);
        Assert.That(Repository.Transition(row.Id, QueueStatus.Pending, QueueStatus.Sending, now), Is.True);

        oldSend.Status = QueueStatus.Sent;
        oldSend.Attempts = 1;
        oldSend.RemoteId = "old-send";
        Assert.That(Repository.Finish(oldSend, now), Is.False, "the old send must not write");

        QueueRow current = Repository.Get(row.Id)!;
        Assert.That(current.Generation, Is.EqualTo(1));
        Assert.That(current.Status, Is.EqualTo(QueueStatus.Sending));
        Assert.That(current.RemoteId, Is.Null);
        Assert.That(current.Attempts, Is.EqualTo(0));

        current.Status = QueueStatus.Sent;
        current.RemoteId = "new-send";
        Assert.That(Repository.Finish(current, now), Is.True);
        Assert.That(Repository.Get(row.Id)!.RemoteId, Is.EqualTo("new-send"));
    }

    [Test]
    public async Task A_temporary_error_waits_the_backoff_and_the_next_run_posts()
    {
        CreateType("post");
        Enable("Fake", ("Token", "fake-token"));
        Fake.Behavior = _ => throw new SocialPostException("Fake answered HTTP 503.", true);
        Publish("Hello");

        await Processor.RunOnceAsync(CancellationToken.None);

        QueueRow row = Rows().Single();
        Assert.That(Fake.Posted, Has.Count.EqualTo(1));
        Assert.That(row.Status, Is.EqualTo(QueueStatus.Pending));
        Assert.That(row.Attempts, Is.EqualTo(1));
        Assert.That(row.LastError, Does.Contain("503"));
        double wait = (row.NextAttemptUtc - Clock.GetUtcNow().UtcDateTime).TotalSeconds;
        Assert.That(wait, Is.InRange(50, 61), "BaseDelaySeconds is 60");

        // Not due yet: nothing is sent.
        await Processor.RunOnceAsync(CancellationToken.None);
        Assert.That(Fake.Posted, Has.Count.EqualTo(1));

        Clock.Offset = TimeSpan.FromMinutes(2);
        Fake.Behavior = _ => new PostResult("fake-2", "https://fake.example/2");
        await Processor.RunOnceAsync(CancellationToken.None);

        row = Rows().Single();
        Assert.That(Fake.Posted, Has.Count.EqualTo(2));
        Assert.That(row.Status, Is.EqualTo(QueueStatus.Sent));
        Assert.That(row.Attempts, Is.EqualTo(2));
        Assert.That(row.RemoteId, Is.EqualTo("fake-2"));
        Assert.That(Fake.Posted[1].IdempotencyKey, Is.EqualTo($"{row.Id}-0"));
    }

    [Test]
    public async Task A_permanent_error_fails_the_row_at_once()
    {
        CreateType("post");
        Enable("Fake", ("Token", "fake-token"));
        Fake.Behavior = _ => throw new SocialPostException("Fake answered HTTP 401.", false);
        Publish("Hello");

        await Processor.RunOnceAsync(CancellationToken.None);

        QueueRow row = Rows().Single();
        Assert.That(row.Status, Is.EqualTo(QueueStatus.Failed));
        Assert.That(row.Attempts, Is.EqualTo(1));
    }

    [Test]
    public async Task A_queued_post_reaches_LinkedIn_as_a_member_through_the_stub()
    {
        CreateType("post");
        Http.Requests.Clear();
        Enable("LinkedIn", ("AuthorUrn", "urn:li:person:fake1"), ("AccessToken", "fake-token"));
        Http.Answer = _ =>
        {
            var response = Recorder.Json("", HttpStatusCode.Created);
            response.Headers.Add("x-restli-id", "urn:li:share:fake1");
            return response;
        };
        Publish("Hello");

        await Processor.RunOnceAsync(CancellationToken.None);

        Sent request = Http.Requests.Single();
        Assert.That(request.Uri.AbsolutePath, Is.EqualTo("/rest/posts"));
        JsonNode body = JsonNode.Parse(request.Body)!;
        Assert.That((string)body["author"]!, Is.EqualTo("urn:li:person:fake1"));
        Assert.That((string)body["content"]!["article"]!["source"]!, Is.EqualTo("https://site.test/post"));
        QueueRow row = Rows().Single();
        Assert.That(row.Status, Is.EqualTo(QueueStatus.Sent));
        Assert.That(row.RemoteId, Is.EqualTo("urn:li:share:fake1"));
    }
}
