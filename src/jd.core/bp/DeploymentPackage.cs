using System;
using System.Collections.Generic;

namespace jd.core.bp;

public class DeploymentPackage
{
    public string Name { get; set; }
    public string Version { get; set; }

    public string DeploymentContent { get; set; }
    public Dictionary<string, ConfigEntry> DeploymentParameters { get; set; }
}
