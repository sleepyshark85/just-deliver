using jd.core.bp;
using Pulumi.Automation;
using Pulumi.Automation.Events;
using System.Linq;
using System.Text.Json;

namespace jd.bp.pulumi;

internal class PulumiResourceChangeParser : IResourceChangeParser<StepEventMetadata>
{
    public ResourceChange? Parse(StepEventMetadata metadata)
    {
        if (metadata.Op == OperationType.Same)
        {
            return null;
        }

        return new ResourceChange
        {
            Urn = metadata.Urn,
            Type = metadata.Type,
            Operation = metadata.Op.ToString(),
            ChangedProperties = metadata.DetailedDiff?.Keys.Select(path => new PropertyChange
            {
                Path = path,
                OldValue = FormatValue(GetTopLevelValue(metadata.Old, path)),
                NewValue = FormatValue(GetTopLevelValue(metadata.New, path)),
            }).ToList() ?? new List<PropertyChange>(),
        };
    }

    private static object? GetTopLevelValue(StepEventStateMetadata? state, string path)
    {
        var key = path.Split('.', '[')[0];
        return state?.Inputs is not null && state.Inputs.TryGetValue(key, out var value) ? value : null;
    }

    private static string? FormatValue(object? value) => value switch
    {
        null => null,
        string s => s,
        System.Collections.IEnumerable => JsonSerializer.Serialize(value),
        _ => value.ToString(),
    };
}
