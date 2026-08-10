using jd.core.bp;
using Microsoft.Extensions.DependencyInjection;
using Pulumi.Automation.Events;

namespace jd.bp.pulumi;

public static class ServiceCollectionExtensions
{
    public static void RegisterPulumiBackend(this ServiceCollection services, Action<PulumiBackendOptions> action)
    {
        services.Configure(action);
        services.AddSingleton<IResourceChangeParser<StepEventMetadata>, PulumiResourceChangeParser>();
        services.AddSingleton<IBackEndProvider, PulumiBackendProvider>();
    }
}
