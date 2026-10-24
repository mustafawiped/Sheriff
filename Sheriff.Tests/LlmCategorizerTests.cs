using System.Text.Json;
using Sheriff.Core;
using Sheriff.Core.Categorization;
using Xunit;
using System.Net;
using System.Text;

namespace Sheriff.Tests;

public sealed class LlmCategoryResponseTests
{
    private static string Response(string category) => JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = JsonSerializer.Serialize(new { category }) } } } });
    [Fact]
    public void ValidCategoryReturnsConfiguredCanonicalSpelling() => Assert.Equal(Categories.Software, LlmResponseParser.ParseCategory(Response("yazılım"), Categories.Defaults));
    [Theory]
    [InlineData("Gereksiz")]
    [InlineData("Made-up category")]
    public void HiddenAndUnconfiguredCategoriesAreRejected(string category) => Assert.Null(LlmResponseParser.ParseCategory(Response(category), Categories.Defaults));
    [Fact]
    public void CustomConfiguredCategoryIsAccepted() => Assert.Equal("İş", LlmResponseParser.ParseCategory(Response("İş"), ["İş", Categories.Other]));
    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[null]}")]
    [InlineData("{\"choices\":[{\"message\":null}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":null}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"not-json\"}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"{\\\"category\\\":4}\"}}]}")]
    public void InvalidResponsesReturnNullWithoutThrowing(string response) => Assert.Null(LlmResponseParser.ParseCategory(response, Categories.Defaults));
}

public sealed class LlmResultCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Sheriff-llm-cache-tests-" + Guid.NewGuid().ToString("N"));
    private string CachePath => Path.Combine(_root, "llm-cache.json");
    [Fact]
    public async Task CachedResultsSurviveReopenWithoutPlaintextMetadata()
    {
        var key = LlmResultCache.CreateKey("endpoint", "model", Categories.Defaults, "Private Application Name", "Private Publisher");
        await new LlmResultCache(CachePath).SetAsync(key, Categories.Software);
        Assert.Equal(Categories.Software, await new LlmResultCache(CachePath).TryGetAsync(key));
        var contents = File.ReadAllText(CachePath);
        Assert.DoesNotContain("Private Application Name", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("Private Publisher", contents, StringComparison.Ordinal);
    }
    [Fact]
    public void KeyIncludesAllClassificationInputs()
    {
        var key = LlmResultCache.CreateKey("endpoint", "model", Categories.Defaults, "Name", "Publisher");
        Assert.NotEqual(key, LlmResultCache.CreateKey("changed", "model", Categories.Defaults, "Name", "Publisher"));
        Assert.NotEqual(key, LlmResultCache.CreateKey("endpoint", "changed", Categories.Defaults, "Name", "Publisher"));
        Assert.NotEqual(key, LlmResultCache.CreateKey("endpoint", "model", ["Other"], "Name", "Publisher"));
        Assert.NotEqual(key, LlmResultCache.CreateKey("endpoint", "model", Categories.Defaults, "Changed", "Publisher"));
        Assert.NotEqual(key, LlmResultCache.CreateKey("endpoint", "model", Categories.Defaults, "Name", "Changed"));
        Assert.Equal(key, LlmResultCache.CreateKey("endpoint", "model", Categories.Defaults.Reverse(), "Name", "Publisher"));
    }
    [Fact]
    public async Task ConcurrentWritesPreserveEveryResult()
    {
        var cache = new LlmResultCache(CachePath);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => cache.SetAsync("key-" + i, Categories.Software)));
        var reopened = new LlmResultCache(CachePath);
        foreach (var i in Enumerable.Range(0, 12)) Assert.Equal(Categories.Software, await reopened.TryGetAsync("key-" + i));
    }
    [Fact]
    public async Task MalformedCacheFallsBackToEmpty()
    {
        Directory.CreateDirectory(_root); File.WriteAllText(CachePath, "unfinished{");
        var cache = new LlmResultCache(CachePath);
        Assert.Null(await cache.TryGetAsync("key"));
        await cache.SetAsync("key", Categories.Other);
        Assert.Equal(Categories.Other, await new LlmResultCache(CachePath).TryGetAsync("key"));
    }
    [Fact]
    public async Task CancelledCacheOperationDoesNotCreateAFile()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cache = new LlmResultCache(CachePath);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SetAsync("key", Categories.Other, cancelled.Token));
        Assert.False(File.Exists(CachePath));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

