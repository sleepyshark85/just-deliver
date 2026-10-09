using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Azure.Cosmos;
using SampleApp;
using Xunit;

namespace sample_app.tests;

// The app runs for real on a loopback port; only the Cosmos access is faked.
public sealed class SampleAppTests
{
    private sealed class FakeStore : IItemStore
    {
        public Exception? Failure { get; set; }

        public Dictionary<string, Item> Items { get; } = [];

        public Task CheckAccessAsync(CancellationToken ct) => Failure is null ? Task.CompletedTask : Task.FromException(Failure);

        public Task SaveAsync(Item item, CancellationToken ct)
        {
            if (Failure is not null)
            {
                return Task.FromException(Failure);
            }

            Items[item.Id] = item;
            return Task.CompletedTask;
        }

        public Task<Item?> GetAsync(string id, CancellationToken ct) =>
            Failure is null ? Task.FromResult(Items.GetValueOrDefault(id)) : Task.FromException<Item?>(Failure);
    }

    private static async Task<(WebApplication App, HttpClient Client)> Start(IItemStore? store)
    {
        var app = SampleAppFactory.Build(["--urls", "http://127.0.0.1:0"], store);
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static CosmosException Cosmos(HttpStatusCode status) => new("failed", status, 0, "activity", 0);

    [Fact]
    public async Task Health_is_200_when_the_store_works()
    {
        var (app, client) = await Start(new FakeStore());
        await using var _ = app;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "denied access")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "returned 503")]
    public async Task Health_is_503_with_a_reason_when_cosmos_denies_or_fails(HttpStatusCode cosmos, string reason)
    {
        var (app, client) = await Start(new FakeStore { Failure = Cosmos(cosmos) });
        await using var _ = app;

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(reason, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Health_recovers_once_access_is_granted()
    {
        var store = new FakeStore { Failure = Cosmos(HttpStatusCode.Forbidden) };
        var (app, client) = await Start(store);
        await using var _ = app;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health")).StatusCode);

        store.Failure = null;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task App_starts_and_reports_unhealthy_without_any_cosmos_configuration()
    {
        var (app, client) = await Start(store: null);
        await using var _ = app;

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("not configured", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Items_round_trip()
    {
        var (app, client) = await Start(new FakeStore());
        await using var _ = app;

        var created = await client.PostAsJsonAsync("/items", new { text = "hello" });
        var item = await created.Content.ReadFromJsonAsync<JsonElement>();
        var fetched = await client.GetAsync(created.Headers.Location);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var got = await fetched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(item.GetProperty("id").GetString(), got.GetProperty("id").GetString());
        Assert.Equal("hello", got.GetProperty("text").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/items/missing")).StatusCode);
    }

    [Fact]
    public async Task Items_return_503_instead_of_crashing_when_the_store_fails()
    {
        var (app, client) = await Start(new FakeStore { Failure = Cosmos(HttpStatusCode.Forbidden) });
        await using var _ = app;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/items", new { text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/items/any")).StatusCode);
    }
}
