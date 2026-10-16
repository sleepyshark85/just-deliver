using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using jd.core.revisions;

namespace jd.azure;

/// <summary>
/// The revisions of a Container App as Azure Resource Manager reports them: traffic weight and container images, read live for the app
/// whose resource id it is given (<c>options</c>: a test seam for the transport). Only revisions of Container Apps are known here; no workload, name or image is.
/// </summary>
public sealed class ArmLiveRevisions(TokenCredential credential, ArmClientOptions? options = null) : ILiveRevisions
{
    /// <summary>Throws <see cref="InvalidOperationException"/>, fit to show a person, naming the status and error code Azure answered with and nothing else of its reply.</summary>
    public async Task<IReadOnlyList<LiveRevision>> GetAsync(string resourceId, CancellationToken cancellationToken)
    {
        try
        {
            var revisions = new List<LiveRevision>();
            await foreach (var revision in new ArmClient(credential, null, options).GetContainerAppResource(new ResourceIdentifier(resourceId)).GetContainerAppRevisions().GetAllAsync(cancellationToken: cancellationToken))
            {
                var data = revision.Data;
                revisions.Add(new LiveRevision(data.Name, data.TrafficWeight ?? 0, (data.Template?.Containers ?? []).Select(c => c.Image).ToList()));
            }

            return revisions;
        }
        catch (RequestFailedException ex)
        {
            // The SDK's message is a page of HTTP detail; the status and Azure's error code say what went wrong without echoing a payload.
            throw new InvalidOperationException($"Azure refused to list the revisions of '{resourceId}': HTTP {ex.Status}{(string.IsNullOrEmpty(ex.ErrorCode) ? string.Empty : $", {ex.ErrorCode}")}.", ex);
        }
        catch (AggregateException ex)
        {
            // What Azure.Core throws when every retry failed to reach Azure; its text lists each attempt and the transport's own message.
            throw new InvalidOperationException($"Azure could not be reached to list the revisions of '{resourceId}'; check the network.", ex);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // The SDK refuses an id it cannot take for a subscription-scoped resource.
            throw new InvalidOperationException($"'{resourceId}' is not an Azure resource id.", ex);
        }
        catch (AuthenticationFailedException ex)
        {
            throw new InvalidOperationException($"Azure did not accept the credential while listing the revisions of '{resourceId}'; check {ArmCredentials.TenantVariable}, {ArmCredentials.ClientVariable} and {ArmCredentials.SecretVariable}.", ex);
        }
    }
}
