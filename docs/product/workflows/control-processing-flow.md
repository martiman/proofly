# Control Processing Flow

## Status

Proposed deterministic control process. Not implemented.

## Process objective

For one legal entity and business date, Proofly should transform immutable source evidence into reproducible control results, exceptions and an evidence-backed sign-off decision.

## Processing diagram

```mermaid
flowchart TD
    A[Business calendar and source registry] --> B[Create business-date run]
    B --> C[Await expected source deliveries]

    C --> D1[Transfer instructions]
    C --> D2[Direct-debit events]
    C --> D3[Internal ledger entries and balances]
    C --> D4[Banking transactions and balances]
    C --> D5[Card transaction events and settlements]
    C --> D6[Safeguarding balances]

    D1 --> E[Immutable source evidence store]
    D2 --> E
    D3 --> E
    D4 --> E
    D5 --> E
    D6 --> E

    E --> F[Schema, identity and control-total validation]
    F --> G{Mandatory population complete?}
    G -- No --> H[Create completeness exception and qualify run]
    G -- Yes --> I[Accept delivery population]
    H --> J[Normalise accepted records with provenance]
    I --> J

    J --> K1[Independent ledger reconstruction]
    J --> K2[Matching and reconciliation]
    J --> K3[Transaction lifecycle verification]
    J --> K4[Safeguarding coverage calculation]

    K1 --> L[Aggregate versioned control results]
    K2 --> L
    K3 --> L
    K4 --> L

    L --> M{Material failure or unsupported conclusion?}
    M -- Yes --> N[Create and route durable exception]
    N --> O[Investigation, resolution and approval workflow]
    O --> P[Evaluate sign-off readiness]
    M -- No --> P

    P --> Q{Blocking condition remains?}
    Q -- Yes --> N
    Q -- No --> R[Authorised control-owner sign-off]
    R --> S[Generate evidence pack]
    S --> T[Retain signed run and audit history]

    U[Late or corrected evidence] --> V[Create linked run version]
    V --> F
```

## Process guarantees

- Original evidence remains immutable or content-addressed.
- Every normalised record retains source-delivery and source-record lineage.
- Controls use explicit versions of schemas, mappings, lifecycle models and tolerances.
- Reprocessing identical inputs and rules is deterministic and idempotent.
- Incomplete evidence is never represented as a successful zero result.
- Exceptions, approvals and sign-off append history rather than replacing it.
- Card events, processor settlements and bank movements remain separate populations linked through controlled many-to-one matches.
