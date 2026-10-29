# Roles and Approvals

## Status

Proposed oMVP authorisation and separation-of-duties model.

## Principles

- Access is denied unless explicitly granted.
- Permissions are scoped by role and legal entity.
- Read, propose, approve and administer are distinct capabilities.
- A user must not approve their own material proposal where separation of duties applies.
- Every permission and approval action is audited.

## Roles

### Control analyst

May review runs and evidence, investigate assigned exceptions, add notes, propose resolutions and request reprocessing where policy allows.

### Control owner

May perform analyst actions, approve material resolutions and adjustments, accept qualifications and sign off a run within their legal-entity scope.

### Configuration administrator

May create and validate source, mapping and control-configuration drafts. They cannot activate their own material configuration change.

### Security administrator

May manage users, roles and legal-entity scope. They cannot sign off financial controls solely because of this role.

### Read-only reviewer

May inspect permitted runs, evidence lineage, exceptions, approvals and evidence packs without modifying state.

## Capability matrix

| Capability | Analyst | Control owner | Configuration admin | Security admin | Reviewer |
| --- | --- | --- | --- | --- | --- |
| View scoped runs and results | Yes | Yes | As configured | As configured | Yes |
| View source evidence | Yes | Yes | As configured | No by default | As configured |
| Investigate exceptions | Yes | Yes | No | No | No |
| Propose resolution | Yes | Yes | No | No | No |
| Approve material resolution | No | Yes | No | No | No |
| Propose safeguarding adjustment | Yes | Yes | No | No | No |
| Approve safeguarding adjustment | No | Yes, not own proposal | No | No | No |
| Sign off run | No | Yes | No | No | No |
| Draft configuration | No | As configured | Yes | No | No |
| Approve configuration | As configured | As configured | Not own proposal | No | No |
| Manage users and roles | No | No | No | Yes | No |
| Export evidence pack | As configured | Yes | No | No | Yes |

## Approval objects

Each approval records:

- object type and identifier;
- proposal version;
- proposer;
- approver;
- decision and timestamp;
- rationale;
- supporting evidence references; and
- policy version requiring the approval.

Changing a proposal after approval invalidates the approval and requires a new decision.

## Material actions requiring approval

- accepted material difference;
- material manual match;
- safeguarding adjustment;
- run qualification;
- run sign-off;
- activation of material control or mapping configuration; and
- permission changes granting approval or administration capabilities.

## Emergency access

Emergency access is outside the initial oMVP unless required by the pilot. If introduced, it must be time-limited, reasoned, independently reviewed and visibly reported.

