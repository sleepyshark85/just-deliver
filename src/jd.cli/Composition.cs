using Azure.Core;
using jd.azure;
using jd.bp.pulumi;
using jd.core.bp;
using jd.orchestrator.usecases;
using Microsoft.Extensions.DependencyInjection;

namespace jd.cli;

/// <summary>
/// The CLI's composition root: the <see cref="Connections"/> every command runs with, from the environment variables and jd's data directory
/// (docs/architecture/application.md section 5). The one place the adapters and the DI container are named.
/// </summary>
internal static class Composition
{
    private const string PulumiHomeVariable = "PULUMI_HOME";
    private const string ScratchDirectoryVariable = "JD_SCRATCH_DIR";

    /// <summary>The connections over <paramref name="environmentVariable"/>; nothing is built until a factory is invoked.</summary>
    public static Connections Connect(Func<string, string?> environmentVariable) => new(
        Records: () => DataDirectory.Resolve(environmentVariable).Records,
        // The ARM_* service principal Pulumi uses too, or the Azure CLI sign-in. Throws InvalidOperationException naming the unset ones of a partial ARM_* set.
        Identity: () => new ArmDeployerIdentity(ArmCredential(environmentVariable)),
        // The same credential as the identity, so the principal named in a record is the one that observed.
        LiveRevisions: () => new ArmLiveRevisions(ArmCredential(environmentVariable)),
        Backend: () => CreatePulumiBackend(environmentVariable),
        // The probe judges the revision's own answer, so a redirect is a failed probe, not a hop to some other host.
        Probe: () => new HttpRevisionProbe(HttpRevisionProbe.CreateClient()),
        Time: () => TimeProvider.System,
        Templates: PulumiTemplates.Of);

    // The Pulumi backend on the state in jd's data directory (created on first use) and its key, with the optional Pulumi home and scratch directory.
    internal static Action<PulumiBackendOptions> PulumiSettings(Func<string, string?> environmentVariable)
    {
        var (state, key) = DataDirectory.Resolve(environmentVariable).PrepareState();
        return options =>
        {
            options.BackendUrl = "file://" + state;
            options.ConfigPassPhraseFile = key;
            options.PulumiHome = environmentVariable(PulumiHomeVariable);
            options.ScratchDirectory = environmentVariable(ScratchDirectoryVariable) ?? options.ScratchDirectory;
        };
    }

    private static TokenCredential ArmCredential(Func<string, string?> environmentVariable) =>
        new ArmCredentials(environmentVariable(ArmCredentials.TenantVariable), environmentVariable(ArmCredentials.ClientVariable), environmentVariable(ArmCredentials.SecretVariable)).ToCredential();

    private static IBackEndProvider CreatePulumiBackend(Func<string, string?> environmentVariable)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterPulumiBackend(PulumiSettings(environmentVariable));
        return services.BuildServiceProvider().GetRequiredService<IBackEndProvider>();
    }
}
