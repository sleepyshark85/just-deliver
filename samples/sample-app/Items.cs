using System.Net;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Azure.Cosmos;

namespace SampleApp;

public sealed record Item(string Id, string Text);

/// <summary>The only thing the app needs from Cosmos DB; the seam the tests fake.</summary>
public interface IItemStore
{
    /// <summary>Throws unless the app can reach its container with its own identity.</summary>
    Task CheckAccessAsync(CancellationToken ct);

    Task SaveAsync(Item item, CancellationToken ct);

    Task<Item?> GetAsync(string id, CancellationToken ct);
}

/// <summary>
/// Cosmos DB through <see cref="DefaultAzureCredential"/> only: no key, no connection string.
/// Nothing is contacted until the first call, and a failure is never remembered: the client is dropped
/// so the next request starts clean (ADR 0012: the grant arrives after the process starts).
/// </summary>
public sealed class CosmosItemStore(string? endpoint, string? database, string? container) : IItemStore, IDisposable
{
    // The container's partition key is /id (catalog/mappings/cosmos-sql); the probe reads a document that never exists.
    private const string ProbeId = "health-probe";

    private CosmosClient? _client;
    private Container? _container;

    public async Task CheckAccessAsync(CancellationToken ct) => await GetAsync(ProbeId, ct);

    public Task SaveAsync(Item item, CancellationToken ct) =>
        Run(c => c.UpsertItemAsync(item, new PartitionKey(item.Id), cancellationToken: ct));

    public async Task<Item?> GetAsync(string id, CancellationToken ct)
    {
        try
        {
            return await Run(async c => (await c.ReadItemAsync<Item>(id, new PartitionKey(id), cancellationToken: ct)).Resource);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public void Dispose() => _client?.Dispose();

    private async Task<T> Run<T>(Func<Container, Task<T>> action)
    {
        try
        {
            return await action(_container ??= Connect());
        }
        catch (Exception e) when (e is not CosmosException { StatusCode: HttpStatusCode.NotFound })
        {
            _container = null;
            _client?.Dispose();
            _client = null;
            throw;
        }
    }

    private Container Connect()
    {
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(database) || string.IsNullOrEmpty(container))
        {
            throw new InvalidOperationException("Cosmos DB is not configured: set COSMOS_ENDPOINT, COSMOS_DATABASE and COSMOS_CONTAINER.");
        }

        _client = new CosmosClient(endpoint, new DefaultAzureCredential(), new CosmosClientOptions
        {
            UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web),
        });
        return _client.GetContainer(database, container);
    }
}
