# 0022. A platform-neutral deployer identity

- **Status:** Proposed — pending review
- **Origin:** S28 ([architecture/multi-cloud.md](../architecture/multi-cloud.md) §6)

## Context
`DeployerIdentity(TenantId, ClientId, ObjectId)` is Entra's shape. It is written into release and approval records, approvers are
listed as Entra object ids, and approval counting and separation of duties compare `objectId`
([release-record.md §3](../architecture/release-record.md#3-identity-and-the-trust-boundary), [approvals.md §3](../architecture/approvals.md#3-approval-policy-as-data)).
A GCP service account has no tenant or object id; its stable key is its unique id.

## Decision
- **`DeployerIdentity(Directory, Principal, Name)`.** `Principal` is the stable key and the only field any rule compares; `Directory`
  is where the principal lives; `Name` is for people. Azure: `tid`, `oid`, `appid`. GCP: the service account's project, its unique id,
  its email.
- **Each platform's adapter of `IDeployerIdentity` fills it** from the platform's own answer about the credential, never from a file.
- **Records write `identity: {directory, principal, name}`.** No version field: the old identity shape stays in the record schemas as
  "read, never written", as `tier` does, and the readers map it to the new one.
- **Approvers** may be any platform's principal key.
- **Records gain no platform.** Which environment qualifies a promotion is already explicit data, the approval policy's `qualifiedOn`;
  whether promotion across platforms is refused is the user's open choice 5 ([multi-cloud.md §13](../architecture/multi-cloud.md#13-open-choices-for-the-user)).

## Consequences
- Records written before the change keep counting: a legacy record and a new one of the same principal are one deployer.
- A person with principals on two clouds is two principals, as two Azure service principals are today; separation remains per principal
  ([approvals.md §4](../architecture/approvals.md#4-approving)).
- The golden records gain a legacy twin that must read to the same principal.
- Rejected: **keeping Entra's fields and filling them on GCP:** a GCP account has no tenant or object id, so the fields would hold
  values that mean something else.
- Rejected: **a platform field in every record, a missing one read as `azure`:** a reader holding a platform name is catalog data in
  code, and `qualifiedOn` already says which environment's records count.

## Related
- [0021](0021-platform-dimension.md), [0007](0007-definition-upload-snapshot.md)
- [architecture/multi-cloud.md](../architecture/multi-cloud.md), [architecture/release-record.md](../architecture/release-record.md),
  [architecture/approvals.md](../architecture/approvals.md)
