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
/// Holds one shared, lazily created resource. Concurrent callers get the same instance, and a failed
/// instance is disposed once, by whoever still finds it in place.
/// </summary>
public sealed class SharedConnection<T>(Func<T> create) : IDisposable where T : class, IDisposable
{
    private readonly object _gate = new();
    private T? _current;

    public T Get()
    {
        lock (_gate)
        {
            return _current ??= create();
        }
    }

    /// <summary>Forgets <paramref name="used"/> and disposes it, unless another caller already replaced it.</summary>
    public void Drop(T used)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, used))
            {
                return;
            }

            _current = null;
        }

        used.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _current?.Dispose();
            _current = null;
        }
    }
}

/// <summary>
/// Cosmos DB through <see cref="DefaultAzureCredential"/> only: no key, no connection string.
/// Nothing is contacted until the first call. A Cosmos answer (403 before the grant lands, 404 for a missing
/// container) is per request and the same client sees the grant once it propagates; any other failure (credential
/// unavailable, timeout, not configured) is never remembered: the client is dropped so the next request starts
/// clean (ADR 0012). Calls time out after 5 s.
/// </summary>
public sealed class CosmosItemStore : IItemStore, IDisposable
{
    // The container's partition key is /id (catalog/mappings/cosmos-sql); the probe reads a document that never exists.
    private const string ProbeId = "health-probe";

    // The SDK can retry an unreachable endpoint for far longer than a probe may wait, so every call is bounded here.
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private readonly SharedConnection<Handle> _connection;

    public CosmosItemStore(string? endpoint, string? database, string? container) =>
        _connection = new SharedConnection<Handle>(() => Connect(endpoint, database, container));

    /// <summary>Only a plain 404 means the document is absent; 404 with a substatus (e.g. 1003) means the container or database is.</summary>
    public static bool IsMissingDocument(CosmosException e) => e is { StatusCode: HttpStatusCode.NotFound, SubStatusCode: 0 };

    public async Task CheckAccessAsync(CancellationToken ct) => await GetAsync(ProbeId, ct);

    public Task SaveAsync(Item item, CancellationToken ct) =>
        Run(c => c.UpsertItemAsync(item, new PartitionKey(item.Id), cancellationToken: ct));

    public async Task<Item?> GetAsync(string id, CancellationToken ct)
    {
        try
        {
            return await Run(async c => (await c.ReadItemAsync<Item>(id, new PartitionKey(id), cancellationToken: ct)).Resource);
        }
        catch (CosmosException e) when (IsMissingDocument(e))
        {
            return null;
        }
    }

    public void Dispose() => _connection.Dispose();

    private async Task<T> Run<T>(Func<Container, Task<T>> action)
    {
        var handle = _connection.Get();
        try
        {
            return await action(handle.Container).WaitAsync(CallTimeout);
        }
        catch (Exception e) when (e is not CosmosException)
        {
            _connection.Drop(handle);
            throw;
        }
    }

    private static Handle Connect(string? endpoint, string? database, string? container)
    {
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(database) || string.IsNullOrEmpty(container))
        {
            throw new InvalidOperationException("Cosmos DB is not configured: set COSMOS_ENDPOINT, COSMOS_DATABASE and COSMOS_CONTAINER.");
        }

        var client = new CosmosClient(endpoint, new DefaultAzureCredential(), new CosmosClientOptions
        {
            UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web),
        });
        return new Handle(client, client.GetContainer(database, container));
    }

    private sealed record Handle(CosmosClient Client, Container Container) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }
}
