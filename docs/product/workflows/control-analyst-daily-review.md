# Control Analyst Daily Review

## Status

Proposed user workflow. Not implemented.

## User outcome

The control analyst should be able to determine whether the previous business date is ready for sign-off, investigate every material break and leave a complete evidence trail without accessing internal databases.

## Primary flow

```mermaid
flowchart TD
    A[Open Daily Control Room] --> B[Select legal entity and business date]
    B --> C[Review run status and source readiness]
    C --> D{Mandatory source missing or rejected?}

    D -- Yes --> E[Open Sources and inspect delivery]
    E --> F[Assign source exception or await corrected evidence]
    F --> G[Create a new run version after correction]
    G --> C

    D -- No --> H[Review ledger, matching, lifecycle and safeguarding results]
    H --> I{Material break detected?}

    I -- Yes --> J[Open exception from attention queue]
    J --> K[Inspect transaction timeline, match decision and source evidence]
    K --> L[Record investigation and propose outcome]
    L --> M{Independent approval required?}
    M -- Yes --> N[Control owner reviews proposal]
    N --> O{Approved?}
    O -- No --> K
    O -- Yes --> P[Apply approved resolution or qualification]
    M -- No --> P
    P --> Q{Blocking exceptions remain?}
    Q -- Yes --> J
    Q -- No --> R[Prepare run for sign-off]

    I -- No --> R
    R --> S[Control owner reviews summary and qualifications]
    S --> T{Sign-off accepted?}
    T -- No --> J
    T -- Yes --> U[Sign exact run version]
    U --> V[Generate reproducible evidence pack]
    V --> W[Run complete]
```

## Important alternate states

- A source that is late but still inside its agreed window remains pending rather than failed.
- Incomplete mandatory evidence visibly qualifies downstream results and blocks unqualified sign-off.
- An analyst cannot approve their own restricted resolution.
- Late or corrected evidence after sign-off creates a new run version; it never rewrites the signed result.
- A user without access to the selected legal entity sees a restriction state rather than partial financial data.

## Screen mapping

| Workflow step | Primary screen |
| --- | --- |
| Select and assess run | Daily Control Room |
| Inspect delivery problem | Sources |
| Understand transaction journey | Transaction Explorer |
| Review deterministic relationship | Matching Workbench |
| Investigate and resolve break | Exception Detail |
| Review coverage | Safeguarding Position |
| Approve and attest | Run Sign-off |
| Reproduce conclusion | Evidence and Audit Explorer |
