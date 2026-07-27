# Dynamic, Definition-Driven Provisioning

## Concept

What gets provisioned depends entirely on what teams declare. There is no fixed infrastructure template — resources are constructed dynamically from definition files submitted by teams and global policies set by IT Ops.

## Two Layers of Definition

### Team-defined (per workload)
Each team declares what their workload needs:
```
workload: my-service
requires:
  - database
  - azure-keyvault
  - azure-service-bus
  - azure-appinsights
```
The platform provisions exactly what is declared, nothing more.

### IT Ops-defined (global policy)
IT Ops defines platform-wide rules that apply to all workloads:
```
global:
  - all workloads receive traffic through azure-application-gateway
```
These are merged with team definitions at provisioning time — teams don't need to declare them.

## Provisioning Flow

```
Team submits definition file
        ↓
API parses and validates definition
        ↓
Global IT Ops policies are merged in
        ↓
Dynamic resource graph is constructed
        ↓
Background worker executes provisioning via Pulumi Automation API
        ↓
Status streamed back to platform (not a blocking API call)
```

## Technology Choice: Pulumi with Automation API

**Why Pulumi over Terraform or Bicep:**

- **Automation API** — Pulumi can be driven programmatically as a library from within the backend, no subprocess or CLI shelling required. The API enqueues a job; a background worker calls Pulumi in-process.
- **Dynamic resource graphs** — real code (TypeScript/Python/Go/C#) with loops and conditionals. Definition files with variable resource lists map naturally to code; HCL (Terraform) is awkward for this, Bicep is template-oriented and not suitable.
- **Terraform/Bicep limitation** — both require CLI subprocess calls from an API, which are fragile, hard to stream progress from, and difficult to handle failures cleanly.

**State backend:** Azure Blob Storage (self-hosted, no dependency on Pulumi Cloud SaaS).

## Why Not Shell Out to CLI

Calling `terraform apply` or `az deployment` directly from an API request is fragile:
- Long-running operations block or require hacky async workarounds
- Progress is hard to stream
- Failures are difficult to handle and retry cleanly

The correct pattern: API enqueues a provisioning job → background worker executes → status is polled or pushed to clients.
