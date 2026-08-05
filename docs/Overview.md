# Just Deliver platform

## The Problem: Why We Need This

Today's release process is largely manual — unstructured handoffs, paper approvals, synchronous meetings between Dev and IT Ops, no audit trail, and blurry accountability. As the organization grows, this model breaks:
- **Ops becomes a bottleneck.** Every deployment waits on the Ops team. Scaling teams doesn't scale the system — it degrades it.
- **Accountability splits.** Dev owns code. Ops owns infrastructure, secrets, deployments. When something breaks, responsibility is unclear.
- **No auditability.** Paper trails are hard to audit consistently. Compliance evidence collection is manual.
- **Knowledge lives in people.** Onboarding requires tribal knowledge. Ramp-up is slow.

An Internal Developer Platform solves this by automating handoffs, enforcing policy at the infrastructure layer, and giving teams self-serve tools within guardrails.

## What We Want to Achieve

### For organizations
- Enable development teams to release their work without being blocked by manual handoffs or waiting on Ops
- Full governance and control: flexibility in setting up deployment processes with quality gates and approval gates
- Full auditability, compliance, and easy rollback — all automatic, not manual
- Ensure resources teams provision adhere to organization best practices for security and other aspects
- Ensure development teams use their provided resources and quota wisely
- Ensure resources of different teams don't interfere with each other

### For development teams
- Set up and tear down environments quickly without manual provisioning requests
- Deploy their workload quickly and easily, within organizational policy
- No manual configuration of secrets per environment — automatic injection across all environments
- Single source of truth for release status, approvals, and audit trail

### For platform team
- Shift from executor to enabler: define guardrails and policy once, then let the platform enforce them automatically across all teams
- Quickly set up a master landing zone for shared services used by the whole organization (VPN, Entra, etc.)
- Quickly provision landing zones for teams, ensuring isolation and no cross-team interference
- Scale governance without scaling headcount: enforce security baselines, quota limits, compliance policies automatically rather than through manual review
- Reduce toil: move away from reactive firefighting (approving every deployment, debugging ad-hoc configurations) to proactive policy setting
- Full visibility into what teams are doing: understand resource usage, deployment patterns, compliance across all teams without micromanaging each request
- Iterate on policies without friction: change governance rules and have them take effect immediately for new deployments