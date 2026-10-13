---
title: Scoping Criteria
description: "Decide which objects a Synchronisation Rule applies to, how criteria are evaluated, and how to preview a scope change."
type: object
audience: administrator
---

# Scoping Criteria

Scoping criteria determine which objects the rule applies to. Only objects that match are processed.

For import rules, criteria evaluate Connected System Object attributes:

```text
cs["objectClass"] = "user" AND cs["employeeType"] = "FTE"
```

For export rules, criteria evaluate Metaverse Object attributes:

```text
mv["Object Type"] = "Person" AND mv["Employee Status"] = "Active"
```

Objects that fall out of scope are **disconnected** from the rule. This is important for the [JML lifecycle](../concepts/jml-lifecycle.md): when an employee's status changes to "Leaver", they may fall out of scope for an export rule, triggering deprovisioning. Changing an export rule's criteria moves objects too, at the next synchronisation; see [When a change to an export rule takes effect](deprovisioning.md#when-a-change-to-an-export-rule-takes-effect).

An import rule's **Out-of-Scope Action** (**When objects fall out of scope** on the rule's Scope tab) can be **Remain joined (stop Attribute Flow)** instead of **Disconnect from Metaverse**. The Connected System Object then stays joined to its Metaverse Object, but nothing flows from it while it is out of scope, and the values it already contributed stay where they are. The run records this on the object's execution item as **Left scope, join kept**, naming the rule; see [Execution Items](activity-execution-items.md).

Criteria are organised into groups with AND/OR logic and support nested groups for complex conditions. Each criterion compares one attribute with a value you enter, or tests whether it holds a value at all; criteria do not use the [expression language](../concepts/expressions.md).

Each criterion is evaluated case-sensitively by default. Where a data source is inconsistent about casing (for example `Sales` versus `SALES`), you can switch an individual criterion to case-insensitive matching; see [Case Sensitivity](../concepts/case-sensitivity.md).

## Operators

The operators offered depend on the attribute's data type. They mean the same here as in a [Predefined Search](predefined-searches.md#operators-by-attribute-type), apart from the pattern operators, which a Predefined Search does not offer.

| Attribute type | Operators |
|----------------|-----------|
| Text | equals, does not equal, starts with, does not start with, ends with, does not end with, contains, does not contain, matches pattern, does not match pattern, has a value, has no value |
| Number, Long Number, Decimal | equals, does not equal, less than, less than or equal to, greater than, greater than or equal to, has a value, has no value |
| Date/Time | before, on or before, after, on or after, equals, does not equal, has a value, has no value |
| Boolean, GUID | equals, does not equal, has a value, has no value |

- **Has a value, has no value**<br /> Test whether the attribute holds anything at all, so they take no value. Use *has no value* rather than *equals* with an empty value, which JIM no longer accepts.
- **Matches pattern, does not match pattern**<br /> Take a .NET regular expression. A pattern matches anywhere in the value unless you anchor it with `^` and `$`, and follows the criterion's case setting. JIM checks the pattern when you save the rule and refuses one that is not a valid expression. Each match has a time limit of 100 milliseconds: a pattern that runs past it on some value fails that object's synchronisation with an error, rather than guessing whether it is in scope.

For example, to bring in only directory accounts that have an employee type and an email address in the organisation's domain, use an **All** group with `employeeType` *has a value* and `mail` *matches pattern* `^[a-z.]+@example\.org$`. An account with no employee type, or with an address that does not fit, is out of scope, and its Connections tab names the condition it fails.

## How criteria are evaluated

Three rules decide outcomes that are easy to misread from the editor:

