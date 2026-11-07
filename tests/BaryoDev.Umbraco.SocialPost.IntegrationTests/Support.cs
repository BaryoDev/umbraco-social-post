using System.Net;
using System.Reflection;
using System.Text;
using BaryoDev.Umbraco.SocialPost.Connectors;
using BaryoDev.Umbraco.SocialPost.Credentials;
using BaryoDev.Umbraco.SocialPost.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Migrations;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.Migrations;
using Umbraco.Cms.Infrastructure.Migrations.Upgrade;
using Umbraco.Cms.Infrastructure.Sync;
using Umbraco.Cms.Tests.Integration.Testing;
using Umbraco.Extensions;

namespace BaryoDev.Umbraco.SocialPost.IntegrationTests;

// Answers named members; everything else returns a default.
public class Stub : DispatchProxy
{
    public Dictionary<string, Func<object?[], object?>> On { get; } = new();

    public static T Make<T>(params (string Member, Func<object?[], object?> Answer)[] answers)
        where T : class
    {
        var proxy = Create<T, Stub>();
        foreach (var (member, answer) in answers)
        {
            ((Stub)(object)proxy).On[member] = answer;
        }

        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (On.TryGetValue(targetMethod!.Name, out var answer))
        {
            return answer(args ?? []);
        }

        var type = targetMethod.ReturnType;
        if (type == typeof(string))
        {
            return "/";
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return Array.CreateInstance(type.GetGenericArguments()[0], 0);
        }

        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}

// Delivers cache refreshers on this server straight away, so a publish updates the published cache and navigation.
public sealed class LocalServerMessenger : ServerMessengerBase
{
    public LocalServerMessenger(IJsonSerializer serializer) : base(false, serializer)
    {
    }

    public override void SendMessages()
    {
    }

    public override void Sync()
    {
    }

    protected override void DeliverRemote(ICacheRefresher refresher, MessageType messageType, IEnumerable<object>? ids = null, string? json = null) =>
        throw new NotSupportedException();
}

// The real clock plus an offset the test moves forward.
public sealed class TestClock : TimeProvider
{
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
}

public sealed record Sent(HttpMethod Method, Uri Uri, string Body);

// Answers from a function and records what was sent. Nothing here reaches the network.
public sealed class Recorder(Func<Sent, HttpResponseMessage> initial) : HttpMessageHandler
{
    public Func<Sent, HttpResponseMessage> Answer { get; set; } = initial;

    public List<Sent> Requests { get; } = [];

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var sent = new Sent(request.Method, request.RequestUri!, body);
        lock (Requests)
        {
            Requests.Add(sent);
        }

        return Answer(sent);
    }
}

// A connector a Pro package could add. It posts whatever Behavior says.
public sealed class FakeConnector : ISocialConnector
{
    public string Name => "Fake";
    public int MaxLength => 1000;
    public int Measure(string text) => text.Length;
    public string DefaultTemplate => "{title} {url}";
    public IReadOnlyList<CredentialField> Fields { get; } = [new("Token", "Token", true, true)];

    public List<SocialMessage> Posted { get; } = [];

    public Func<SocialMessage, PostResult> Behavior { get; set; } = _ => new PostResult("fake-1", "https://fake.example/1");

    public Task<PostResult> PostAsync(SocialMessage message, IReadOnlyDictionary<string, string> settings, CancellationToken ct)
    {
        Posted.Add(message);
        return Task.FromResult(Behavior(message));
    }
}

public sealed class FixedAccessor(IBackOfficeSecurity? security) : IBackOfficeSecurityAccessor
{
    public IBackOfficeSecurity BackOfficeSecurity { get; } = security!;
}

public abstract class SocialPostFixture : UmbracoIntegrationTest
{
    protected TestClock Clock { get; private set; } = null!;

    // Every connector's typed HttpClient goes through this, so no test reaches the network.
    protected Recorder Http { get; private set; } = null!;

    protected FakeConnector Fake { get; private set; } = null!;

    protected override void CustomTestSetup(IUmbracoBuilder builder)
    {
        Clock = new TestClock();
        Fake = new FakeConnector();
        Recorder http = Http = new Recorder(sent => throw new InvalidOperationException("No answer set for " + sent.Uri));

        builder.AddNotificationHandler<ContentTreeChangeNotification, ContentTreeChangeDistributedCacheNotificationHandler>();
        builder.Services.AddUnique<IServerMessenger, LocalServerMessenger>();
        new SocialPostComposer().Compose(builder);

        builder.Services.Configure<SocialPostOptions>(o =>
        {
            o.DocumentTypes = ["post"];
            o.BaseDelaySeconds = 60;
        });
        builder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));

        // The tests drain the queue themselves, so the background service must not race them.
        foreach (ServiceDescriptor d in builder.Services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(QueueProcessor)).ToList())
        {
            builder.Services.Remove(d);
        }

        builder.Services.AddHttpClient<LinkedInConnector>().ConfigurePrimaryHttpMessageHandler(() => http);
        builder.Services.AddHttpClient<BlueskyConnector>().ConfigurePrimaryHttpMessageHandler(() => http);
        builder.Services.AddHttpClient<MastodonConnector>().ConfigurePrimaryHttpMessageHandler(() => http);
        builder.Services.AddTransient<ISocialConnector>(_ => Fake);

        // Url building needs a request; the tests only need a stable absolute link.
        builder.Services.Replace(ServiceDescriptor.Singleton(Stub.Make<IPublishedUrlProvider>(("GetUrl", _ => "https://site.test/post"))));
    }

    // Umbraco's test host does not run package migrations, so run the package's own plan.
    [SetUp]
    public async Task CreatePackageTables()
    {
        var upgrader = new Upgrader(new SocialPostMigrationPlan());
        await upgrader.ExecuteAsync(
            GetRequiredService<IMigrationPlanExecutor>(),
            GetRequiredService<ICoreScopeProvider>(),
            GetRequiredService<IKeyValueService>());
    }

    protected IContentService Content => GetRequiredService<IContentService>();

    protected QueueRepository Repository => GetRequiredService<QueueRepository>();

    protected CredentialStore Store => GetRequiredService<CredentialStore>();

    protected IReadOnlyList<ISocialConnector> Connectors => GetRequiredService<IEnumerable<ISocialConnector>>().ToList();

    protected QueueProcessor Processor => ActivatorUtilities.CreateInstance<QueueProcessor>(Services);

    protected void CreateType(string alias)
    {
        var type = new ContentType(GetRequiredService<IShortStringHelper>(), Constants.System.Root)
        {
            Alias = alias,
            Name = alias,
            AllowedAsRoot = true,
        };
        var created = GetRequiredService<IContentTypeService>()
            .CreateAsync(type, Constants.Security.SuperUserKey).GetAwaiter().GetResult();
        Assert.That(created.Success, Is.True, "create type: " + created.Result);
    }

    protected IContent Publish(string name, string alias = "post")
    {
        var item = Content.Create(name, Constants.System.Root, alias);
        Content.Save(item);
        var published = Content.Publish(item, ["*"]);
        Assert.That(published.Success, Is.True, "publish " + name + ": " + published.Result);
        return item;
    }

    protected void Enable(string network, params (string Key, string Value)[] values)
    {
        ISocialConnector connector = Connectors.First(c => c.Name == network);
        Store.Save(connector, true, values.ToDictionary(v => v.Key, v => (string?)v.Value), []);
    }

    protected IUser SuperUser() =>
        GetRequiredService<IUserService>().GetAsync(Constants.Security.SuperUserKey).GetAwaiter().GetResult()!;
}
