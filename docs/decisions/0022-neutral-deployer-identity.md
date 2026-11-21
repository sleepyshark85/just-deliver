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
  is where the principal lives; `Name` is for people. Azure: `tid`, `oid`, `appid`. GCP: the issuer, the service account's unique id, its
  email.
- **Each platform's adapter of `IDeployerIdentity` fills it** from the platform's own answer about the credential, never from a file.
- **Records write `identity: {directory, principal, name}`** and `environment.platform`. No version field: the old identity shape stays
  in the record schemas as "read, never written", as `tier` does, and the readers map it to the new one; a record without a platform was
  written when jd knew only Azure and is read as `azure`.
- **Approvers** may be any platform's principal key.
- **Promotion stays on one platform:** the target's platform must equal the qualifying records'.

## Consequences
- Records written before the change keep counting: a legacy record and a new one of the same principal are one deployer.
- A person with principals on two clouds is two principals, as two Azure service principals are today; separation remains per principal
  ([approvals.md §4](../architecture/approvals.md#4-approving)).
- The golden records gain a legacy twin that must read to the same principal.

## Related
- [0021](0021-platform-dimension.md), [0007](0007-definition-upload-snapshot.md)
- [architecture/multi-cloud.md](../architecture/multi-cloud.md), [architecture/release-record.md](../architecture/release-record.md),
  [architecture/approvals.md](../architecture/approvals.md)
