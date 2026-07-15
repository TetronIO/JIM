---
title: Synchronisation Rules
---

# Synchronisation Rules

A **Synchronisation Rule** defines the complete relationship between a [Connected System](connected-systems.md) and the metaverse. It controls which objects are in scope, how objects are matched, when new Metaverse Objects are created, and how attributes flow between systems.

Synchronisation Rules are the central configuration mechanism for identity synchronisation in JIM. Every Connected System needs at least one Synchronisation Rule to participate in synchronisation.

## What a Synchronisation Rule ties together

1. **Direction**<br /> Whether data flows inbound (a source system into the metaverse) or outbound (the metaverse out to a target system).
2. **Scoping criteria**<br /> Which objects the rule applies to.
3. **Object Matching Rules**<br /> How to match a Connected System Object to an existing Metaverse Object.
4. **Projection or Provisioning**<br /> What to do when no match is found.
5. **Attribute mappings**<br /> Which attributes to synchronise and how to transform them.

Each rule also has a name and an optional **description**, a free-text note for recording what the rule is for and why it exists. The description is shown on the rule's Details tab and changes to it are tracked in the [configuration change history](activities.md#configuration-change-history).

## Direction

Each rule has a direction that determines the flow of data.

### Import (inbound)

Import rules process data from a Connected System into the metaverse. They are used with **source systems**: systems that provide authoritative identity data (HR databases, badge systems, etc.).

An import rule:

- Reads CSOs from the Connected System's connector space
- Attempts to join each CSO to an existing MVO using the rule's object matching configuration
- Projects a new MVO if no match is found and projection is enabled
- Flows attribute values from the CSO to the MVO

### Export (outbound)

Export rules push data from the metaverse to a Connected System. They are used with **target systems**: systems that receive provisioned identity data (LDAP directories, email systems, etc.).

An export rule:

- Evaluates MVOs in the metaverse against the rule's scoping criteria
- Provisions a new CSO in the target system's connector space if one does not exist (and provisioning is enabled)
- Flows attribute values from the MVO to the CSO
- Creates Pending Exports for any changes

When **enforce state** is set on an export rule, JIM additionally detects and remediates attribute drift in the Connected System: if an exported attribute is changed externally, the next sync run pulls it back to the metaverse-derived value.

## Scoping criteria

Scoping criteria determine which objects the rule applies to. Only objects that match are processed.

For import rules, criteria evaluate CSO attributes:

```text
cs["objectClass"] = "user" AND cs["employeeType"] = "FTE"
```

For export rules, criteria evaluate MVO attributes:

```text
mv["Object Type"] = "Person" AND mv["Employee Status"] = "Active"
```

Objects that fall out of scope are **disconnected** from the rule. This is important for the [JML lifecycle](../concepts/jml-lifecycle.md): when an employee's status changes to "Leaver", they may fall out of scope for an export rule, triggering deprovisioning.

Criteria are organised into groups with AND/OR logic and support nested groups for complex conditions. Criteria expressions use the JIM [expression language](../concepts/expressions.md).

Each criterion is evaluated case-sensitively by default. Where a data source is inconsistent about casing (for example `Sales` versus `SALES`), you can switch an individual criterion to case-insensitive matching; see [Case Sensitivity](../concepts/case-sensitivity.md).

### Relative dates in scope filters

A criterion on a **date/time** attribute can compare against either a fixed date (**Absolute**) or a date worked out **Relative** to the moment the rule runs. Relative criteria are re-evaluated on every run, so a scope that says "terminated within the last year" keeps moving with time, with no need to edit the rule.

A relative criterion is a **count**, a **unit** (Hours, Days, Weeks, Months or Years) and a **direction** (Ago for the past, From now for the future). Date/time operators read in calendar wording: *before*, *on or before*, *after*, *on or after*, *equals*, *does not equal*.

- **Whole-day rounding**<br /> Days and coarser units resolve to midnight UTC, so "30 days ago" is a clean day boundary. The Hours unit keeps exact-instant precision for finer windows.
- **Calendar-correct**<br /> Month and year offsets respect the calendar (31 March minus one month is the last day of February).
- **Evaluated on demand**<br /> The boundary is resolved fresh each run from the host's UTC clock; nothing is stored as a fixed date.

