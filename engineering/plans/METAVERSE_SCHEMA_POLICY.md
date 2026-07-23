# Metaverse Schema Policy

- **Status:** Planned
- **Issue:** [#1104](https://github.com/TetronIO/JIM/issues/1104)
- **Related Issues:** [#545](https://github.com/TetronIO/JIM/issues/545) (SCIM 2.0 Client Connector), [#124](https://github.com/TetronIO/JIM/issues/124) (SCIM 2.0 Server Support), [#60](https://github.com/TetronIO/JIM/issues/60) (Attribute Set Templates; the complementary "packs" idea), [#1046](https://github.com/TetronIO/JIM/issues/1046) (Decimal attribute data type)
- **Related Plans:** [`doing/SCIM_CLIENT_CONNECTOR_DESIGN.md`](doing/SCIM_CLIENT_CONNECTOR_DESIGN.md), [`SCIM_SERVER_DESIGN.md`](SCIM_SERVER_DESIGN.md)
- **Last Updated:** 2026-07-23

## Overview

JIM must serve two identity schema standards well: LDAP/Active Directory (the historic core of ILM deployments) and SCIM 2.0 (the dominant modern provisioning standard). This document records the decision on how the built-in Metaverse schema relates to those standards, the policy that governs future schema additions, and the near-term work it implies (SCIM-parity gap attributes and advisory standard-mapping metadata).

## Decision

**The Metaverse schema is JIM's own canonical vocabulary. No wire standard is its foundation.**

This formalises what the schema already is in practice: the built-in attributes carry friendly, standard-neutral names ("First Name", "Job Title", "Email", "Department") with a layer of AD/Exchange heritage ("Extension Attribute 1-15", "Proxy Addresses", "Mail Nickname", "objectGUID") and modern, SCIM-era additions ("Pronouns", "Identity Assurance Level (IAL)", "Subject Identifier").

Options considered and rejected:

1. **Formalise LDAP as the foundation.** Rejected: the schema is not actually LDAP-named today, and an LDAP baseline has no natural home for modern attributes (`active`, `timezone`, `preferredLanguage`, pronouns).
2. **Rebase on SCIM.** Rejected: a breaking change to every deployment's Synchronisation Rules, Predefined Searches, Example Data Templates, SSO claim mapping (`SSO.MvAttribute` references an attribute name), PowerShell scripts and documentation; and SCIM core is thin enough that most AD-heritage attributes would immediately return as custom additions. High cost, no administrator-visible benefit.
3. **Parallel per-standard schemas selected at deployment time, with provenance metadata.** Rejected as a schema model: two built-in vocabularies for the same semantic (a SCIM `emails` and an LDAP `Proxy Addresses` as peers) reintroduce the mapping problem inside the Metaverse, requiring cross-schema equivalence rules (a synonym ontology) and forking documentation, templates, tests and support by chosen schema. The valuable kernels of this option are retained below as *advisory* standard-mapping metadata, and (separately, future) attribute packs/templates.

## Policy

1. **Names are friendly and standard-neutral.** New built-in Metaverse Attributes must not adopt raw LDAP or SCIM attribute names. Existing AD-isms ("objectGUID", "objectSid", "sIDHistory") are grandfathered; they set no precedent.
2. **Each built-in attribute documents its standard mappings.** Every built-in attribute should declare how it maps to LDAP/AD and/or SCIM (see Standard-Mapping Metadata below). An attribute may map to both, one, or neither (JIM-native).
3. **When standards conflict, the neutral JIM name wins and both standards map in.** JIM does not privilege either standard's shape; connectors adapt at the Connected System boundary via Attribute Flows.
4. **Gaps are filled with neutral names as connectors need them.** When a standard exposes a semantic JIM cannot represent cleanly, add a neutral built-in attribute rather than bending an existing one (e.g. do not overload the AD bitmask "User Account Control" to represent SCIM's `active`).
5. **JIM never automatically copies values between Metaverse Attributes.** Where two attributes overlap in meaning (e.g. "Email" and "Emails"), what flows into each is an administrator decision expressed in Synchronisation Rules, not engine behaviour.
6. **Attribute Flow remains the only mapping mechanism.** Standard-mapping metadata is advisory (UI hints, wizard defaults, documentation); it must never be consulted by the synchronisation engine at run time.

## The Emails Case (worked example)

The decision that prompted this policy: SCIM `emails` is a multi-valued complex attribute; JIM's built-ins offer single-valued "Email" plus the Exchange-flavoured "Proxy Addresses" (which carries the `smtp:`/`SMTP:` prefix convention and would look alien fed from SCIM).

Resolution per the policy:

- **"Email"** (existing, single-valued): the primary email address. Unchanged.
- **"Emails"** (new, neutral, multi-valued Text): all of a person's addresses. Administrators who want the primary duplicated into it flow it there via Synchronisation Rules; JIM does not copy it automatically (Policy rule 5). This mirrors the decades-old LDAP `mail`/`proxyAddresses` pattern without inheriting the prefix convention.
- **"Proxy Addresses"** (existing): grandfathered for Exchange/AD scenarios.

## SCIM-Parity Gap Attributes

Initial list of neutral built-in attributes to add so SCIM core/enterprise resources map cleanly. To be finalised during implementation against the RFC 7643 attribute list:

| New attribute | Type | Plurality | SCIM counterpart | Notes |
|---|---|---|---|---|
| Emails | Text | Multi-valued | `emails` | See worked example above. |
| Account Enabled | Boolean | Single-valued | `active` | "User Account Control" is an AD bitmask; "Status" is free text. Neither is a clean boolean target. |
| Nickname | Text | Single-valued | `nickName` | Distinct from the Exchange-ism "Mail Nickname". |
| Preferred Language | Text | Single-valued | `preferredLanguage` | RFC 7231 language tag, e.g. `en-GB`. |
| Locale | Text | Single-valued | `locale` | RFC 5646 tag used for formatting. |
| Time Zone | Text | Single-valued | `timezone` | IANA name, e.g. `Europe/London`. |
| Middle Name | Text | Single-valued | `name.middleName` | |
| Honorific Prefix | Text | Single-valued | `name.honorificPrefix` | |
| Honorific Suffix | Text | Single-valued | `name.honorificSuffix` | |

Existing built-ins already covering SCIM semantics need no addition (e.g. `title` → "Job Title", `profileUrl` → "WebPage", `userType` → "Employee Type", `manager` → "Manager").

## Standard-Mapping Metadata (advisory)

Add a structured, advisory collection to `MetaverseAttribute`:

```
StandardMapping
├── Standard: Scim | Ldap | Jim
├── StandardAttributeName: string?   e.g. "name.givenName", "givenName"; null for Jim
└── Notes: string?                   e.g. caveats such as prefix conventions
```

An attribute may carry several mappings ("First Name" maps to both standards; "Proxy Addresses" is LDAP/AD-only; "Pronouns" is SCIM and JIM). Seeded for all built-in attributes; editable for custom attributes.

**What it powers** (all advisory):

- Filtering and hints in the Attribute Flow editor when defining inbound flows ("show SCIM-relevant targets").
- Default Attribute Flow suggestions in the SCIM connector setup wizards.
- Sensible default outbound mappings for the SCIM 2.0 Service Provider (JIM-to-JIM scenarios).
- Generated documentation of the built-in schema against both standards.

**What it must never do:** participate in run-time synchronisation decisions. Attribute Flow configuration remains the single source of mapping truth (Policy rule 6). This constraint is what keeps the rejected option 3's ontology problem out of the product.

## Implementation Phases

### Phase 1: Gap attributes

- Seed the SCIM-parity attributes above (finalised against RFC 7643) via `SeedingServer`, following the existing built-in attribute seeding and startup-reconciliation patterns.

### Phase 2: Standard-mapping metadata

- `StandardMapping` model, EF migration, seeding for all built-in attributes, reconciliation on startup.
- Admin UI read-only display on Metaverse Attribute pages; editable for custom attributes.

### Phase 3: Consumption

- Attribute Flow editor filter/hints.
- SCIM connector wizard default-flow suggestions (client connector first, server when built).

Phases 1 and 2 should land before or alongside the SCIM client connector's enablement phase (#545 Phase 7), so the connector ships with clean mapping targets and wizard hints.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Metadata drifts into run-time semantics over time | Policy rule 6 is explicit; code review gate: no sync-engine reference to `StandardMapping`. |
| Gap list grows into wholesale SCIM mirroring | Additions require a connector-driven need (Policy rule 4), not completeness for its own sake. |
| Overlapping attributes ("Email"/"Emails") confuse administrators | Standard-mapping notes and docs state the intended split; no automatic copying keeps behaviour predictable. |
