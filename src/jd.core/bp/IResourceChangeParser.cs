namespace jd.core.bp;

/// <summary>Turns a backend's per-resource engine event (<typeparamref name="TMetadata"/>) into the backend-neutral <see cref="ResourceChange"/>.</summary>
public interface IResourceChangeParser<TMetadata>
{
    /// <summary>The change the event describes; null when the resource is unchanged.</summary>
    ResourceChange? Parse(TMetadata metadata);
}
