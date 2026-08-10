namespace jd.core.bp;

public interface IResourceChangeParser<TMetadata>
{
    ResourceChange? Parse(TMetadata metadata);
}
