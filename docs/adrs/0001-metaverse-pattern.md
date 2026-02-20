# ADR 0001: Use Metaverse Pattern for Identity Management

## Status
Accepted

## Context
JIM needs to synchronise identity data between multiple enterprise systems (Active Directory, HR systems, databases, cloud applications). Each system may have different schemas, update frequencies, and data quality.

We need an architecture that:
- Provides a single source of truth for identity data
- Supports bidirectional synchronisation
- Handles schema differences between systems
- Allows precedence rules for conflicting data

## Decision
We will implement the **metaverse pattern**, where:
1. A central **metaverse** stores canonical identity objects
2. Each connected system has a **staging area** with copies of external data
3. **Sync rules** define how data flows between staging and metaverse
4. **Attribute flows** handle transformation and precedence

## Consequences

### Positive
- Clear separation between external system data and canonical data
- Flexible precedence rules per attribute
- Supports complex transformation logic
- Audit trail of all changes
- Resilient to temporary system unavailability

### Negative
- More complex than direct system-to-system sync
- Requires storage for staging area data
- Learning curve for administrators familiar with direct sync tools

## References
- Microsoft Identity Manager (MIM) metaverse architecture
- FIM/MIM Synchronisation Service concepts
