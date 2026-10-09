using System.Net;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Azure.Cosmos;

namespace SampleApp;

public sealed record NewItem(string Text);

public static class SampleAppFactory
{
    // Settings come from environment variables, named in the workload's container.variables.
    public static WebApplication Build(string[] args, IItemStore? store = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        var config = builder.Configuration;

        // Telemetry only when the platform gave the runtime a connection string (the exporter reads it itself).
        if (!string.IsNullOrEmpty(config["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            builder.Services.AddOpenTelemetry().UseAzureMonitor();
        }

        builder.Services.AddSingleton<IItemStore>(store ?? new CosmosItemStore(config["COSMOS_ENDPOINT"], config["COSMOS_DATABASE"], config["COSMOS_CONTAINER"]));
        return Map(builder.Build());
    }

    private static WebApplication Map(WebApplication app)
    {
        // The qualification probe: 200 only when this app really reads its container with its own identity.
        app.MapGet("/health", async (IItemStore items, ILogger<IItemStore> log, CancellationToken ct) =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await items.CheckAccessAsync(timeout.Token);
                return Results.Ok(new { status = "healthy" });
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Health check failed");
                return Results.Json(new { status = "unhealthy", reason = Reason(e) }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapPost("/items", (NewItem body, IItemStore items, ILogger<IItemStore> log, CancellationToken ct) =>
        {
            var item = new Item(Guid.NewGuid().ToString("N"), body.Text);
            return Guard(log, async () =>
            {
                await items.SaveAsync(item, ct);
                return Results.Created($"/items/{item.Id}", item);
            });
        });

        app.MapGet("/items/{id}", (string id, IItemStore items, ILogger<IItemStore> log, CancellationToken ct) =>
            Guard(log, async () => await items.GetAsync(id, ct) is { } item ? Results.Ok(item) : Results.NotFound()));

        return app;
    }

    // A store failure is a 503 for this request only; the next request tries again.
    private static async Task<IResult> Guard(ILogger log, Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Store request failed");
            return Results.Json(new { reason = Reason(e) }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static string Reason(Exception e) => e switch
    {
        OperationCanceledException => "timed out reaching Cosmos DB",
        CosmosException c when c.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
            $"Cosmos DB denied access ({(int)c.StatusCode}): the identity has no grant yet",
        CosmosException c => $"Cosmos DB returned {(int)c.StatusCode}",
        _ => $"{e.GetType().Name}: {e.Message.Split('\n')[0]}",
    };
}
