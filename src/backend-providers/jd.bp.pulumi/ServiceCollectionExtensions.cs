using jd.core.bp;
using Microsoft.Extensions.DependencyInjection;

namespace jd.bp.pulumi;

public static class ServiceCollectionExtensions
{
    public static void RegisterPulumiBackend(this ServiceCollection services, Action<PulumiBackendOptions> action)
    {
        services.Configure(action);
        services.AddSingleton<IBackEndProvider, BackendProvider>();
    }
}
