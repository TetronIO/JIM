# Sync Rules

A **sync rule** defines the complete relationship between a connected system and the metaverse. It controls which objects are in scope, how objects are matched, when new metaverse objects are created, and how attributes flow between systems. Sync rules are the central configuration mechanism for identity synchronisation in JIM.

## Overview

Every connected system needs at least one sync rule to participate in synchronisation. A sync rule ties together five key components:

1. **Direction** -- whether data flows inbound (source to metaverse) or outbound (metaverse to target)
2. **Scoping filters** -- which objects the rule applies to
3. **Join rules** -- how to match a CSO to an existing MVO
4. **Projection rules** -- what to do when no match is found
5. **Attribute flows** -- which attributes to synchronise and how to transform them

## Direction

Each sync rule has a **direction** that determines the flow of data:

### Inbound (Source to Metaverse)

Inbound sync rules process data from a connected system into the metaverse. They are used with **source systems** -- systems that provide authoritative identity data (e.g., HR databases, badge systems).

An inbound sync rule:

- Reads CSOs from the connected system's connector space
- Attempts to join each CSO to an existing MVO
- Projects a new MVO if no match is found (and projection is enabled)
- Flows attribute values from the CSO to the MVO

### Outbound (Metaverse to Target)

Outbound sync rules push data from the metaverse to a connected system. They are used with **target systems** -- systems that receive provisioned identity data (e.g., Active Directory, email systems).

An outbound sync rule:

- Evaluates MVOs in the metaverse against the rule's scoping filter
- Provisions a new CSO in the target system's connector space if one does not exist
- Flows attribute values from the MVO to the CSO
- Creates pending exports for any changes

## Scoping Filters

Scoping filters determine **which objects** a sync rule applies to. Only objects that match the scoping filter are processed by the rule.

For inbound rules, scoping filters evaluate CSO attributes:

```text
cs["objectClass"] = "user" AND cs["employeeType"] = "FTE"
```

For outbound rules, scoping filters evaluate MVO attributes:

```text
mv["Object Type"] = "Person" AND mv["Employee Status"] = "Active"
```

Objects that fall out of scope are **disconnected** from the sync rule. This is important for the [JML lifecycle](jml-lifecycle.md) -- when an employee's status changes to "Leaver", they may fall out of scope for an outbound rule, triggering deprovisioning.

Scoping filters support the JIM [expression language](expressions.md), allowing complex conditions to be evaluated.

## Join Rules

Join rules define **how to match** a Connected System Object to an existing Metaverse Object. When a CSO is processed by an inbound sync rule, JIM evaluates the join rules to find the correct MVO.

### How Joining Works

Join rules specify one or more attribute pairs to compare:

| CSO Attribute | MVO Attribute | Description |
|---------------|---------------|-------------|
| `employeeId` | `Employee ID` | Match on employee identifier |
| `mail` | `Email Address` | Match on email address |

JIM evaluates the join rules in order. If a match is found, the CSO is **joined** to the MVO -- they are linked, and attribute flows apply.

### Multiple Join Rules

You can configure multiple join rules as a fallback strategy. For example:

1. First, try to match on `employeeId` (most reliable)
2. If no match, try to match on `mail` (secondary)
3. If no match, try to match on `firstName` + `lastName` (least reliable)

JIM evaluates the rules in order and uses the first match found.

### Join Outcomes

| Outcome | Description |
|---------|-------------|
| **Joined** | Exactly one matching MVO found -- CSO is linked to it |
| **No match** | No matching MVO found -- projection may create one |
| **Multiple matches** | More than one matching MVO found -- an error is raised (ambiguous join) |

## Projection Rules

Projection rules determine **what happens when no matching MVO is found** during join resolution.

If projection is enabled on the sync rule, JIM creates a new MVO of the specified object type and links the CSO to it. This is how new identities enter the metaverse for the first time.

For example, when a new employee appears in the HR system:

1. The HR import creates a new CSO
2. The inbound sync rule attempts to join the CSO to an existing MVO
3. No match is found (the employee is new)
4. The projection rule creates a new MVO of type "Person"
5. Attribute flows populate the new MVO with the employee's data

If projection is **not** enabled, the CSO remains disconnected -- it exists in the connector space but is not linked to the metaverse. This is useful for rules that should only update existing identities, not create new ones.

## Attribute Flows

Attribute flows define **which attributes to synchronise** and **how to transform them**. Each flow maps a source attribute (or expression) to a target attribute.

### Inbound Attribute Flows

Inbound flows map CSO attributes to MVO attributes:

| Source (CSO) | Target (MVO) | Type |
|--------------|--------------|------|
| `givenName` | `First Name` | Direct |
| `sn` | `Last Name` | Direct |
| `Lower(cs["givenName"]) + "." + Lower(cs["sn"]) + "@company.com"` | `Email Address` | Expression |

### Outbound Attribute Flows

Outbound flows map MVO attributes to CSO attributes:

| Source (MVO) | Target (CSO) | Type |
|--------------|--------------|------|
| `First Name` | `givenName` | Direct |
| `Last Name` | `sn` | Direct |
| `mv["First Name"] + " " + mv["Last Name"]` | `displayName` | Expression |
| `IIF(Eq(mv["Employee Status"], "Active"), 512, 514)` | `userAccountControl` | Expression |

### Flow Types

| Type | Description |
|------|-------------|
| **Direct** | Copy the attribute value as-is, with no transformation |
| **Expression** | Apply a transformation using the [expression language](expressions.md) |

### Multi-Valued Attributes

Attribute flows support both single-valued and multi-valued attributes. Multi-valued attributes hold a list of values (e.g., group memberships, email aliases). Flows can map multi-valued to multi-valued, or use functions like `Join()` and `Split()` to convert between multi-valued and single-valued representations.

## Precedence

When multiple inbound sync rules write to the same MVO attribute, **precedence** determines which rule's value wins. Each sync rule has a precedence level, and the rule with the highest precedence takes priority.

This is important when an identity has data flowing from multiple source systems. For example:

- HR system provides `First Name` and `Last Name` (high precedence -- authoritative)
- Badge system also provides `First Name` (lower precedence -- secondary)

The HR system's values take precedence because its sync rule has a higher priority.

## Example: Complete Sync Rule

Here is an example of how a complete inbound sync rule for an HR system might be configured:

| Component | Configuration |
|-----------|---------------|
| **Name** | HR Inbound - Employees |
| **Direction** | Inbound |
| **Connected System** | HR Database |
| **Scoping** | `Eq(cs["employeeType"], "FTE")` |
| **Join Rule 1** | Match `cs["employeeId"]` to `mv["Employee ID"]` |
| **Projection** | Create MVO of type "Person" |
| **Attribute Flows** | `cs["givenName"]` to `mv["First Name"]` (direct) |
| | `cs["sn"]` to `mv["Last Name"]` (direct) |
| | `cs["department"]` to `mv["Department"]` (direct) |
| | `cs["employeeId"]` to `mv["Employee ID"]` (direct) |
| | `Capitalise(cs["givenName"]) + " " + Capitalise(cs["sn"])` to `mv["Display Name"]` (expression) |

This rule imports full-time employees from the HR system, joins them to existing metaverse objects by employee ID, creates new metaverse objects for new starters, and flows their attributes into the metaverse.
