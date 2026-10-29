# ADR 0002: Deterministic oMVP

## Status

Accepted product direction for the oMVP documentation.

## Context

The first product must prove that Proofly can independently verify completeness, balances, transaction lifecycles and safeguarding coverage. Adding model integrations, agent workflows or MCP would increase security, product and operational scope before the core control value is demonstrated.

## Decision

The oMVP will contain deterministic controls and structured user workflows only.

The following are outside the oMVP:

- AI-assisted control conclusions;
- agent-based investigation;
- MCP access;
- external model or personal subscription connections;
- predictive risk scoring; and
- autonomous workflow decisions.

The architecture may be extended later, but no current component should depend on a model connection.

## Consequences

- every financial conclusion is reproducible from evidence and a ruleset version;
- the first security boundary is easier to explain and test;
- product screens focus on evidence, exceptions and approval;
- the repository does not include an agent application; and
- later intelligence features require a separate architecture and product decision.

