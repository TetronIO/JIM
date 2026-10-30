---
title: Scoping Criteria
description: "Where scope is checked, what happens to objects in and out of scope, how criteria are evaluated, and how to test and preview a scope change."
type: object
audience: administrator
---

# Scoping Criteria

Scoping criteria decide which objects a Synchronisation Rule applies to. An object that matches is in scope and the rule processes it; one that does not is out of scope, and what follows depends on whether the rule imports into the Metaverse or exports from it.

## :material-sitemap-outline: Where scope is checked

A synchronisation does not look at every object. A Delta Synchronisation visits the Connected System Objects that changed since the last one, and every object of an Object Type awaiting synchronisation; a Full Synchronisation visits them all. Scope is checked at two points on the path from there, and an object a run never visits is not checked at all.

--8<-- "assets/diagrams/visits-and-scope.svg"

An object nothing has changed for has nothing new to evaluate, so a Delta Synchronisation leaves it alone. The exceptions are the events that change what an unchanged object should do; see [What re-checks an object](#what-re-checks-an-object).

## :material-swap-horizontal: Import scope and export scope

The two sides read different objects. For an import rule, criteria evaluate Connected System Object attributes:

```text
cs["objectClass"] = "user" AND cs["employeeType"] = "FTE"
```

For an export rule, criteria evaluate Metaverse Object attributes:

```text
mv["Object Type"] = "Person" AND mv["Employee Status"] = "Active"
```

```jim-diagram
archetype: comparison
title: Import scope and export scope compared
desc: 'An import rule judges a Connected System Object and an export rule judges a Metaverse Object. In scope, an import rule joins or projects the object and values flow in, and an export rule provisions an account and values flow out. Out of scope, an import rule applies its Out-of-Scope Action, which disconnects the object or keeps the join, and an export rule applies its Deprovisioning Action, which disconnects the account or deletes it. When a relative date is crossed, the object is flagged, for the next synchronisation of its Connected System on the import side and of any Connected System on the export side. An edited import scope takes effect at the next synchronisation of the rule''s Connected System, which processes every object of the type; an edited export scope takes effect at the next synchronisation of any Connected System, which reviews every Metaverse Object of the type.'
columns:
- {label: Import scope, sub: judged on the Connected System Object}
- {label: Export scope, sub: judged on the Metaverse Object}
rows:
- label: In scope
  cells:
  - {text: joins or projects, sub: then values flow in}
  - {text: provisions, sub: then values flow out}
- label: Out of scope
  cells:
  - {text: Out-of-Scope Action, sub: 'disconnects, or keeps the join'}
  - {text: Deprovisioning Action, sub: 'disconnects, or deletes the account'}
- label: A relative date is crossed
  cells:
  - {text: flagged, sub: next synchronisation of its Connected System}
  - {text: flagged, sub: next synchronisation of any Connected System}
- label: An edited scope takes effect
  cells:
  - {text: next synchronisation of its Connected System, sub: processes every object of the type}
  - {text: next synchronisation of any Connected System, sub: reviews every Metaverse Object of the type}
```

This is how the [JML lifecycle](../concepts/jml-lifecycle.md) acts on a status change: when an employee's status changes to "Leaver", they fall out of scope for an export rule, and [Deprovisioning](deprovisioning.md) covers what happens to their account.

## :material-arrow-decision-outline: What happens to each object a synchronisation visits

Each Connected System Object a synchronisation visits ends in one of these outcomes:

```jim-diagram
archetype: decision
title: What a synchronisation does with a Connected System Object it visits
desc: 'A Connected System Object a synchronisation visits is checked against the scope of the import rules for its Object Type. If it is in scope and already joined to a Metaverse Object, or an Object Matching Rule finds one, the join stands and values flow. If it is in scope and nothing matches, the rule projects a new Metaverse Object when projection is on, and otherwise the object stays unjoined. If it is out of scope and not joined, nothing happens. If it is out of scope and joined, the Out-of-Scope Action decides: Disconnect breaks the join and withdraws the values the object contributed, and Remain joined keeps the join and stops values flowing.'
start: {label: Connected System Object}
questions:
- {id: scope, label: 'In scope of an import rule?', sub: its Scoping Criteria}
- {id: join, label: 'Joined, or matches one?', sub: 'an existing join, or an Object Matching Rule'}
- {id: project, label: 'Projection on?'}
outcomes:
- {id: flows, label: 'Joined, values flow', sub: a match is joined first, good: true}
- {id: projected, label: Projected, sub: 'a new Metaverse Object, then values flow'}
- {id: unjoined, label: Left unjoined, sub: nothing flows}
- {id: disconnected, label: Disconnected, sub: 'values it contributed withdrawn'}
- {id: kept, label: Join kept, sub: nothing flows from it}
- {id: nothing, label: Nothing to do}
answers:
- {from: scope, to: join, label: 'yes'}
- {from: scope, to: nothing, label: 'no, not joined'}
- {from: scope, to: disconnected, label: 'no, joined: Disconnect'}
- {from: scope, to: kept, label: 'no, joined: Remain joined'}
- {from: join, to: flows, label: 'yes'}
- {from: join, to: project, label: 'no'}
- {from: project, to: projected, label: 'yes'}
- {from: project, to: unjoined, label: 'no'}
```

An import rule's **Out-of-Scope Action** (**When objects fall out of scope** on the rule's Scope tab) is **Disconnect from Metaverse** by default. With **Remain joined (stop Attribute Flow)** the Connected System Object stays joined to its Metaverse Object, but nothing flows from it while it is out of scope, and the values it already contributed stay where they are. The run records this on the object's execution item as **Left scope, join kept**, naming the rule; see [Execution Items](activity-execution-items.md). An export rule has no such choice: its [Deprovisioning Action](deprovisioning.md) decides what happens to the account.

