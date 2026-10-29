# Evidence and Audit Explorer

## Status

**Proposed.** Not implemented.

## Purpose

The Evidence and Audit Explorer allows authorised users to reproduce a historical conclusion from source delivery through normalisation, calculation, exception and approval.

## Primary users

- internal and external audit reviewers;
- control owners;
- compliance and risk reviewers; and
- administrators investigating processing history.

## Main layout

### Search and scope

Searches by business date, run, source delivery, record, transaction, account, control, exception, user action or evidence-pack identifier. Role permissions constrain results before search is executed.

### Lineage view

Shows the relationship between:

- original source delivery;
- normalised records;
- matching and ledger calculations;
- control results;
- exceptions and resolutions;
- run sign-off; and
- evidence exports.

### Run manifest

Shows input fingerprints, schema and mapping versions, ruleset version, processing timestamps, record and value totals, software version and outcome.

### Activity history

Shows configuration changes, operator actions, automated processing, approvals and exports. Human actions and system actions are visibly distinct.

### Evidence preview

Allows an authorised user to inspect a source excerpt or stored object metadata without losing the immutable reference to the original evidence.

## Primary actions

- trace a result to its source;
- compare two run versions;
- verify an evidence fingerprint;
- inspect the configuration used historically;
- generate a scoped evidence pack; and
- follow lineage between evidence, calculations, exceptions and approval.

## Important states

- complete lineage;
- evidence retained externally by reference;
- access restricted;
- evidence expired under policy;
- integrity verification failed; and
- historical component version unavailable.

## MVP boundary

The MVP requires run manifests, record-to-source lineage, action history and evidence export for a selected business date.

