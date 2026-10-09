namespace jd.core.bp;

/// <summary>The deployment templates of the catalog, by the name nodes reference (<c>azure/log-analytics</c>).</summary>
public interface ITemplateStore
{
    /// <summary>The template's content (<see cref="DeploymentPackage.DeploymentContent"/>). Throws when the template does not exist.</summary>
    string GetContent(string templateName);
}
