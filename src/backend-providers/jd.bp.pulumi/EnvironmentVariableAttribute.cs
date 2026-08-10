namespace jd.bp.pulumi;

[AttributeUsage(AttributeTargets.Property)]
internal class EnvironmentVariableAttribute : Attribute
{
    public EnvironmentVariableAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; set; }
}
