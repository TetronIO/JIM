---
title: Synchronisation Rules
---

# Synchronisation Rules

A **synchronisation rule** defines the complete relationship between a [connected system](connected-systems.md) and the metaverse. It controls which objects are in scope, how objects are matched, when new metaverse objects are created, and how attributes flow between systems.

Synchronisation rules are the central configuration mechanism for identity synchronisation in JIM. Every connected system needs at least one synchronisation rule to participate in synchronisation.

## What a synchronisation rule ties together

1. **Direction**<br /> Whether data flows inbound (a source system into the metaverse) or outbound (the metaverse out to a target system).
2. **Scoping criteria**<br /> Which objects the rule applies to.
3. **Object matching rules**<br /> How to match a Connected System Object to an existing Metaverse Object.
4. **Projection or provisioning**<br /> What to do when no match is found.
5. **Attribute mappings**<br /> Which attributes to synchronise and how to transform them.

## Direction

Each rule has a direction that determines the flow of data.

### Import (inbound)

Import rules process data from a connected system into the metaverse. They are used with **source systems**: systems that provide authoritative identity data (HR databases, badge systems, etc.).

An import rule:

- Reads CSOs from the connected system's connector space
- Attempts to join each CSO to an existing MVO using the rule's object matching configuration
- Projects a new MVO if no match is found and projection is enabled
- Flows attribute values from the CSO to the MVO

### Export (outbound)

Export rules push data from the metaverse to a connected system. They are used with **target systems**: systems that receive provisioned identity data (LDAP directories, email systems, etc.).

An export rule:

- Evaluates MVOs in the metaverse against the rule's scoping criteria
- Provisions a new CSO in the target system's connector space if one does not exist (and provisioning is enabled)
- Flows attribute values from the MVO to the CSO
- Creates pending exports for any changes

When **enforce state** is set on an export rule, JIM additionally detects and remediates attribute drift in the connected system: if an exported attribute is changed externally, the next sync run pulls it back to the metaverse-derived value.

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

## Object matching rules

Object matching rules define how a Connected System Object is matched to an existing Metaverse Object. Rules specify one or more attribute pairs to compare:

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

- **Simple mode**<br /> Configured at the connected system level; the matching rules are shared across all synchronisation rules for that system. Easier to manage when matching is uniform.
- **Advanced mode**<br /> Configured per synchronisation rule, so each rule can match independently. Use this when different synchronisation rules need different matching strategies against the same connected system.

## Projection and provisioning

These determine what happens when no match is found.

**Projection** applies to import rules. If projection is enabled, JIM creates a new MVO of the specified object type and links the CSO to it. This is how new identities enter the metaverse for the first time. If projection is not enabled, the CSO remains disconnected.

**Provisioning** applies to export rules. If provisioning is enabled, JIM creates a new CSO in the target system's connector space (and ultimately the target system itself, when the export run profile flushes pending exports). If provisioning is not enabled, the rule only updates objects that already exist in the target.

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

## Precedence

When multiple import rules write to the same MVO attribute, **precedence** determines which rule's value wins. Each synchronisation rule has a precedence level, and the rule with the highest precedence takes priority.

This matters when an identity has data flowing from multiple source systems. For example:

- HR system provides `First Name` and `Last Name` (high precedence: authoritative)
- Badge system also provides `First Name` (lower precedence: secondary)

The HR system's values take precedence because its synchronisation rule has a higher priority.

## Common workflows

**Setting up an import rule:**

1. Create a synchronisation rule with direction `Import`, choosing the source object type in the connected system and the target metaverse object type
2. Add attribute mappings to flow values from CSO attributes onto the corresponding MVO attributes
3. Configure object matching rules so incoming objects join to the right MVOs
4. Decide whether to project new MVOs when no match exists, and enable that on the rule

**Setting up an export rule with scoping:**

1. Create a synchronisation rule with direction `Export`, with provisioning enabled if you want JIM to create objects in the target system
2. Add attribute mappings to flow values from MVO attributes onto CSO attributes
3. Add scoping criteria so only the relevant MVOs are exported (for example, only `person` objects whose `department` is `IT`)
4. Configure object matching rules for the export direction
5. Decide whether to enforce state, i.e. detect and correct drift in the target system

## Example: a complete import rule

A complete import rule for an HR system might look like:

| Component | Configuration |
|-----------|---------------|
| **Name** | HR Import - Employees |
| **Direction** | Import |
| **Connected System** | HR Database |
| **Scoping** | `Eq(cs["employeeType"], "FTE")` |
| **Object matching rule 1** | Match `cs["employeeId"]` to `mv["Employee ID"]` |
| **Projection** | Create MVO of type `Person` |
| **Attribute mappings** | `cs["givenName"]` to `mv["First Name"]` (direct) |
| | `cs["sn"]` to `mv["Last Name"]` (direct) |
| | `cs["department"]` to `mv["Department"]` (direct) |
| | `cs["employeeId"]` to `mv["Employee ID"]` (direct) |
| | `Capitalise(cs["givenName"]) + " " + Capitalise(cs["sn"])` to `mv["Display Name"]` (expression) |

This rule imports full-time employees from the HR system, joins them to existing metaverse objects by employee ID, creates new metaverse objects for new starters, and flows their attributes into the metaverse.

## Manage Synchronisation Rules

- **JIM portal**<br /> Synchronisation Rules area of the admin UI
- **PowerShell**<br /> [Synchronisation Rules cmdlets](../powershell/synchronisation-rules.md) (`Get-JIMSyncRule`, `New-JIMSyncRule`, etc.)
- **REST API**<br /> Synchronisation Rules endpoints in the [interactive API reference](../../api/reference/)

## See also

- [Connected Systems](connected-systems.md) -- the systems a synchronisation rule connects to
- [Concepts: Synchronisation Pipeline](../concepts/synchronisation-pipeline.md) -- where synchronisation rules fit in the import/sync/export flow
- [Concepts: JML Lifecycle](../concepts/jml-lifecycle.md) -- how scoping and provisioning drive joiner/mover/leaver behaviour
- [Concepts: Expressions](../concepts/expressions.md) -- the expression language used in scoping criteria and attribute mappings
- [Concepts: Case Sensitivity](../concepts/case-sensitivity.md) -- where matching and scoping are exact, and how to make them case-insensitive
