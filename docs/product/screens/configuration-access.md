# Configuration and Access

## Status

**Proposed.** Not implemented.

## Purpose

Configuration and Access provides controlled administration of source definitions, mappings, control parameters, users, roles and approval responsibilities.

## Primary users

- configuration administrators;
- security administrators; and
- control owners reviewing changes.

## Main layout

### Source configuration

Defines expected deliveries, business calendars, schema versions, source owners and validation requirements.

### Control configuration

Shows versioned mappings, matching parameters, lifecycle expectations, materiality thresholds and safeguarding eligibility settings.

### Users and roles

Assigns roles, legal-entity scope and allowed actions. Separation-of-duties conflicts are displayed before a change can be activated.

### Change history

Shows draft, approved, active and retired configuration versions, including proposer, approver and effective date.

## Primary actions

- create a draft configuration change;
- validate it against structural and domain rules;
- submit it for approval;
- approve or reject when authorised;
- schedule activation; and
- manage user roles and scope.

## Important states

- draft;
- invalid;
- review required;
- approved;
- scheduled;
- active;
- retired; and
- rejected.

## oMVP boundary

The oMVP may use controlled file-based configuration for complex rules while providing a structured view of active versions, basic source settings, users and roles.

