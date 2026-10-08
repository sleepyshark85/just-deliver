# 0007. Workload definition is uploaded and snapshotted by the platform

- **Status:** Proposed — pending review
- **Origin:** open-questions A2

## Context
A workload definition could live in the team's repository or in a platform database. [0006](0006-infra-deployed-with-app.md) identifies a deployment by image tag plus definition SHA, so whatever is stored must keep the audit trail intact even if the source repository changes.

## Decision
The team decides where to author and store the definition; keeping it in the workload's own code repository is recommended but not required. The definition is **uploaded to the platform to create a deployment, and the platform stores what it receives.**

- **The upload is the snapshot.** Storing only a pointer would leave the audit trail dangling after a force-push, a deleted branch or a moved repository. The platform hashes and retains the content; any repository and commit reference is kept as provenance alongside it.
- **Provenance is attested, not verified.** Under a push model the platform cannot confirm submitted content matches the claimed commit unless it also fetches. The record says: this content, submitted by this identity, at this time.
- **The upload identity is the trust boundary.** Whatever credential a team's pipeline holds can submit any definition for that workload, so the approval gate on sensitive diffs catches a bad submission, not the storage location.
- **Storage and enforcement reinforce each other.** Holding the previous deployment's definition lets the platform diff an incoming one and route sensitive changes (a new dependency, an override, a policy-relevant field) into an approval gate, regardless of how the file was edited or reviewed upstream. Enforcement sits at the deploy gate, consistent with [0006](0006-infra-deployed-with-app.md) and [0008](0008-enforcement-in-landing-zone.md).
- **The platform owns the workload registry even though it does not own the file**: workload name, owning team, environments, and where its definition is expected to come from. Without a registry there is no inventory, and E51 and G42 are impossible.
- **Templates are scaffolding, not inheritance.** A platform template helps a team author a valid definition; it is copied and diverged from. "Platform decides how" logic lives in mappings and policies resolved at deploy time. Propagating templates would create two overlapping inheritance systems and contradict lazy propagation in [0006](0006-infra-deployed-with-app.md).

## Consequences
- Release record holds the full definition content, so definition retention is bounded by audit retention (F39), not by a team repo's lifetime.
- Validation must be available before upload — a CLI/CI step (H50).
- `metadata.environment` should be removed from the schema; environment is a deployment parameter (H50).

**Follow-ups:**
- **Open:** the registration flow — whether a workload is registered explicitly before its first deployment or created implicitly by it, and who may register one.

## Related
- [0004](0004-two-layer-definition-model.md), [architecture/workload-definition.md](../architecture/workload-definition.md)
- [open-questions](../open-questions.md): E51, F39, G42, H50
