# LDAP Connector Schema Discovery Review

- **Related Issue**: [#346 - Review the LDAP Connector schema discovery feature WRT objectClass attributes](https://github.com/TetronIO/JIM/issues/346)
- **Date**: 2026-02-24

## Status: ✅ RESOLVED

All recommendations implemented: defunct attribute filtering, constructed attribute detection, back-link detection, system-only detection, and writability indication (`AttributeWritability`) are all in place. The optional schema query optimisation (Section 5.5 — request only needed attributes) was not implemented but is non-critical.

## Executive Summary

The current implementation correctly walks the objectClass hierarchy and collects attributes from structural superclasses and auxiliary classes. The hierarchy-walking logic is **sound** and aligns with RFC 4512. However, the implementation currently returns **all** schema-declared attributes without filtering out those that are not practically useful or writable, which leads to:

1. **User confusion** — hundreds of attributes per object type, many of which cannot be written to or serve no identity management purpose
2. **Potential export errors** — if a user maps to a constructed or system-only attribute, the export will fail at runtime
3. **Reduced performance** — unnecessary schema queries for attributes that will never be used

This document analyses what the LDAP spec requires, what the current code does, identifies the gaps, and makes specific recommendations.

---

## 1. What the LDAP Specification Says

### RFC 4512 — Object Class Kinds (Sections 2.4.1-2.4.3)

Three kinds of object class:

| Kind | Description | Subclassing Rules |
|------|-------------|-------------------|
| **Abstract** | Base classes for inheritance only (e.g. `top`). Entries cannot belong to an abstract class alone. | Can be subclassed by abstract, structural, or auxiliary |
| **Structural** | Defines the primary identity of an entry. Every entry has exactly one structural class chain. | Can only subclass other structural classes (or abstract) |
| **Auxiliary** | Augments entries with additional attributes. Can be attached statically (in schema) or dynamically (per-object). | Can only subclass other auxiliary classes (or abstract) |

### Attribute Inheritance (RFC 4512 Section 2.4)

> "An object class inherits the sets of required and allowed attributes from its superclasses."

The complete set of attributes legally settable on an entry is the **union** of:

1. All `MUST` and `MAY` attributes from the structural class's entire superclass chain (up to `top`)
2. All `MUST` and `MAY` attributes from every auxiliary class attached to the entry (including the auxiliary classes' own superclass chains)
3. Any additional attributes allowed/precluded by DIT Content Rules (RFC 4512 Section 2.6)

### Attribute Types (RFC 4512 Section 2.5)

Attributes have a `USAGE` field:

| Usage | Meaning |
|-------|---------|
| `userApplications` (default) | Normal user-settable attribute |
| `directoryOperation` | Operational attribute, typically server-managed |
| `distributedOperation` | Shared across DSAs, server-managed |
| `dSAOperation` | Specific to one DSA, not replicated, server-managed |

Only `userApplications` attributes are generally writable by clients. Operational attributes are server-managed and not returned in searches unless explicitly requested.

### Key Takeaway

**Walking the full superclass chain and collecting attributes from structural + auxiliary classes is correct per the RFC.** The issue is not about collecting too many from the hierarchy, but about **not filtering out attributes that the directory server will reject writes to**.

---

## 2. Active Directory Extensions

AD extends the RFC model with additional schema metadata on `classSchema` and `attributeSchema` objects.

### On classSchema Objects

| AD Attribute | Purpose |
|---|---|
| `objectClassCategory` | 0 = 88-class (legacy structural), 1 = structural, 2 = abstract, 3 = auxiliary |
| `subClassOf` | Immediate parent class |
| `auxiliaryClass` | Statically linked auxiliary classes (admin-modifiable) |
| `systemAuxiliaryClass` | Statically linked auxiliary classes (system-protected, immutable) |
| `mustContain` / `systemMustContain` | Required attributes (admin-modifiable / system-protected) |
| `mayContain` / `systemMayContain` | Optional attributes (admin-modifiable / system-protected) |
| `defaultHidingValue` | If TRUE, objects of this class are hidden in AD tools |

### On attributeSchema Objects (Relevant to Filtering)

| AD Attribute | Purpose | Relevance |
|---|---|---|
| `systemFlags` | Bit flags controlling attribute behaviour | **Critical for filtering** |
| `systemOnly` | TRUE = server-managed, clients cannot write | **Critical for filtering** |
| `isDefunct` | TRUE = deprecated, should not be used | Should filter |
| `isSingleValued` | TRUE/FALSE — value plurality | Already used |
| `oMSyntax` | Data type indicator | Already used |
| `linkID` | If set, attribute is a linked attribute; odd = back-link | Should filter back-links |
| `searchFlags` | Controls indexing, confidentiality, etc. | Informational |

### systemFlags Bit Definitions for attributeSchema

| Flag | Hex | Meaning | Writable? |
|------|-----|---------|-----------|
| `FLAG_ATTR_NOT_REPLICATED` | 0x00000001 | Not replicated between DCs | May be writable |
| `FLAG_ATTR_REQ_PARTIAL_SET_MEMBER` | 0x00000002 | In Global Catalogue partial attribute set | Writable |
| `FLAG_ATTR_IS_CONSTRUCTED` | 0x00000004 | Computed on-the-fly by the DC | **NOT writable** |
| `FLAG_ATTR_IS_OPERATIONAL` | 0x00000008 | Server-managed operational attribute | **Typically NOT writable** |
| `FLAG_SCHEMA_BASE_OBJECT` | 0x00000010 | Part of base schema | Writable (flag is about schema modifiability) |

### Constructed Attributes (MS-ADTS)

An attribute is "constructed" if any of these are true:
- `systemFlags & 0x00000004` is set
- It is a rootDSE attribute
- It is a back-link attribute (linkID is an odd number)

Constructed attributes **cannot be written**, **cannot be used in search filters** (with few exceptions), and are **not returned by default** in search results.

Examples: `canonicalName`, `tokenGroups`, `allowedAttributes`, `allowedAttributesEffective`, `msDS-User-Account-Control-Computed`, `primaryGroupToken`, `sDRightsEffective`, `memberOf` (back-link of `member`).

### SAM Layer Overrides

The Security Account Manager (SAM) layer overrides schema semantics for security principal classes (user, group, computer, inetOrgPerson, samDomain, samServer). The current code already handles the `description` attribute plurality override — this is correct.

### Dynamic Auxiliary Classes

Starting with Windows Server 2003, auxiliary classes can be dynamically linked to individual objects by modifying the object's `objectClass` attribute. These are per-object, not per-class, and cannot be predicted at schema discovery time. The current approach of only including statically linked auxiliary classes is correct for schema discovery.

---

## 3. Current Implementation Analysis

### What the Code Does Correctly

1. **Filters to structural classes only** (`objectClassCategory=1`) — correct per RFC 4512
2. **Excludes hidden classes** (`defaultHidingValue=FALSE`) — sensible UX decision
3. **Walks the full superclass chain** via `subClassOf` — correct per RFC 4512
4. **Includes auxiliary class attributes** from both `auxiliaryClass` and `systemAuxiliaryClass` — correct per RFC 4512
5. **Recursively walks auxiliary class hierarchies** — correct, auxiliary classes can subclass other auxiliary classes
6. **Deduplicates attributes** across the hierarchy — correct, prevents duplicate entries
7. **Handles SAM layer plurality overrides** — correct for AD environments
8. **Correctly determines data types** via `oMSyntax` mapping — comprehensive coverage
9. **Records which class defined each attribute** (`ClassName` property) — useful for user understanding
10. **Sets recommended external ID attributes** (objectGUID + distinguishedName) — correct for AD

### What the Code Does NOT Do (Gaps)

| Gap | Impact | Severity |
|-----|--------|----------|
| Does not check `systemOnly` on attributeSchema entries | Returns attributes that cannot be written (e.g. `objectGUID`, `whenCreated`, `whenChanged`, `objectSid`, `instanceType`) | **High** |
| Does not check `systemFlags` for constructed flag (0x4) | Returns computed attributes that will fail on write (e.g. `canonicalName`, `tokenGroups`, `primaryGroupToken`) | **High** |
| Does not check `linkID` for back-links (odd values) | Returns back-link attributes that cannot be written directly (e.g. `memberOf`, `managedObjects`) | **High** |
| Does not check `isDefunct` | May return deprecated attributes on extended schemas | **Low** |
| Does not differentiate between read-only system attributes and writable user attributes in the UI | Users see a flat list with no indication of writability | **Medium** |
| Does not request specific attributes in the schema class search | Retrieves all attributes on classSchema entries, not just the ones needed | **Low** (performance) |
| Uses `SearchScope.OneLevel` for individual attribute lookups | Correct for flat AD schema partition; could be an issue for hierarchical schema partitions (unlikely in practice) | **Low** |

### Attribute Count Impact

A typical AD `user` object type inherits from: `user` -> `organizationalPerson` -> `person` -> `top`, plus auxiliary classes like `securityPrincipal`, `mailRecipient`, etc.

This produces approximately **300+ attributes** in the schema. Of those:

- ~40-60 are system-only (server manages them, clients cannot write)
- ~20-30 are constructed (computed on the fly)
- ~10-15 are back-links (must be written from the other side)
- **~200 are genuinely writable** by LDAP clients

For practical identity management, most deployments use only **15-40 attributes** on user objects. The gap between 300+ presented and 15-40 actually needed is the source of user confusion.

---

## 4. Best Practice: What Other Identity Management Tools Do

Identity management tools typically use one of these strategies:

### Strategy A: Full Schema, Filter Display
Collect the full schema but filter what's shown to the user. Mark attributes as "read-only", "system", or "writable" and allow the user to see the full list but focus on writable attributes by default.

### Strategy B: Curated Defaults + Full Schema Access
Present a curated list of commonly-used attributes by default, with an "advanced" or "show all" toggle to access the complete schema. This is the most user-friendly approach.

### Strategy C: Query allowedAttributesEffective
On AD, query a sample object using the `allowedAttributesEffective` constructed attribute. This returns only attributes the current user has write permission for on that specific object. Highly accurate but requires a sample object and varies by permissions.

### Recommended Approach for JIM

**Strategy A is the most practical and correct approach for JIM**, because:

- JIM needs schema discovery at Connected System configuration time, before objects exist in JIM
- JIM needs to know about all attributes for both import and export
- Some "read-only" attributes (like `objectSid`, `whenCreated`) are valuable for import even though they cannot be exported
- Filtering by writability metadata is reliable and doesn't require sample objects

---

## 5. Specific Recommendations

### 5.1 Add Writability Metadata to ConnectorSchemaAttribute (Recommended)

Add a property indicating whether the attribute is writable, read-only, or system-managed. This allows the UI to distinguish between attributes the user can map for export vs. those only useful for import.

**New property on `ConnectorSchemaAttribute`:**

```
Writability: Writable | ReadOnly | Constructed
```

**Detection logic during schema discovery:**

```
if systemOnly == TRUE           --> ReadOnly
if systemFlags & 0x4 (constructed) --> Constructed
if linkID is odd (back-link)     --> ReadOnly (back-links are constructed per MS-ADTS)
if isDefunct == TRUE            --> exclude entirely
otherwise                       --> Writable
```

### 5.2 Filter Out Non-Useful Attributes (Recommended)

Exclude from the schema results:

| What to Exclude | Why | Detection |
|---|---|---|
| Defunct attributes | No longer valid in the schema | `isDefunct = TRUE` |
| Constructed attributes (optionally) | Cannot be written to; rarely useful for import either | `systemFlags & 0x4` |

**Do NOT exclude** system-only attributes entirely — some are valuable for import (e.g. `whenCreated`, `objectSid`). Instead, mark them as read-only so the UI can indicate they are import-only.

### 5.3 Retrieve Filtering Attributes During Schema Queries (Recommended)

When looking up each attribute's schema entry in `GetSchemaAttribute()`, also request:

- `systemFlags` — to detect constructed attributes
- `systemOnly` — to detect system-managed attributes
- `linkID` — to detect back-links
- `isDefunct` — to detect deprecated attributes

These are all available on `attributeSchema` objects in the schema partition and can be retrieved in the same query that already fetches `oMSyntax`, `isSingleValued`, `description`, etc.

### 5.4 Back-Link Attribute Handling (Recommended)

Back-links (odd `linkID` values) like `memberOf` are particularly confusing for users because:

1. They appear as normal attributes in the schema
2. They cannot be written to directly
3. The user must modify the forward-link attribute on the related object instead (e.g. modify `member` on the group, not `memberOf` on the user)

Mark these as read-only and consider adding a note in the description indicating the forward-link relationship.

### 5.5 Performance Optimisation: Request Only Needed Attributes (Optional)

The current `GetSchemaEntry()` method performs a bare search without specifying which attributes to return — the server returns all attributes on each schema entry. For the class hierarchy walk, only `ldapdisplayname`, `subclassof`, `maycontain`, `mustcontain`, `systemmaycontain`, `systemmustcontain`, `auxiliaryclass`, and `systemauxiliaryclass` are needed. For attribute lookups, only `ldapdisplayname`, `description`, `admindescription`, `issinglevalued`, `omsyntax`, `systemflags`, `systemonly`, `linkid`, and `isdefunct` are needed.

Specifying required attributes in the `SearchRequest` would reduce network traffic and server processing time.

### 5.6 AD Alternative: allowedAttributes Query (Informational Only)

AD provides a constructed attribute `allowedAttributes` on every directory object. When requested, the server returns the pre-computed list of all attribute names the object can have. There is also `allowedAttributesEffective` which returns only attributes the bound user has permission to write.

This is **not recommended as a replacement** for schema walking because:

- Requires an existing object to query against
- `allowedAttributesEffective` is permission-dependent (varies by who binds)
- Does not provide attribute metadata (type, plurality, description)
- JIM needs schema at configuration time, before objects exist

However, it could be useful as a **validation cross-check** during development to verify the hierarchy walker is producing correct results.

### 5.7 Non-AD LDAP Directory Support (Informational)

The current implementation uses AD-specific schema attributes (`objectClassCategory`, `defaultHidingValue`, `systemMayContain`, `systemMustContain`, `systemAuxiliaryClass`, `oMSyntax`). These are not available on standard LDAP directories (OpenLDAP, 389DS, etc.).

For future non-AD LDAP support, the schema discovery would need to:

- Parse `objectClass` definitions from `cn=subschema` (RFC 4512 Section 4.2)
- Use `objectClasses` and `attributeTypes` operational attributes on the subschema entry
- Map `SYNTAX` OIDs (not `oMSyntax`) to data types
- Use `USAGE` field to determine operational vs user attributes
- Handle `SUP` (superclass) references in objectClass definitions

This is noted for future reference and is out of scope for issue #346.

---

## 6. Summary of Current State vs Desired State

| Aspect | Current State | Desired State |
|--------|--------------|---------------|
| Structural class filtering | Correct | No change needed |
| Hidden class filtering | Correct | No change needed |
| Hierarchy walking (superclass chain) | Correct | No change needed |
| Auxiliary class attribute inclusion | Correct (static only, as appropriate) | No change needed |
| Attribute deduplication | Correct | No change needed |
| SAM layer overrides | Correct | No change needed |
| Data type determination | Correct | No change needed |
| System-only attribute detection | **Not implemented** | Mark as read-only |
| Constructed attribute detection | **Not implemented** | Mark as constructed or exclude |
| Back-link detection | **Not implemented** | Mark as read-only |
| Defunct attribute filtering | **Not implemented** | Exclude entirely |
| Writability indication | **Not available** | Add to schema model |
| Schema query optimisation | Not optimised (retrieves all attributes) | Request only needed attributes |

---

## 7. References

### IETF RFCs

- **RFC 4512** — LDAP: Directory Information Models — [https://www.rfc-editor.org/rfc/rfc4512.html](https://www.rfc-editor.org/rfc/rfc4512.html)
  - Section 2.4: Object Classes (abstract, structural, auxiliary)
  - Section 2.4.1-2.4.3: Kind-specific rules and inheritance
  - Section 2.5: Attribute Types and USAGE
  - Section 2.6: DIT Content Rules
- **RFC 4519** — LDAP Schema for User Applications — [https://www.rfc-editor.org/rfc/rfc4519.html](https://www.rfc-editor.org/rfc/rfc4519.html)
- **RFC 4517** — LDAP Syntaxes and Matching Rules — [https://www.rfc-editor.org/rfc/rfc4517.html](https://www.rfc-editor.org/rfc/rfc4517.html)

### Microsoft Documentation

- **MS-ADTS: systemFlags** — [https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/1e38247d-8234-4273-9de3-bbf313548631](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/1e38247d-8234-4273-9de3-bbf313548631)
- **MS-ADTS: Constructed Attributes** — [https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/a3aff238-5f0e-4eec-8598-0a59c30ecd56](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/a3aff238-5f0e-4eec-8598-0a59c30ecd56)
- **MS-ADTS: Auxiliary Classes** — [https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/06f3acb8-8cff-49e9-94ad-6737fa0a9503](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/06f3acb8-8cff-49e9-94ad-6737fa0a9503)
- **AD Schema: Class Inheritance** — [https://learn.microsoft.com/en-us/windows/win32/ad/class-inheritance-in-the-active-directory-schema](https://learn.microsoft.com/en-us/windows/win32/ad/class-inheritance-in-the-active-directory-schema)
- **AD Schema: description attribute** — [https://learn.microsoft.com/en-us/windows/win32/adschema/a-description](https://learn.microsoft.com/en-us/windows/win32/adschema/a-description)
- **ADS_SYSTEMFLAG_ENUM** — [https://learn.microsoft.com/en-us/windows/win32/api/iads/ne-iads-ads_systemflag_enum](https://learn.microsoft.com/en-us/windows/win32/api/iads/ne-iads-ads_systemflag_enum)

### Community References

- **LDAP.com: Object Classes** — [https://ldap.com/object-classes/](https://ldap.com/object-classes/)
- **LDAP.com: DIT Content Rules** — [https://ldap.com/dit-content-rules/](https://ldap.com/dit-content-rules/)

---

## Appendix A: Defunct Attributes in Active Directory

### How Rare Are Defunct Objects?

Microsoft has only ever used `isDefunct` once across the entire history of AD schema updates (Sch1 through Sch91, Windows 2000 to Server 2025). It happened in Windows Server 2012 R2 when they redesigned Device Registration Service.

**Pre-2012 R2 forests have zero defunct objects.** Base schema objects (`FLAG_SCHEMA_BASE_OBJECT`) cannot be made defunct — AD rejects it with `unwillingToPerform`.

### Default Defunct Objects (Windows Server 2012 R2+)

**6 defunct attributes:**

| Attribute | OID | Defunct In |
|---|---|---|
| `ms-DS-User-Device-Registration-Link` | 1.2.840.113556.1.4.2244 | Sch59.ldf |
| `ms-DS-User-Device-Registration-Link-BL` | 1.2.840.113556.1.4.2245 | Sch59.ldf |
| `ms-DS-Authentication-Level` | 1.2.840.113556.1.4.2246 | Sch59.ldf |
| `ms-DS-Approximate-Last-Use-Time-Stamp` | 1.2.840.113556.1.4.2247 | Sch59.ldf |
| `ms-DS-Device-Reference` | 1.2.840.113556.1.4.2239 | Sch59.ldf |
| `ms-DS-Drs-Farm-ID` | (various) | Sch67.ldf |

**2 defunct classes:**

| Class | OID | Defunct In |
|---|---|---|
| `ms-DS-User-Device-Registration` | 1.2.840.113556.1.5.285 | Sch59.ldf |
| `ms-DS-User-Device-Registration-Container` | 1.2.840.113556.1.5.288 | Sch59.ldf |

### Why So Rare?

- Most administrators never touch the schema
- Applications (Exchange, Skype for Business, etc.) add hundreds of schema extensions but never clean them up when decommissioned
- An attribute must be removed from all class definitions before it can be defuncted
- The mechanism is reversible (`isDefunct = FALSE` reactivates) which reduces urgency

### Impact for JIM

While rare, filtering defunct attributes is cheap insurance. A filter on the classSchema query (`(!(isDefunct=TRUE))`) handles the class level, and checking `isDefunct` during attribute schema lookups handles the attribute level. The cost is negligible — one extra attribute to read per schema entry lookup.

### References

- **MS-ADTS: Defunct** — [https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/23f34386-ed78-4ce5-aff2-3f04be12c090](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/23f34386-ed78-4ce5-aff2-3f04be12c090)
- **AD Schema: isDefunct attribute** — [https://learn.microsoft.com/en-us/windows/win32/adschema/a-isdefunct](https://learn.microsoft.com/en-us/windows/win32/adschema/a-isdefunct)
- **Disabling Existing Classes and Attributes** — [https://learn.microsoft.com/en-us/windows/win32/ad/disabling-existing-classes-and-attributes](https://learn.microsoft.com/en-us/windows/win32/ad/disabling-existing-classes-and-attributes)
