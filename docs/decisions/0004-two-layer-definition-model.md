# 0004. Two-layer definition model: team definitions plus IT Ops global policy

- **Status:** Proposed — pending review
- **Origin:** investigation notes (decisions and approach; provisioning design)

## Context
There is no fixed infrastructure template; what gets provisioned depends on what teams declare. IT Ops still has to apply organisation-wide rules (security, networking) to every workload, without each team having to remember them, and without manual review of each request.

## Decision
Definitions have two layers, merged at provisioning time:

- **Team layer (per workload).** Each team declares what its workload needs, and the platform provisions exactly that and nothing more:
  ```
  workload: my-service
  requires:
    - database
    - azure-keyvault
    - azure-service-bus
    - azure-appinsights
  ```
- **IT Ops global layer.** Platform-wide policies apply to every workload, and teams do not declare them:
  ```
  global:
    - all workloads receive traffic through azure-application-gateway
  ```

Flow: the team submits a definition → the API parses and validates it → global policies are merged in → a resource graph is built → a background worker provisions it ([0002](0002-pulumi-automation-api.md)) → status is reported back without blocking the API call.

## Consequences
- IT Ops writes a guardrail once and it applies to every new deployment, which supports scaling governance without adding headcount.
- Teams describe intent, not infrastructure details, consistent with "structured data over free text" ([0005](0005-build-bc-first-design-for-d.md)).
- Merge semantics (precedence, conflicts between team requests and policy) still need to be specified; see [architecture/resolver.md](../architecture/resolver.md).
- Where enforcement lives (platform merge vs landing-zone policy) is covered in [0008](0008-enforcement-in-landing-zone.md).
- The example syntax above is illustrative; the actual schema is in [architecture/workload-definition.md](../architecture/workload-definition.md).

## Related
- [0001 custom build](0001-custom-build-api-and-clients.md), [0007 definition upload snapshot](0007-definition-upload-snapshot.md)
- [architecture/provisioning.md](../architecture/provisioning.md), [open-questions.md](../open-questions.md)
