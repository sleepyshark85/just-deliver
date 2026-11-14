namespace jd.resolver;

/// <summary>
/// The roles the engine reads by name (docs/architecture/documents.md Appendix B): the engine's interface, like the expression roots. A schema
/// says which field holds each (<c>is</c>, or <c>select</c> for the value a map is keyed by); no field name is spelled here.
/// </summary>
internal static class Roles
{
    /// <summary>A document whose parts the binder fills (<see cref="catalog.PartOwner"/>).</summary>
    public const string Owner = "owner";

    /// <summary>A part: nodes deployed together and the exports evaluated from them.</summary>
    public const string Part = "part";

    /// <summary>A runtime's part, deployed by the release flow; bound like any other part.</summary>
    public const string RuntimePart = "runtime.part";

    /// <summary>A part's nodes, by name.</summary>
    public const string PartNodes = "part.nodes";

    /// <summary>A part's exports, by name.</summary>
    public const string PartExports = "part.exports";

    /// <summary>The shared resources a part reads; a value held by an object enclosing the part counts for each of its parts.</summary>
    public const string PartReads = "part.reads";

    /// <summary>A part's scope, when the schema does not state it with <c>scope</c>.</summary>
    public const string PartScope = "part.scope";

    /// <summary>A node's template.</summary>
    public const string NodeTemplate = "node.template";

    /// <summary>A node's kind (<see cref="catalog.NodeKind"/>).</summary>
    public const string NodeKind = "node.kind";

    /// <summary>A node's config.</summary>
    public const string NodeConfig = "node.config";

    /// <summary>The exports every selected part of an owner exports exactly.</summary>
    public const string Contract = "contract";

    /// <summary>The layers, as an <c>enum</c> whose listed order is the deploy order.</summary>
    public const string Layer = "layer";

    /// <summary>The catalog's version, recorded with every deployment.</summary>
    public const string CatalogVersion = "catalog.version";

    /// <summary>A declared environment type's layer; the object holding it is the type, named by the key it is held under.</summary>
    public const string EnvironmentTypeLayer = "environment-type.layer";

    /// <summary>Whether an environment of a declared type must carry an approval block.</summary>
    public const string EnvironmentTypeGated = "environment-type.gated";

    /// <summary>The naming rules, by the resource kind <c>name()</c> is given.</summary>
    public const string NamingRules = "naming.rules";

    /// <summary>A naming rule's pattern, with the placeholders <c>name()</c> fills.</summary>
    public const string NamingPattern = "naming.pattern";

    /// <summary>A naming rule's longest legal name.</summary>
    public const string NamingMaxLength = "naming.max-length";

    /// <summary>A naming rule's legal characters, as a regex character class.</summary>
    public const string NamingAllowed = "naming.allowed";

    /// <summary>The role definitions, by the name <c>${role.&lt;name&gt;}</c> reads.</summary>
    public const string RoleDefinitions = "roles";

    /// <summary>The input naming a grant template's target, by the template it is held under.</summary>
    public const string GrantTargetInput = "grant.target-input";

    /// <summary>A policy's name; the documents of a kind that binds it are policies.</summary>
    public const string PolicyName = "policy.name";

    /// <summary>The environment's name, read as <c>${env.name}</c>.</summary>
    public const string EnvironmentName = "env.name";

    /// <summary>Where the environment is deployed, read as <c>${env.region}</c>.</summary>
    public const string EnvironmentRegion = "env.region";

    /// <summary>The environment's type, which selects an implementation.</summary>
    public const string EnvironmentType = "env.type";

    /// <summary>The components and the runtime the environment offers, by name.</summary>
    public const string EnvironmentOffers = "env.offers";

    /// <summary>The deployed parts' exports, as nested mappings of strings; only a descriptor binds it, never an expression root.</summary>
    public const string EnvironmentValues = "env.values";

    /// <summary>The environment's approval block, whose presence gates it.</summary>
    public const string EnvironmentApproval = "env.approval";

    /// <summary>The environment whose records must show a set qualified.</summary>
    public const string ApprovalQualifiedOn = "approval.qualified-on";

    /// <summary>How many distinct approvers are needed.</summary>
    public const string ApprovalRequired = "approval.required";

    /// <summary>The object ids that may approve.</summary>
    public const string ApprovalApprovers = "approval.approvers";

    /// <summary>Whether an approver must not have deployed the label on the environment the set is qualified on.</summary>
    public const string ApprovalSeparate = "approval.separate";

    /// <summary>The workload's name, read as <c>${workload.name}</c>; the documents of a kind that binds it are workloads.</summary>
    public const string WorkloadName = "workload.name";

    /// <summary>The team that owns the workload, read as <c>${workload.team}</c>.</summary>
    public const string WorkloadTeam = "workload.team";

    /// <summary>The workload's image, read as <c>${workload.image}</c>.</summary>
    public const string WorkloadImage = "workload.image";

    /// <summary>The workload's port, read as <c>${workload.port}</c>; under a list, the first item's.</summary>
    public const string WorkloadPort = "workload.port";

    /// <summary>The workload's variables, by name: workload input into the platform, so each may read only a requirement's export (boundary 6).</summary>
    public const string WorkloadVariables = "workload.variables";

    /// <summary>A requirement of the workload, one per item.</summary>
    public const string Requirement = "requirement";

    /// <summary>A requirement's type: the component it asks for.</summary>
    public const string RequirementType = "requirement.type";

    /// <summary>A requirement's id; absent, the requirement's id is its type (ADR 0011).</summary>
    public const string RequirementId = "requirement.id";

    /// <summary>A requirement's class, which selects a class part.</summary>
    public const string RequirementClass = "requirement.class";
}
