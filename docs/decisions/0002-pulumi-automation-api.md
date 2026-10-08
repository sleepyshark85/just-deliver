# 0002. Provision with Pulumi Automation API from a background worker

- **Status:** Proposed — pending review
- **Origin:** investigation notes (decisions and approach; provisioning design; Pulumi notes)

## Context
What gets provisioned depends on what teams declare, so the set of resources changes from one workload to the next. Calling `terraform apply` or `az deployment` directly inside an API request is fragile:
- Long-running operations block the request or need awkward async workarounds.
- Progress is hard to stream.
- Failures are hard to handle and retry cleanly.

Terraform and Bicep can only be driven by a CLI subprocess. HCL is awkward for variable resource lists, and Bicep is template-oriented, which does not suit this case.

## Decision
- Use Pulumi rather than Terraform or Bicep, driven programmatically through the **Automation API** from the platform backend.
- Never run provisioning inside an API request. The API enqueues a job, a background worker runs it through the Automation API, and the worker pushes status to clients or clients poll for it.
- Self-hosted execution: no Pulumi Cloud account and no Pulumi Deployments.
- Keep state self-hosted in **Azure Blob Storage**, with no dependency on the Pulumi Cloud SaaS.

## Consequences
- **"No shelling out" holds only partly.** Self-hosted Automation API still runs the `pulumi` CLI as a subprocess. The rule therefore means **never shell out from the API request path**: the worker does start the CLI, and the Automation API wraps it with typed results and events.
- Every worker host needs the `pulumi` CLI and provider plugins. Each stack operation pays a fixed startup cost (CLI process plus provider plugins; `azure-native` is slow to initialise), so a warm plugin cache or a worker image with the plugins baked in matters.
- **State backend differs by stage.** A local file backend is acceptable for local development only. Local state cannot work on ephemeral hosts (Container Apps Jobs, CI runners), so the switch to Azure Blob Storage must happen before execution moves off a persistent machine.
- **Worker hosting is not settled.** The Pulumi notes rank a CI/CD pipeline first (its approval gates and logs provide governance), then Container Apps Jobs once a self-serve API exists, and advise against Azure Functions (timeout risk). How a "background worker behind the API" relates to "CI/CD pipeline first" needs to be reconciled.
- The "real code handles dynamic graphs" argument for Pulumi now applies to the orchestrator, not to the resource programs, which are static YAML ([0003](0003-pulumi-yaml-template-library.md)).

## Related
- [0003 Pulumi YAML template library](0003-pulumi-yaml-template-library.md), [0004 two-layer definition model](0004-two-layer-definition-model.md)
- [architecture/provisioning.md](../architecture/provisioning.md), [plans/mvp.md](../plans/mvp.md), [plans/phase-0-bootstrap.md](../plans/phase-0-bootstrap.md)
