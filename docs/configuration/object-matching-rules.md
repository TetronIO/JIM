---
title: Object Matching Rules
description: "How a Connected System Object is matched to an existing Metaverse Object, the outcomes, and previewing a matching change."
type: object
audience: administrator
---

# Object Matching Rules

Object Matching Rules define how a Connected System Object is matched to an existing Metaverse Object. Each rule compares one source, a Connected System attribute or an expression over the Connected System Object, with one Metaverse attribute:

```jim-diagram
archetype: decision
title: How an Object Matching Rule decides a join
desc: "JIM tries the Object Matching Rules in order. If a rule's value matches exactly one Metaverse Object, the objects are Joined and the rule and value are recorded. If it matches several, the result is an Ambiguous Match and nothing joins or projects. If the rule cannot be worked out, the result is Object Matching Rule Failed and no later rule is tried. If nothing matches, or the rule has no value, JIM tries the next rule; when no rules are left, the result is No match, and projection or provisioning may create an object."
start: {label: Connected System Object}
questions:
- {id: rule, label: Next Object Matching Rule, sub: 'how many Metaverse Objects match?'}
- {id: more, label: 'More rules?'}
outcomes:
- {id: joined, label: Joined, sub: the rule and value recorded, good: true}
- {id: ambiguous, label: Ambiguous Match, sub: nothing joins or projects}
- {id: failed, label: Object Matching Rule Failed, sub: no later rule is tried}
- {id: none, label: No match, sub: projection or provisioning may create one}
answers:
- {from: rule, to: joined, label: exactly one}
- {from: rule, to: ambiguous, label: several}
- {from: rule, to: failed, label: cannot be worked out}
- {from: rule, to: more, label: 'none, or no value'}
- {from: more, to: rule, label: 'yes: try it'}
- {from: more, to: none, label: 'no'}
```

| Connected System attribute or expression | Metaverse Object Attribute | Description |
|---------------|---------------|-------------|
| `employeeId` | `Employee ID` | Match on employee identifier |
| `mail` | `Email Address` | Match on email address |
| `"E" + Trim(cs["employeeId"])` | `Employee ID` | Match on an identifier this system holds without its prefix |

JIM tries the matching rules in order; a rule with no value, or no match, passes to the next. Configure several as a fallback strategy:

1. First, try to match on `employeeId` (most reliable)
2. If no match, try `mail` (secondary)
3. If no match, try an expression joining first and last name, matched against a Metaverse attribute that holds the same joined value; see [Deriving Metaverse Attributes](derived-attributes.md) (least reliable)