For example, to scope an export rule to leavers terminated between 30 and 364 days ago, use an **All** group with two criteria on the termination-date attribute: *on or before* `30 days ago` and *after* `364 days ago`.

Configure this in the Scope tab of the Synchronisation Rule editor (choose Relative when the attribute is a date), or via the [PowerShell cmdlets](../powershell/synchronisation-rules.md) and the REST API.

## Object Matching Rules

Object Matching Rules define how a Connected System Object is matched to an existing Metaverse Object. Rules specify one or more attribute pairs to compare:

| CSO Attribute | MVO Attribute | Description |
|---------------|---------------|-------------|
| `employeeId` | `Employee ID` | Match on employee identifier |
| `mail` | `Email Address` | Match on email address |

JIM evaluates the matching rules in order and uses the first match found. You can configure multiple matching rules as a fallback strategy:

1. First, try to match on `employeeId` (most reliable)
2. If no match, try `mail` (secondary)
3. If no match, try `firstName` + `lastName` (least reliable)

Attribute comparisons in a matching rule are case-sensitive by default. Where systems disagree on casing, you can make an individual rule case-insensitive; see [Case Sensitivity](../concepts/case-sensitivity.md).

### Matching outcomes

| Outcome | Description |
|---------|-------------|
| **Joined** | Exactly one matching MVO found; the CSO is linked to it |
| **No match** | No matching MVO found; projection may create one |
| **Multiple matches** | More than one MVO matches; an error is raised (ambiguous join) |

### Simple vs advanced matching mode

Object matching can be configured at two levels:

- **Simple mode**<br /> Configured at the Connected System level; the matching rules are shared across all Synchronisation Rules for that system. Easier to manage when matching is uniform.
- **Advanced mode**<br /> Configured per Synchronisation Rule, so each rule can match independently. Use this when different Synchronisation Rules need different matching strategies against the same Connected System.

## Projection and provisioning

These determine what happens when no match is found.

**Projection** applies to import rules. If projection is enabled, JIM creates a new MVO of the specified object type and links the CSO to it. This is how new identities enter the metaverse for the first time. If projection is not enabled, the CSO remains disconnected.

**Provisioning** applies to export rules. If provisioning is enabled, JIM creates a new CSO in the target system's connector space (and ultimately the target system itself, when the export Run Profile flushes Pending Exports). If provisioning is not enabled, the rule only updates objects that already exist in the target.

## Deprovisioning Action

