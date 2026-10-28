# Sources

## Status

**Proposed.** Not implemented.

## Purpose

Sources shows whether every required input for a business date arrived, passed validation and is available for downstream controls.

## Primary users

- control analysts;
- integration administrators; and
- operations support.

## Main layout

### Delivery summary

Shows expected, received, late, missing, rejected and superseded deliveries for the selected business date and legal entity.

### Source table

Recommended columns:

- source and source owner;
- expected delivery time;
- received timestamp;
- business date;
- schema version;
- record count and value totals;
- validation state; and
- downstream processing state.

### Delivery detail

Shows file or extract metadata, content fingerprint, schema validation, control totals, duplicate checks, sequence checks and processing history. Source evidence is available only to authorised roles.

## Primary actions

- inspect a failed validation;
- retry an authorised ingestion step;
- mark a delivery as superseded by a corrected version;
- open resulting exceptions;
- compare delivery versions; and
- export a source manifest.

## Important states

- expected;
- received;
- validating;
- accepted;
- late;
- missing;
- rejected;
- superseded; and
- processing failed.

## oMVP boundary

The oMVP supports controlled file-based sources, explicit expected-delivery configuration, validation results, source control totals and deterministic reprocessing.