A matching rule ignores case by default, so `JBloggs` matches `jbloggs`. Turn on **Case sensitive** only where a system treats them as different objects; see [Case Sensitivity](../concepts/case-sensitivity.md#object-matching-rules).

## :material-arrow-decision-outline: Matching outcomes

| Outcome | Description |
|---------|-------------|
| **Joined** | Exactly one Metaverse Object (or, before provisioning, one Connected System Object) matches; the connection records the rule and value |
| **No match** | No rule matches; projection or provisioning creates one, if the Synchronisation Rule allows it |
| **Ambiguous Match** | The rule's values, every value of a multi-valued attribute counting, match several Metaverse Objects; the Activity item lists them, and nothing joins or projects |
| **Ambiguous Provisioning Match** | Before provisioning, the rule's values match several existing Connected System Objects; the Activity item lists them, and nothing joins or provisions for that export rule. See [Ambiguous Provisioning Match](../administration/troubleshooting-synchronisation.md) |
| **Object Matching Rule Failed** | The rule cannot be worked out; nothing joins, projects or provisions, and no later rule is tried. This includes a rule reading an attribute the object holds only under another data type, because a [schema refresh changed its type](connected-system-schema.md#moving-an-attribute-to-its-new-type) and no import has read it since. Before provisioning, it also includes a lookup that finds nothing while an unjoined Connected System Object of the type holds the attribute that way, since that object may be the identity's own account. A Full Import of the system holding the value resolves it |

A join to a Metaverse Object also records the rule and value on the joining object's [Activity item](activity-execution-items.md), and a [Sync Preview](sync-preview.md) names them for a join it predicts.

## :material-function-variant: Matching on an expression

Two systems often hold the same identifier in different formats: a directory stores `1007` where HR has `E1007`, pads
it to `001007`, keeps stray spaces, or joins two fields into one key. Matching those attribute to attribute finds
nothing, and each mismatch becomes a duplicate identity or a duplicate account. An expression rule computes the value
the Metaverse holds instead.

In **Add Matching Rule**, choose **Match on: Expression**, write the expression (or use **Insert attribute** to add
an attribute), and pick the Metaverse attribute it equals:

| This system holds | Metaverse holds | Expression |
|---|---|---|
| `1007` | `E1007` | `"E" + Trim(cs["employeeId"])` |
| `1007` | `001007` | `Right("000000" + Trim(cs["employeeId"]), 6)` |
| `JBLOGGS@CORP.LOCAL ` | `jbloggs@corp.local` | `Lower(Trim(cs["mail"]))` |
| `Sales` and `1007` | `Sales-1007` | `Trim(cs["department"]) + "-" + Trim(cs["employeeId"])` |

How the expression is read:

- **It reads the Connected System Object only**, as `cs["attributeName"]`. The Metaverse Object is what the rule is
  looking for, so there is nothing on that side to read yet. To normalise the Metaverse side too,
  [derive a Metaverse attribute](derived-attributes.md) that holds the normalised value, and match on that.
- **Its result is converted to the Metaverse attribute's type**, as an Attribute Flow's would be. A result that will
  not convert (text where a number is expected) stops the object with an **Object Matching Rule Failed** error rather
  than letting it match nothing. Correct the expression or the source value, then synchronise again.
- **A list of values is matched value by value**, as every value of a multi-valued attribute is.
- **No value** (missing, empty or only whitespace) passes the object to the next rule.

A rule compares for equality, and only equality. "Starts with" or "contains" could match many identities, so JIM does
not offer them. Put the normalising in the expression instead, so the value it computes is exactly the value the
Metaverse holds.

JIM refuses to save an expression that does not parse, reads `mv["..."]`, reads no `cs["..."]` attribute, reads an
attribute the Object Type does not have, or calls a function that returns a different value each time it runs
(`Now()`, `Today()`, `RandomPassword()`, `RandomPassphrase()`, `Guid.NewGuid()`): each would match a different
identity, or none, on every run. The editor shows the same reason before you save.

When JIM checks, before provisioning, whether a matching account already exists, it applies the expression to each
unjoined Connected System Object of that type. If it cannot do so for some of them, a Metaverse Object that finds no
match stops with an **Object Matching Rule Failed** error naming one of those objects, rather than provisioning an
account that may already exist. Correct that object's value, or the expression, then synchronise again.

## :material-flask-outline: Testing a rule

**Try it**, under the rule editor, shows what the rule would do for one object before you save it, worked out with the same code a synchronisation runs.

- **Values**<br /> Each attribute the rule reads gets an editor for its type, with several values for a multi-valued attribute. An attribute left untouched, or with **No value** ticked, holds no value. **From an object** loads a real Connected System Object's values instead, which you can then edit to try a variation.
- **Match values**<br /> The values the rule would compare, in the Metaverse attribute's type. Or that it has no value, so the next rule would be tried, or the **Object Matching Rule Failed** error a synchronisation would stop the object with.
- **In the Metaverse**<br /> With **Also find the Metaverse Object a synchronisation would join** ticked, the values are looked up as an import join looks them up, and nothing is written. The answer is one of these:
    - **Would join** a named Metaverse Object;
    - **Already joined**, when the loaded object is already joined to it;
    - **Joined to another**, when another object from this Connected System holds the join, so a synchronisation would record that this one could not join;
    - **No match**;
    - **Ambiguous**, listing the Metaverse Objects the values identify.

To keep a test of a saved rule and run it again after later changes, create a [configuration test case](configuration-tests.md) for it: on the Synchronisation Rule's **Tests** tab in Advanced mode, or below the rules on the Connected System's **Matching** tab in Simple mode.

Automation gets the same test of a saved rule through [`Test-JIMMatchingRule`](../powershell/object-matching-rules.md#test-jimmatchingrule) and the REST API's `POST /api/v1/synchronisation/connected-systems/{connectedSystemId}/matching-rules/{ruleId}/test` and `POST /api/v1/synchronisation/sync-rules/{syncRuleId}/matching-rules/{ruleId}/test` endpoints.

## :material-tune-variant: Simple vs advanced matching mode

Object matching can be configured at two levels:

- **Simple mode**<br /> Configured at the Connected System level; the matching rules are shared across all Synchronisation Rules for that system. Easier to manage when matching is uniform.
- **Advanced mode**<br /> Configured per Synchronisation Rule, so each rule can match independently. Use this when different Synchronisation Rules need different matching strategies against the same Connected System.

A Simple mode rule also names the **Metaverse Object Type** it searches. It has to: with no Synchronisation Rule
behind it, nothing else says where to look, and a rule that does not say is skipped during synchronisation. An
Advanced mode rule does not name one, because the Synchronisation Rule that owns it already does.

JIM refuses to save a rule that could never match, naming what is missing. If any rule already stored has that
shape, the Matching tab says so and names it, so it can be removed and recreated.

The active mode also decides which rules the synchronisation engine consults: type-scoped rules in simple mode,
each Synchronisation Rule's own rules in advanced mode. In simple mode that holds even for an object no import
Synchronisation Rule applies to: its type's rules still join it, with nothing flowing in, which is how a target
directory's existing accounts join their identities. [Sync Preview](sync-preview.md) predicts these joins as the
synchronisation makes them. A rule of the other scope would be silently inert, so JIM
refuses to create one, naming the active mode and the remedy (create the rule in the scope the mode consults, or
switch the mode first). Rules of the other scope that already exist, retained by a mode switch, stay editable and
deletable so a later switch back can restore them.

Switching the mode warns about anything it strands:

- **To advanced mode**: the type-scoped rules are retained but no longer consulted (they resume effect on a switch
  back), and export Synchronisation Rules receive no copied rules, so export matching stops for them until rules
  are added: provisioning then creates an account even where the target already holds one for the identity.
- **To simple mode**: where an object type already has rules, those take precedence and the Synchronisation Rules'
  own rules are discarded rather than migrated.

The warnings appear in the portal after the switch, in the REST response's `warnings` list, and on the PowerShell
warning stream from [`Switch-JIMMatchingMode`](../powershell/object-matching-rules.md#switch-jimmatchingmode).

## :material-eye-outline: Previewing a behaviour change

The five behaviour toggles are the settings whose consequences are hardest to picture, because none of them names a
population. Disabling a rule reads like pausing it and is closer to withdrawing every value it owns. Turning
**Provision To Connected System** on reads like granting a capability and is Connected System Object creation at scale. Turning
**Enforce State** off reads like relaxing a constraint and is a standing decision to let a target system diverge.

**Preview Behaviour Impact**, which appears beside the editor's save button once you have edited a toggle, answers
what your edited toggles would do without saving them:

| Transition | What it means |
|---|---|
| No longer creates a Metaverse Object | Objects that would have had a Metaverse Object projected for them and now would not. They stay in the Connector Space, unmanaged. |
| No longer creates a Connected System Object | Metaverse Objects that would have had a Connected System Object created in the target system and now would not. Nothing existing is destroyed, which is why it goes unnoticed. |
| Free to drift from JIM | Objects whose divergence from what JIM holds would no longer be corrected. |
| Projected to the Metaverse / Provisioned / Drift corrected | The inverses, for a toggle being turned on. |

**Direction cannot be previewed, and cannot be changed.** A saved rule's Attribute Flow mappings and Object
Matching Rules are written for the direction it has: an import rule's mappings write Metaverse Attributes and its
matching rules search the Metaverse, so flipped to Export every one of them would address the wrong side. The
preview refuses with a blocking finding rather than answering about a configuration that cannot work. Create a rule
in the direction you need instead.

Toggles that do nothing in the rule's direction are called out rather than counted as zero, because "nothing is
affected" and "this setting does not apply here" are different statements and only one of them explains an empty
result. **Enforce State** and **Provision To Connected System** apply to Export rules; **Project To Metaverse**
applies to Import rules.

Automation gets the same evaluation: `New-JIMConfigurationChangePreview -SyncRuleId <id> -RuleState Disabled`, or
`POST` to the rule's `behaviour/preview` endpoint. See
[Configuration Change Preview](configuration-changes.md).

## :material-vector-link: Previewing an Object Matching change

Matching mistakes do not fail. A rule matched too loosely joins an account to the wrong Metaverse Object, and everything it
contributes goes with it; a rule matched too tightly projects a second Metaverse Object beside the right one. Both look like
a successful synchronisation, and both are found later by a person.

The Matching tab therefore offers **Preview Impact** beside **Add Matching Rule**, and again on the Simple/Advanced
switch. It answers what the proposed matching would do, without saving it.

The preview reports:

| Transition | What it means |
|---|---|
| Joins a different Metaverse Object | The object joins one Metaverse Object under the rules as they stand and would join a different one. The most dangerous outcome a matching change can produce. |
| Joins instead of projecting | The object matches nothing today, so the next synchronisation would create a new Metaverse Object for it, and under the proposal it would join an existing one. Usually what a widened rule is for. |
| Projects instead of joining | The inverse, and a duplicate Metaverse Object risk: the object matches today and would match nothing, so a second Metaverse Object would be created beside the one it should have joined. |
| Matches more than one Metaverse Object | The proposal is ambiguous for this object, so its next synchronisation refuses it rather than joining it to anything. Each row's **Matches** column links the Metaverse Objects it would find. |

One thing decides how to read every one of those counts: **Object Matching Rules are evaluated only for objects that
are not already joined**. An account with a Metaverse Object keeps it, whatever you change here, so the impact covers
the unjoined population alone. The preview says so before it says anything else.

Automation gets the same evaluation, over the whole matching configuration rather than one rule at a time:
`New-JIMConfigurationChangePreview -ConnectedSystemId <id> -MatchingRule <rules>`, or `POST` to the Connected
System's `matching-rules/preview` endpoint. Add `-ObjectMatchingRuleMode` to preview the Simple/Advanced switch. See
[Configuration Change Preview](configuration-changes.md).