Provisioning's counterpart: each export rule's **Deprovisioning Action** determines what happens to the object in the Connected System when its Metaverse Object leaves the rule's scope or is deleted (for example, when a leaver's identity is removed by a [deletion rule](../concepts/jml-lifecycle.md#deletion-rules)):

- **Disconnect** (default): JIM breaks the join and leaves the object in place in the Connected System. Nothing is exported.
- **Delete**: JIM queues a delete so the object is removed from the Connected System on the next export run.

The action applies regardless of how the object came to be joined: it makes no difference whether JIM provisioned it or matched (joined) a pre-existing object. If several export rules cover the same object with different actions, Delete wins.

Configure the action in the export section of the Synchronisation Rule editor. To review the deprovisioning behaviour of every export rule for an object type in one place, use the **Downstream Deprovisioning** panel on the Metaverse Object Type page (Admin, Schema, then the object type), where the action can also be changed inline.

## Attribute mappings

Attribute mappings define which attributes to synchronise and how to transform them. Each mapping maps a source attribute (or expression) to a target attribute.

### Direct mappings

A direct mapping copies the attribute value as-is, with no transformation:

| Source | Target |
|--------|--------|
| `givenName` | `First Name` |
| `sn` | `Last Name` |

### Expression mappings

An expression mapping applies a transformation using the JIM [expression language](../concepts/expressions.md):

| Source | Target |
|--------|--------|
| `Lower(cs["givenName"]) + "." + Lower(cs["sn"]) + "@company.com"` | `Email Address` |
| `mv["First Name"] + " " + mv["Last Name"]` | `displayName` |
| `IIF(Eq(mv["Employee Status"], "Active"), 512, 514)` | `userAccountControl` |

### Multi-source mappings

A multi-source mapping combines several source attributes into one target. This is the concept-level pattern; in practice, you typically express multi-source flows through expression mappings that reference each contributing attribute.

### Multi-valued attributes

Mappings support both single-valued and multi-valued attributes. Multi-valued attributes hold a list of values (group memberships, email aliases, and so on). Mappings can flow multi-valued to multi-valued, or use functions like `Join()` and `Split()` to convert between multi-valued and single-valued representations.

### Value processing (inbound)

Source text is often dirty: stray padding, inconsistent casing, or a "value" that is really just spaces. For **import** mappings that target a **text** Metaverse attribute, you can clean and normalise the imported value before it flows to the Metaverse, configured per mapping in the Attribute Flow editor. Value processing applies to direct and expression mappings alike, and only to text attributes; it does not appear for export mappings or non-text targets.

Four controls are available:

- **Treat whitespace as no value**<br /> A whitespace-only or empty value is treated as no value: it does not flow, and clears any existing Metaverse value. This is **on by default**, so a stray space no longer masquerades as a real value. Switch it off where whitespace is genuinely meaningful.
- **Trim leading and trailing whitespace**<br /> Removes surrounding whitespace, so `··John··` becomes `John` (each `·` represents a space).
- **Collapse internal whitespace**<br /> Reduces runs of consecutive whitespace inside the value to a single space, so multiple spaces or tabs between words collapse to one. For example, `John···Smith` becomes `John Smith` (each `·` represents a space).
- **Case normalisation**<br /> Converts the value to `Upper`, `Lower`, or `Title` case, or leaves it unchanged (`None`). Useful for folding usernames or email addresses to a consistent case.

The transforms run in a fixed order: **trim, then collapse, then case normalisation, then the whitespace-as-no-value decision**. Because the whitespace decision runs last, a value that trims down to nothing is correctly treated as no value. Value processing is *normalisation*; it runs before [Attribute Priority](#attribute-priority) resolves which rule's value wins.

When **Treat whitespace as no value** is switched off and a whitespace-only value is therefore stored, the portal flags it with a `(whitespace)` indicator rather than rendering a misleading blank cell, so administrators can tell a real-but-invisible value apart from an absent one.

## Attribute Priority

When more than one import rule maps to the same Metaverse Object attribute, **Attribute Priority** decides which contributor wins, so the result never depends on the order your synchronisations happen to run in. It is an inbound concern: it governs how values flow from Connected Systems into the metaverse, and does not change how the metaverse is exported back out.

Priority is held **per attribute, per contributing rule**, not as a single level on the whole Synchronisation Rule. The same Connected System can therefore rank first for one attribute and second for another, and a single system can even contribute through several differently-scoped rules at different priorities.

### How a winner is chosen

For a given Metaverse attribute, JIM evaluates every contributing import rule in priority order (1 is highest):

- **The first contributor with a value wins.**<br /> Lower-priority contributors are not consulted.
- **A rule with no opinion is skipped.**<br /> If a rule does not apply to the object (it is disabled, no object from its Connected System is joined, or the joined object is out of the rule's scope), it is passed over and the next priority is considered.
- **If nobody contributes, the attribute is left unset.**

For example, an identity drawing data from two source systems:

- HR system provides `First Name` and `Last Name` (priority 1: authoritative)
- Badge system also provides `First Name` (priority 2: secondary)

The HR system's `First Name` wins because its contribution ranks higher; the badge system only fills in where HR has no opinion.

### Null is a value

By default, if the highest-priority source has **no** value for an attribute, JIM falls through to the next source. That is usually right, but not always: when the authoritative source deliberately *clears* a value, you want the clear to propagate, not to be back-filled from a stale secondary copy that still holds the old value.

Enabling **Null is a value** on a contributor changes that. If the contributor is connected and in scope but supplies no value, JIM stops there and asserts "no value": the attribute is cleared downstream, and lower-priority sources are not consulted. This is distinct from a rule simply having no opinion; a rule that does not apply to the object is still skipped regardless of this setting.

Typical uses are a manager or department cleared at the authoritative source that must propagate as a clear, and a primary-system migration where the new system is authoritative for the people it knows about (including their blanks). It is deliberately powerful: a misbehaving priority-1 import (an empty file, a truncated delta) becomes a mass-clearing event rather than a harmless no-op, so treat Null is a value as an authoritative, considered choice.

### Configuring priority

Attribute Priority is configured per (Metaverse Object Type, attribute), not on the Synchronisation Rule editor. Open the Metaverse Object Type (**Administration → Schema → Object Types**), select the **Attributes** tab, and expand the **contributors** list for any attribute that has more than one contributor. From there you drag contributors to reorder them and toggle **Null is a value** per contributor. A newly added import mapping joins at the **lowest** priority, so a new source never silently takes over an attribute until you promote it explicitly. The same configuration is available via the [PowerShell cmdlets](../powershell/metaverse.md) and the REST API.

> **Full detail:** [Attribute Priority](../concepts/attribute-priority.md) covers re-election when a winning source disconnects or withdraws, multi-valued attribute semantics, per-value provenance, and how to see resolution decisions in [Synchronisation Activities](activities.md).

## Common workflows

**Setting up an import rule:**

1. Create a Synchronisation Rule with direction `Import`, choosing the source object type in the Connected System and the target Metaverse Object Type
2. Add attribute mappings to flow values from CSO attributes onto the corresponding MVO attributes
3. Configure Object Matching Rules so incoming objects join to the right MVOs
4. Decide whether to project new MVOs when no match exists, and enable that on the rule

**Setting up an export rule with scoping:**

1. Create a Synchronisation Rule with direction `Export`, with provisioning enabled if you want JIM to create objects in the target system
2. Add attribute mappings to flow values from MVO attributes onto CSO attributes
3. Add scoping criteria so only the relevant MVOs are exported (for example, only `person` objects whose `department` is `IT`)
4. Configure Object Matching Rules for the export direction
5. Decide whether to enforce state, i.e. detect and correct drift in the target system

## Example: a complete import rule

A complete import rule for an HR system might look like:

| Component | Configuration |
|-----------|---------------|
| **Name** | HR Import - Employees |
| **Direction** | Import |
| **Connected System** | HR Database |
| **Scoping** | `Eq(cs["employeeType"], "FTE")` |
| **Object Matching Rule 1** | Match `cs["employeeId"]` to `mv["Employee ID"]` |
| **Projection** | Create MVO of type `Person` |
| **Attribute mappings** | `cs["givenName"]` to `mv["First Name"]` (direct) |
| | `cs["sn"]` to `mv["Last Name"]` (direct) |
| | `cs["department"]` to `mv["Department"]` (direct) |
| | `cs["employeeId"]` to `mv["Employee ID"]` (direct) |
| | `Capitalise(cs["givenName"]) + " " + Capitalise(cs["sn"])` to `mv["Display Name"]` (expression) |

This rule imports full-time employees from the HR system, joins them to existing Metaverse Objects by employee ID, creates new Metaverse Objects for new starters, and flows their attributes into the metaverse.

## Manage Synchronisation Rules

- **JIM portal**<br /> Synchronisation Rules area of the admin UI
- **PowerShell**<br /> [Synchronisation Rules cmdlets](../powershell/synchronisation-rules.md) (`Get-JIMSyncRule`, `New-JIMSyncRule`, etc.)
- **REST API**<br /> Synchronisation Rules endpoints in the [interactive API reference](../../api/reference/)

## See also

- [Connected Systems](connected-systems.md) -- the systems a Synchronisation Rule connects to
- [Concepts: Synchronisation Pipeline](../concepts/synchronisation-pipeline.md) -- where Synchronisation Rules fit in the import/sync/export flow
- [Concepts: Attribute Priority](../concepts/attribute-priority.md) -- how JIM resolves which source wins when several rules feed the same attribute, and the "Null is a value" setting
- [Concepts: JML Lifecycle](../concepts/jml-lifecycle.md) -- how scoping and provisioning drive joiner/mover/leaver behaviour
- [Concepts: Expressions](../concepts/expressions.md) -- the expression language used in scoping criteria and attribute mappings
- [Concepts: Case Sensitivity](../concepts/case-sensitivity.md) -- where matching and scoping are exact, and how to make them case-insensitive