## :material-update: What re-checks an object

A synchronisation checks only the objects it visits, so an object nothing has changed for waits for one of these events:

--8<-- "assets/diagrams/what-rechecks-an-object.svg"

- **A rule you saved**<br /> [When a change takes effect](configuration-changes.md#when-a-change-takes-effect) lists which change waits for which synchronisation.
- **A relative date crossed**<br /> [Temporal Scope Reconciliation](schedules.md#built-in-schedules) flags the objects a [relative date](#relative-dates-in-scope-filters) has moved across their boundary, on both sides, and the next synchronisation visits them.
- **An account deleted in the target**<br /> The next import obsoletes its Connected System Object, and the next synchronisation of that Connected System disconnects it and, where the Metaverse Object is still in scope of a provisioning rule, stages the account again.
- **A Metaverse Object written outside synchronisation**<br /> It is flagged for review; see [When a change to an export rule takes effect](deprovisioning.md#when-a-change-to-an-export-rule-takes-effect).

## :material-filter-outline: Building criteria

Criteria are organised into groups with AND/OR logic and support nested groups for complex conditions. Each criterion compares one attribute with a value you enter, or tests whether it holds a value at all. Where the attribute's value needs reshaping first, a criterion can compare an [expression](../concepts/expressions.md) instead; see [Comparing a computed value](#comparing-a-computed-value).

Each criterion is evaluated case-sensitively by default. Where a data source is inconsistent about casing (for example `Sales` versus `SALES`), you can switch an individual criterion to case-insensitive matching; see [Case Sensitivity](../concepts/case-sensitivity.md).

## :material-calculator-variant-outline: Operators

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

## :material-arrow-decision-outline: How criteria are evaluated

```jim-diagram
archetype: decision
title: How Scoping Criteria decide an object's scope
desc: 'An object is in scope when any one of the rule''s top-level groups is met, or when the rule has no criteria; within a group, All needs every condition and Any at least one. An object a criterion cannot decide is left as it is, with an error recorded against it. Every other object is out of scope, and what follows depends on whether the rule imports or exports.'
start: {label: Object against a rule's criteria}
questions:
- {id: groups, label: 'Any top-level group met?', sub: 'All: every condition; Any: one'}
outcomes:
- {id: in, label: In scope, sub: processed by the rule, good: true}
- {id: out, label: Out of scope, sub: what follows depends on the side}
- {id: undecided, label: Left as it is, sub: an error recorded against it}
answers:
- {from: groups, to: in, label: 'yes, or no criteria'}
- {from: groups, to: out, label: 'no'}
- {from: groups, to: undecided, label: cannot be decided}
```

Four rules decide outcomes that are easy to misread from the editor:

- **Top-level groups are alternatives**<br /> An object is in scope when any one of the rule's top-level groups is met. Within a group, **All** requires every condition and child group to be met, and **Any** requires at least one. A group with nothing in it counts as met, and a rule with no criteria at all includes every object of its type.
- **A missing value fails the comparison**<br /> An attribute with no value fails every comparison, including negative ones such as *does not equal* and *does not match pattern*, except *has no value*. "Department does not equal Test" therefore excludes an object with no Department.
- **Every value of a multi-valued attribute is tested**<br /> A positive comparison is met by any one matching value, and a negated one only when no value matches what it negates; see [Multi-valued attributes in scope](#multi-valued-attributes-in-scope). The Connections tab shows the value that decided the outcome.
- **A value held under an earlier data type is not a missing value**<br /> After a schema refresh changes an attribute's data type, a Connected System Object keeps its value under the old type until a Full Import reads it again; see [Moving an attribute to its new type](connected-system-schema.md#moving-an-attribute-to-its-new-type). An import rule's criterion that reaches such a value, directly or through an expression, cannot decide whether the object is in scope. Synchronisation records an error against that object and leaves it, and its connections, as they are, rather than bringing it into scope or taking it out on a guess, and the Connections tab names the value and its old type. Run the Full Import, and the next synchronisation decides the object's scope. An object that also holds a value of the attribute's current type is scoped on that value.

To see how one object evaluates against a rule, and which conditions it fails, open the object's Connections tab: see [Why it is connected, and why it is not](metaverse-objects.md#why-it-is-connected-and-why-it-is-not).

## :material-format-list-bulleted: Multi-valued attributes in scope

A criterion on a multi-valued attribute (group names, a directory's `objectClass`, email aliases) is tested against **every** value the object holds, so the order its values were imported in never affects scope:

- **Positive operators**<br /> *equals*, *starts with*, *ends with*, *contains*, *matches pattern*, and the ordering comparisons (*less than*, *before*, *greater than*, *after* and their *or equal to* forms) are met when **any** value matches.
- **Negated operators**<br /> *does not equal*, *does not start with*, *does not end with*, *does not contain* and *does not match pattern* are met when **no** value matches.
- **Presence**<br /> *has a value* is met by one value or more, and *has no value* only when the attribute holds none.

For example, an export rule scoped to `Groups` *contains* `Finance` includes a Metaverse Object whose groups are `All Staff` and `Finance Readers`, and `Groups` *does not contain* `Finance` excludes it. An import rule scoped to `objectClass` *equals* `user` includes a directory user whose classes are `top`, `person`, `organizationalPerson` and `user`.

Because each criterion looks for its own matching value, two criteria in an **All** group can be met by two different values. A date range built as *after* one date and *before* another includes an object with one date before the range and one after it, since each bound is satisfied by a different value. Scope ranges on a single-valued attribute when one value must fall inside them.

## :material-calculator-variant-outline: Comparing a computed value

A criterion can compare the value an [expression](../concepts/expressions.md) computes from the object, rather than an attribute as it is stored. Use it when the value needs reshaping before it can be compared: an inconsistently cased or padded department, a date held as text, a code inside a longer string. In the Scope tab, choose **Add Criteria**, then **Expression** beside **Compare**, write the expression (**Insert attribute** and **Insert function** help), and choose **Compare as**. **Add Criteria** stays unavailable until the criterion is one the rule can be saved with, and says what is wrong while it is not. Automation creates one with [`New-JIMScopingCriterion`](../powershell/scoping-criteria.md#new-jimscopingcriterion) `-Expression` and `-CompareAs`, or through the REST API by giving `expression` and `compareAs` in place of an attribute id.

- **It reads the object the rule scopes**<br /> An import rule's expression reads the Connected System Object with `cs["..."]`, and an export rule's reads the Metaverse Object with `mv["..."]`. It must read at least one attribute of that object.
- **You choose the type it is compared as**<br /> **Compare as** (Text, Number, Long Number, Decimal, Date/Time, Boolean or GUID) decides which operators are offered and what you compare against, exactly as an attribute's own type does. It is chosen rather than worked out, because an expression can compute no value for the object you happen to try it on.
- **Every value counts**<br /> An expression that computes several values, such as one reading a multi-valued attribute, is tested value by value, as a multi-valued attribute is. One that computes nothing, or only blank text, holds no value.
- **Explanations show what it read**<br /> The Connections tab and **Test this scope** show the expression, each attribute it read with the values the object held, and the value it computed. For an object it cannot decide, they say why. Values of an attribute that may hold a credential are withheld, and so is anything computed from one.
- **An object it cannot decide is left alone**<br /> When the expression fails for an object, computes a value that cannot be read as the **Compare as** type (`Finance` compared as a Number), or reads an attribute the object type no longer has because it was renamed or removed after the rule was saved, synchronisation records an error against that object and leaves it, and its connections, as they are. It never guesses which side of the scope the object is on. The rest of the run continues.

For example, to bring in people whose department starts with *Finance* however the source system cases or pads it, compare `Lower(Trim(cs["department"]))` as Text with *starts with* `finance`.

JIM refuses an expression criterion when you save the rule if it could never work: one that does not parse, reads the other side (`mv["..."]` on an import rule), reads no attribute, names an attribute the object type does not have, or calls `Now()` or another function that returns a different value each time it runs. To scope by how long ago something happened, compare the date as a Date/Time against a [relative date](#relative-dates-in-scope-filters) instead. A criterion that stops being valid after it was saved, because an attribute it reads was renamed, removed or changed type, is not refused when the rule is next saved: the save goes ahead and warns about it, so the rule can always be disabled or corrected. Changing that criterion without fixing it is refused.

## :material-calendar-clock-outline: Relative dates in scope filters

A criterion on a **date/time** attribute can compare against either a fixed date (**Absolute**) or a date worked out **Relative** to the moment the rule runs. Relative criteria are re-evaluated whenever an object is visited, and [Temporal Scope Reconciliation](schedules.md#built-in-schedules) flags the objects whose boundary has been crossed so the next synchronisation visits them. A scope that says "terminated within the last year" therefore keeps moving with time, with no need to edit the rule.

A relative criterion is a **count**, a **unit** (Hours, Days, Weeks, Months or Years) and a **direction** (Ago for the past, From now for the future). Date/time operators read in calendar wording: *before*, *on or before*, *after*, *on or after*, *equals*, *does not equal*.

- **Whole-day rounding**<br /> Days and coarser units resolve to midnight UTC, so "30 days ago" is a clean day boundary. The Hours unit keeps exact-instant precision for finer windows.
- **Calendar-correct**<br /> Month and year offsets respect the calendar (31 March minus one month is the last day of February).
- **Evaluated on demand**<br /> The boundary is resolved fresh each run from the host's UTC clock; nothing is stored as a fixed date.

A relative date can also be the comparison for an [expression compared as a Date/Time](#comparing-a-computed-value), such as a start date held as text. That has a cost worth knowing on a large object type. [Temporal Scope Reconciliation](schedules.md#built-in-schedules) normally reads only the objects whose stored date the clock has just crossed, but an expression's date is computed, not stored, so for such a rule every reconciliation evaluates every object of its type, in batches. JIM says so when you save the criterion, and the reconciliation's Activity reports how many objects it evaluated. Where the type is large, prefer flowing the date into an attribute and scoping on that.

For example, to scope an export rule to leavers terminated between 30 and 364 days ago, use an **All** group with two criteria on the termination-date attribute: *on or before* `30 days ago` and *after* `364 days ago`.

Configure this in the Scope tab of the Synchronisation Rule editor (choose Relative when the attribute is a date), or with [`New-JIMScopingCriterion`](../powershell/scoping-criteria.md#new-jimscopingcriterion) and the REST API.

## :material-flask-outline: Testing a scope

The **Test this scope** panel at the foot of the Scope tab answers whether one object would be in scope, before anything runs. It evaluates the criteria as they stand on the form, saved or not, with the same evaluation a synchronisation uses, so a test cannot pass where a synchronisation would not.

- **Values**<br /> Each attribute the criteria compare gets an editor for its type: a date for a date/time attribute, True or False for a Boolean, several values for a multi-valued attribute. An attribute left untouched, or with **No value** ticked, holds no value. **From an object** loads a real object's values instead (a Connected System Object for an import rule, a Metaverse Object for an export rule), which you can then edit to try a variation.
- **Run as of**<br /> Relative dates resolve against the date and time you choose (UTC), or the current time when you leave it empty. Use it to check a leaver rule on the day it should let go.
- **Result**<br /> Whether the object is in scope, each group and criterion with what the object held, and when the outcome next changes as time passes, the values staying the same. For example, a leave date of 1 November under *after* `30 days ago` reports that the object leaves scope at midnight UTC on 1 December.

To keep a test and run it again after later changes, choose **Save as test case**; see [Configuration Tests](configuration-tests.md).

Automation gets the same test through [`Test-JIMScope`](../powershell/scoping-criteria.md#test-jimscope) and the REST API's `POST sync-rules/{id}/scoping-criteria/test` endpoint, which test the saved criteria.

## :material-eye-outline: Previewing a scope change

Changing a Scoping Criterion decides which objects the rule manages at all, and what that costs is decided by a different setting sitting beside it: narrowing an import rule takes objects out of scope, and the **Out-of-Scope Action** then decides whether their Metaverse Object joins survive; narrowing an export rule can delete the objects that leave from the target system, per the **Deprovisioning Action**. Widening pulls objects in, projecting and provisioning identities nobody has counted.

The **Preview Scope Impact** button, which appears beside the editor's save button once you have edited the criteria, starts a [Configuration Change Preview](configuration-changes.md#previewing-a-change-before-you-make-it) of the criteria as they stand on the form, evaluated against the rule's saved criteria, changing nothing. It reports each object that would move, split by what the move actually costs it:

- **Leaving an import rule's scope**<br /> A joined object whose join would break, taking whatever it contributed out of the Metaverse with it; a joined object that would keep its join and stop receiving Attribute Flow; and an unjoined object that stops matching and loses nothing. Where a broken join would take a Metaverse Object's last connector, the preview follows the chain and reports which identities would become eligible for deletion.
- **Leaving an export rule's scope**<br /> A Metaverse Object with an object in the target system, which the **Deprovisioning Action** then removes from the target or disconnects and leaves in place; a Metaverse Object with no target object that a provisioning rule would have created a Connected System Object for and now will not; and a Metaverse Object with no target object under a rule that does not provision, which leaves scope with nothing to remove.
- **Entering scope**<br /> What each object would become, answered by running the same evaluation a synchronisation would: a new Metaverse Object projected, a join to an existing one, or an object provisioned into the target Connected System. An export rule that does not provision, or whose target object already exists, reports the object as entering export scope and creates nothing.

Two answers are deliberately negative rather than reassuring. Removing every criterion is called out as a warning, because it hands the rule every object of its type and is one click away from tidying up. And where another import Synchronisation Rule covers the same object type with **no** criteria of its own, that rule keeps every object in scope whatever this one says, so narrowing this rule disconnects nobody: the preview names that rule and counts no departures, rather than reporting a disconnection wave that would never happen.

Saving with a current preview on screen states its counts on the confirmation and records the preview against the change's [Activity](activities.md); edit the criteria afterwards and the preview is marked stale and contributes nothing. Automation gets the same evaluation through [`New-JIMConfigurationChangePreview -ScopingCriteriaGroup`](../powershell/previews.md) and the REST API's `POST sync-rules/{id}/scoping-criteria/preview` endpoint.
