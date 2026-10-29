# Data Handling

## Status

Proposed oMVP data-handling requirements. Customer policy and jurisdiction-specific obligations may add stricter requirements.

## Data classes

### Source evidence

Original files, extracts, statements and balance evidence. Highest sensitivity within the Proofly data plane.

### Canonical financial data

Normalised client accounts, transactions, ledger entries, balances and lifecycle events.

### Control and workflow data

Run manifests, control results, exceptions, notes, approvals and sign-offs.

### Configuration and reference data

Schemas, mappings, calendars, materiality thresholds, eligibility policies, users and roles.

### Operational metadata

Job status, performance metrics, health information and error diagnostics. Operational metadata must not contain unnecessary client or transaction data.

## Storage requirements

- Source evidence is immutable or content-addressed after acceptance.
- Canonical and workflow data use durable transactional storage.
- Evidence objects and database records retain stable cross-references.
- Monetary values preserve fixed decimal precision and currency.
- Secrets are stored outside source code and ordinary configuration files.

## Encryption

- Encrypt network connections in production.
- Encrypt databases, evidence storage and backups at rest.
- Store connector credentials in an approved secret store.
- Do not place secrets or production evidence in logs.

## Access

- Authenticate every user and service identity.
- Enforce least privilege and legal-entity scope.
- Check permission when data is requested, not only when a screen is opened.
- Audit access to raw source evidence and exported evidence packs.

## Retention and deletion

Retention is configured by data class and customer policy. Deletion must:

- require authorisation;
- preserve legally required audit evidence;
- record what was deleted, when and under which policy;
- include backups according to the approved expiry process; and
- never silently rewrite a historical run conclusion.

The oMVP must not invent a universal retention period.

## Logs and diagnostics

- Use stable identifiers rather than full client data where possible.
- Mask account and personal identifiers in ordinary logs.
- Separate user-visible validation errors from internal diagnostic detail.
- Apply retention and access controls to logs.

## Backups and recovery

- Back up database and evidence storage consistently.
- Verify that evidence references remain valid after restore.
- Test recovery before a production pilot is accepted.
- Record recovery point and recovery time objectives per deployment.
- Recovered runs retain original identifiers, versions and audit history.

## Environments

- Development and automated tests use synthetic data by default.
- Production data does not enter developer environments without explicit authorised handling.
- Credentials and storage are isolated between development, evaluation and production.
- Environment identity is visible in the application.

## Exports

Evidence exports are scoped, attributable and audited. Export generation records requester, scope, timestamp, run version and content fingerprint.

## Incident considerations

The deployment must support suspension of a source connection, credential rotation, access revocation, preservation of relevant audit records and identification of affected deliveries and exports.

## Decisions still required

- customer data-classification mapping;
- jurisdiction-specific retention;
- approved encryption and secret-management products;
- backup location and retention;
- permitted export formats and destinations; and
- production support access model.

