using jd.core.bp;
using Microsoft.Extensions.DependencyInjection;
using Pulumi.Automation.Events;

namespace jd.bp.pulumi;

/// <summary>Adds the Pulumi backend to the composition root's container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IBackEndProvider"/> as the Pulumi backend, configured by <paramref name="action"/>.</summary>
    public static void RegisterPulumiBackend(this ServiceCollection services, Action<PulumiBackendOptions> action)
    {
        services.Configure(action);
        services.AddSingleton<IResourceChangeParser<StepEventMetadata>, PulumiResourceChangeParser>();
        services.AddSingleton<IBackEndProvider, PulumiBackendProvider>();
    }
}