- **Top-level groups are alternatives**<br /> An object is in scope when any one of the rule's top-level groups is met. Within a group, **All** requires every condition and child group to be met, and **Any** requires at least one. A group with nothing in it counts as met, and a rule with no criteria at all includes every object of its type.
- **A missing value fails the comparison**<br /> An attribute with no value fails every comparison, including negative ones such as *does not equal* and *does not match pattern*, except *has no value*. "Department does not equal Test" therefore excludes an object with no Department.
- **Every value of a multi-valued attribute is tested**<br /> A positive comparison is met by any one matching value, and a negated one only when no value matches what it negates; see [Multi-valued attributes in scope](#multi-valued-attributes-in-scope). The Connections tab shows the value that decided the outcome.

To see how one object evaluates against a rule, and which conditions it fails, open the object's Connections tab: see [Why it is connected, and why it is not](metaverse-objects.md#why-it-is-connected-and-why-it-is-not).

## Multi-valued attributes in scope

A criterion on a multi-valued attribute (group names, a directory's `objectClass`, email aliases) is tested against **every** value the object holds, so the order its values were imported in never affects scope:

- **Positive operators**<br /> *equals*, *starts with*, *ends with*, *contains*, *matches pattern*, and the ordering comparisons (*less than*, *before*, *greater than*, *after* and their *or equal to* forms) are met when **any** value matches.
- **Negated operators**<br /> *does not equal*, *does not start with*, *does not end with*, *does not contain* and *does not match pattern* are met when **no** value matches.
- **Presence**<br /> *has a value* is met by one value or more, and *has no value* only when the attribute holds none.

For example, an export rule scoped to `Groups` *contains* `Finance` includes a Metaverse Object whose groups are `All Staff` and `Finance Readers`, and `Groups` *does not contain* `Finance` excludes it. An import rule scoped to `objectClass` *equals* `user` includes a directory user whose classes are `top`, `person`, `organizationalPerson` and `user`.

Because each criterion looks for its own matching value, two criteria in an **All** group can be met by two different values. A date range built as *after* one date and *before* another includes an object with one date before the range and one after it, since each bound is satisfied by a different value. Scope ranges on a single-valued attribute when one value must fall inside them.

## Relative dates in scope filters

A criterion on a **date/time** attribute can compare against either a fixed date (**Absolute**) or a date worked out **Relative** to the moment the rule runs. Relative criteria are re-evaluated on every run, so a scope that says "terminated within the last year" keeps moving with time, with no need to edit the rule.

A relative criterion is a **count**, a **unit** (Hours, Days, Weeks, Months or Years) and a **direction** (Ago for the past, From now for the future). Date/time operators read in calendar wording: *before*, *on or before*, *after*, *on or after*, *equals*, *does not equal*.

- **Whole-day rounding**<br /> Days and coarser units resolve to midnight UTC, so "30 days ago" is a clean day boundary. The Hours unit keeps exact-instant precision for finer windows.
- **Calendar-correct**<br /> Month and year offsets respect the calendar (31 March minus one month is the last day of February).
- **Evaluated on demand**<br /> The boundary is resolved fresh each run from the host's UTC clock; nothing is stored as a fixed date.

For example, to scope an export rule to leavers terminated between 30 and 364 days ago, use an **All** group with two criteria on the termination-date attribute: *on or before* `30 days ago` and *after* `364 days ago`.

Configure this in the Scope tab of the Synchronisation Rule editor (choose Relative when the attribute is a date), or with [`New-JIMScopingCriterion`](../powershell/scoping-criteria.md#new-jimscopingcriterion) and the REST API.

## Previewing a scope change

Changing a Scoping Criterion decides which objects the rule manages at all, and what that costs is decided by a different setting sitting beside it: narrowing an import rule takes objects out of scope, and the **Out-of-Scope Action** then decides whether their Metaverse Object joins survive; narrowing an export rule can delete the objects that leave from the target system, per the **Deprovisioning Action**. Widening pulls objects in, projecting and provisioning identities nobody has counted.

The **Preview Scope Impact** button, which appears beside the editor's save button once you have edited the criteria, starts a [Configuration Change Preview](configuration-changes.md#previewing-a-change-before-you-make-it) of the criteria as they stand on the form, evaluated against the rule's saved criteria, changing nothing. It reports each object that would move, split by what the move actually costs it:

- **Leaving an import rule's scope**<br /> A joined object whose join would break, taking whatever it contributed out of the Metaverse with it; a joined object that would keep its join and stop receiving Attribute Flow; and an unjoined object that stops matching and loses nothing. Where a broken join would take a Metaverse Object's last connector, the preview follows the chain and reports which identities would become eligible for deletion.
- **Leaving an export rule's scope**<br /> A Metaverse Object with an object in the target system, which the **Deprovisioning Action** then removes from the target or disconnects and leaves in place; a Metaverse Object with no target object that a provisioning rule would have created a Connected System Object for and now will not; and a Metaverse Object with no target object under a rule that does not provision, which leaves scope with nothing to remove.
- **Entering scope**<br /> What each object would become, answered by running the same evaluation a synchronisation would: a new Metaverse Object projected, a join to an existing one, or an object provisioned into the target Connected System. An export rule that does not provision, or whose target object already exists, reports the object as entering export scope and creates nothing.

Two answers are deliberately negative rather than reassuring. Removing every criterion is called out as a warning, because it hands the rule every object of its type and is one click away from tidying up. And where another import Synchronisation Rule covers the same object type with **no** criteria of its own, that rule keeps every object in scope whatever this one says, so narrowing this rule disconnects nobody: the preview names that rule and counts no departures, rather than reporting a disconnection wave that would never happen.

Saving with a current preview on screen states its counts on the confirmation and records the preview against the change's [Activity](activities.md); edit the criteria afterwards and the preview is marked stale and contributes nothing. Automation gets the same evaluation through [`New-JIMConfigurationChangePreview -ScopingCriteriaGroup`](../powershell/previews.md) and the REST API's `POST sync-rules/{id}/scoping-criteria/preview` endpoint.