public sealed class LlmTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Sheriff-llm-fake-tests-" + Guid.NewGuid().ToString("N"));
    private string CachePath => Path.Combine(_root, "llm-cache.json");
    private static AppEntry Unknown() => new()
    {
        Id = "private-id-4791", DisplayName = "Quasar Unfamiliar", Publisher = "Example Publisher",
        TargetPathOrAumid = @"C:\private-location-4791\mystery.exe", Description = "private-description-4791",
        Arguments = "--private-argument-4791", IconCachePath = @"C:\private-icon-4791.png"
    };
    private static SheriffConfig Enabled() => new() { LlmEnabled = true, LlmApiKey = "fake-unit-test-key", LlmEndpoint = LlmCategorizer.ApprovedEndpoint, LlmModel = "test-model" };
    private LlmCategorizer Service(HttpClient client, AppCategorizer? rules = null) => new(CachePath, client, rules ?? new AppCategorizer([]));

    [Fact]
    public async Task DisabledByDefaultMakesNoRequest()
    {
        using var handler = new FakeHandler(); using var client = new HttpClient(handler); using var service = Service(client);
        var config = Enabled(); config.LlmEnabled = false;
        Assert.Null(await service.ClassifyAsync(Unknown(), config));
        Assert.Equal(0, handler.Requests);
        Assert.False(File.Exists(CachePath));
    }
    [Theory]
    [InlineData("https://elsewhere.test/v1/chat/completions")]
    [InlineData("http://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/other")]
    [InlineData("https://api.openai.com/v1/chat/completions?forward=yes")]
    [InlineData("https://api.openai.com:444/v1/chat/completions")]
    [InlineData("https://user:password@api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/chat/completions#fragment")]
    public async Task UnapprovedDestinationsNeverReceiveRequests(string endpoint)
    {
        using var handler = new FakeHandler(); using var client = new HttpClient(handler); using var service = Service(client);
        var config = Enabled(); config.LlmEndpoint = endpoint;
        Assert.Null(await service.ClassifyAsync(Unknown(), config));
        Assert.Equal(0, handler.Requests);
    }
    [Theory]
    [InlineData("known")]
    [InlineData("override")]
    [InlineData("junk")]
    [InlineData("missing-key")]
    public async Task OnlyUnclassifiedAppsWithExplicitKeyReachTransport(string scenario)
    {
        using var handler = new FakeHandler(); using var client = new HttpClient(handler);
        var classifier = new AppCategorizer([new() { Name = "Known", Category = Categories.Software, ExeNames = ["known.exe"] }]);
        using var service = Service(client, classifier);
        var config = Enabled(); var app = Unknown();
        if (scenario == "known") app = app with { TargetPathOrAumid = @"C:\Apps\known.exe" };
        if (scenario == "override") config.CategoryOverrides[app.Id] = Categories.Other;
        if (scenario == "junk") app = app with { DisplayName = "Uninstall Quasar" };
        if (scenario == "missing-key") config.LlmApiKey = "";
        Assert.Null(await service.ClassifyAsync(app, config));
        Assert.Equal(0, handler.Requests);
    }
    [Fact]
    public async Task FakeRequestUsesFixedUrlAndOnlyNamePublisherMetadata()
    {
        using var handler = new FakeHandler(); using var client = new HttpClient(handler); using var service = Service(client);
        var config = Enabled(); config.Categories.Add("private-custom-taxonomy-4791");
        Assert.Equal(Categories.Software, await service.ClassifyAsync(Unknown(), config));
        Assert.Equal(LlmCategorizer.ApprovedEndpoint, handler.Destination);
        Assert.Equal(HttpMethod.Post, handler.Method);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal(128, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Contains("JSON", messages[0].GetProperty("content").GetString()!, StringComparison.Ordinal);
        using var metadata = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal(new[] { "name", "publisher" }, metadata.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Unknown().DisplayName, metadata.RootElement.GetProperty("name").GetString());
        Assert.Equal(Unknown().Publisher, metadata.RootElement.GetProperty("publisher").GetString());
        Assert.DoesNotContain("private-", handler.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain(Enabled().LlmApiKey, handler.Body!, StringComparison.Ordinal);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal(Enabled().LlmApiKey, handler.AuthorizationParameter);
    }
    [Fact]
    public async Task CustomCategoriesStayLocalAndCannotBeChosenByModel()
    {
        const string custom = "Private Custom Category";
        using var handler = new FakeHandler { ResponseCategory = custom }; using var client = new HttpClient(handler); using var service = Service(client);
        var config = Enabled(); config.Categories.Add(custom);
        Assert.Null(await service.ClassifyAsync(Unknown(), config));
        Assert.DoesNotContain(custom, handler.Body!, StringComparison.Ordinal);
        Assert.False(File.Exists(CachePath));
    }
    [Fact]
    public async Task CachedResultIsReusedAfterServiceReopen()
    {
        using var firstHandler = new FakeHandler(); using var firstClient = new HttpClient(firstHandler);
        using (var first = Service(firstClient)) Assert.Equal(Categories.Software, await first.ClassifyAsync(Unknown(), Enabled()));
        using var nextHandler = new FakeHandler(); using var nextClient = new HttpClient(nextHandler); using var reopened = Service(nextClient);
        Assert.Equal(Categories.Software, await reopened.ClassifyAsync(Unknown(), Enabled()));
        Assert.Equal(0, nextHandler.Requests);
        Assert.DoesNotContain(Enabled().LlmApiKey, File.ReadAllText(CachePath), StringComparison.Ordinal);
    }
    [Fact]
    public async Task ConcurrentIdenticalRequestsUseOneFakeResponse()
    {
        using var handler = new FakeHandler(); using var client = new HttpClient(handler); using var service = Service(client);
        var result = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.ClassifyAsync(Unknown(), Enabled())));
        Assert.All(result, value => Assert.Equal(Categories.Software, value));
        Assert.Equal(1, handler.Requests);
    }
    [Theory]
    [InlineData(302)]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task FailedEndpointHasCooldownAcrossApps(int status)
    {
        using var handler = new FakeHandler { StatusCode = (HttpStatusCode)status }; using var client = new HttpClient(handler); using var service = Service(client);
        Assert.Null(await service.ClassifyAsync(Unknown(), Enabled()));
        Assert.Null(await service.ClassifyAsync(Unknown() with { DisplayName = "Another Unfamiliar" }, Enabled()));
        Assert.Equal(1, handler.Requests);
    }
    [Theory]
    [InlineData("Gereksiz")]
    [InlineData("Unconfigured category")]
    public async Task InvalidModelCategoriesAreNotCached(string category)
    {
        using var handler = new FakeHandler { ResponseCategory = category }; using var client = new HttpClient(handler); using var service = Service(client);
        Assert.Null(await service.ClassifyAsync(Unknown(), Enabled()));
        Assert.False(File.Exists(CachePath));
    }
    [Fact]
    public async Task CancellationReturnsWithoutApplyingFailureCooldown()
    {
        using var handler = new FakeHandler { WaitForCancellation = true }; using var client = new HttpClient(handler); using var service = Service(client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Assert.Null(await service.ClassifyAsync(Unknown(), Enabled(), cancellation.Token));
        handler.WaitForCancellation = false;
        Assert.Equal(Categories.Software, await service.ClassifyAsync(Unknown(), Enabled()));
        Assert.Equal(2, handler.Requests);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? Body { get; private set; }
        public string? Destination { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string ResponseCategory { get; set; } = Categories.Software;
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public bool WaitForCancellation { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++; Destination = request.RequestUri!.AbsoluteUri; Method = request.Method;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var content = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = JsonSerializer.Serialize(new { category = ResponseCategory }) } } } });
            var response = new HttpResponseMessage(StatusCode) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
            if ((int)StatusCode is >= 300 and < 400) response.Headers.Location = new Uri("https://unapproved.test/redirect");
            return response;
        }
    }
}
