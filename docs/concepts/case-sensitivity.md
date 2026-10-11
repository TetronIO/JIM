# Case Sensitivity

Whenever JIM compares two pieces of text, it is answering one of four questions. Each question has one rule, so once you know which question a comparison asks, you know how it treats case.

Understanding this model helps you predict when a change will flow, when two objects will join, and why an [expression](expressions.md) behaves the way it does.

## ⚖️ The policy

| Question | Rule | Where JIM asks it |
|----------|------|-------------------|
| **Has this value changed?** | Exact | Change detection, export confirmation, Drift Detection |
| **Are these the same thing?** | Ignore case | External IDs, references, Object Matching Rules, generated-value uniqueness, attribute names in expressions |
| **Does this value meet a condition?** | Exact, unless you say otherwise | Scoping criteria, Predefined Search criteria, comparisons in expressions |
| **Finding something** | Ignore case | Search boxes and list filters in the portal, log search |

- **A change of case is a change.**<br /> If a source system changes `jsmith` to `JSmith`, JIM detects the change and synchronises it rather than dropping it.
- **A difference of case is not a different identity.**<br /> The systems that hold identifiers ignore case: directory account names, user principal names, email addresses, LDAP distinguished names, SCIM `userName` and Microsoft SQL Server's default collation all treat `JBloggs` and `jbloggs` as one value. Comparing them exactly would make JIM disagree with itself: it would refuse to issue `jbloggs` because `JBloggs` is taken, yet decline to join HR's `JBloggs` to the directory's `jbloggs` and create a duplicate identity instead.
- **A condition means what it says.**<br /> A scoping criterion for `Sales` matches `Sales` and not `SALES`. Where a source is inconsistent about casing, you switch that one criterion to ignore case.
- **Finding things is forgiving.**<br /> A capital letter in the wrong place should not hide what you are looking for.

## 🗂️ Quick reference

Every feature that compares text, the question it asks, and the rule that answers it.

| Area | Feature | Question | Rule | Can you change it? |
|------|---------|----------|------|--------------------|
| Importing | Finding the existing Connected System Object for an incoming object, by External ID | Same thing? | Ignore case | ❌ No |
| Importing | Detecting two incoming objects with the same External ID | Same thing? | Ignore case | ❌ No |
| Importing | Resolving references, such as group members and managers, to the objects they point to | Same thing? | Ignore case | ❌ No |
| Importing | Whether an attribute value changed | Changed? | Exact | ❌ No |
| Synchronising | [Object Matching Rules](#object-matching-rules): joining on import, and finding an existing account before provisioning on export | Same thing? | Ignore case | ✅ Per rule |
| Synchronising | [Scoping criteria](../configuration/synchronisation-rules.md#scoping-criteria) | Condition? | Exact | ✅ Per criterion |
| Synchronising | Attribute Flow: whether a Metaverse value changed | Changed? | Exact | ❌ No |
| Synchronising | [Generated values](#identifier-uniqueness-ignores-case): whether a candidate is already taken | Same thing? | Ignore case | ❌ No |
| Synchronising | [Case normalisation](../configuration/synchronisation-rules.md#value-processing-inbound) in value processing | Changes case rather than comparing it | Upper, lower or title case, as you choose | ✅ Per Attribute Flow |
| Exporting | Export confirmation (did the value JIM sent arrive?) | Changed? | Exact | ❌ No |
| Exporting | Drift Detection | Changed? | Exact | ❌ No |
| Expressions | Attribute names in `mv["..."]` and `cs["..."]` | Same thing? | Ignore case | ❌ No |
| Expressions | `Eq`, `StartsWith`, `EndsWith`, `Contains`, `CollectionContains` | Condition? | Exact | ✅ Wrap both sides in `Lower()` |
| Expressions | `Replace`, `Split` | Changes text rather than comparing it | Exact | ❌ No |
| Searching | Predefined Search criteria | Condition? | Exact | ✅ Per criterion |
| Searching | Portal search boxes and list filters, log search | Finding? | Ignore case | ❌ No |

## 🔗 Object Matching Rules

An Object Matching Rule asks whether an account and an identity are the same thing, so it ignores case: HR's `JBloggs` joins the directory's `jbloggs`. This holds however the rule is created, in the portal, through the REST API or with PowerShell.

Turn on **Case sensitive** for a rule only when its Connected System really does treat values that differ only in case as different objects. Do it sparingly: with case sensitivity on, an account whose value differs from the identity's only in case does not join it, and JIM creates a duplicate identity instead.

Rules created through the REST API or PowerShell before JIM ignored case by default, without stating a setting, respect case. Nothing about them changed: each rule keeps the setting it was saved with. In simple matching mode the Connected System's Object Matching tab shows each rule's setting as a switch you can turn off. In either mode, `Get-JIMMatchingRule` and `Get-JIMSyncRuleMatchingRule` show it as `CaseSensitive`, and `Set-JIMMatchingRule` or `Set-JIMSyncRuleMatchingRule` with `-CaseSensitive $false` turns it off.

## 🎯 Scoping and search criteria

Scoping criteria and Predefined Search criteria ask whether a value meets a condition, so each one is exact unless you switch it to ignore case. That is the right default for a condition: `Sales` means `Sales`. Where a source is inconsistent, for example an HR feed that sometimes records a department as `Sales` and sometimes `SALES`, switch off case sensitivity on that criterion and it matches both.

## 🪪 Identifier uniqueness (ignores case)

These checks decide whether a value JIM generates, such as an account name, is free to issue (see [Generated values](../configuration/synchronisation-rules.md#generated-values)). They ignore case, and cannot be switched to exact matching.

| Check | Behaviour |
|-------|-----------|
| Values issued earlier in the same run | Case-insensitive |
| Metaverse Objects' values for the attribute | Case-insensitive |
| Accounts JIM has imported from each Connected System the value is exported to | Case-insensitive |
| Retired values, which are never issued again | Case-insensitive |
| Probing a Connected System for accounts JIM has not imported | Case-insensitive, wherever the target can be asked |

Why case is ignored here:

- **One value has to be unique everywhere it goes.**<br /> A generated value is usually exported to several Connected Systems, so it has to satisfy the strictest of them. A value that is free only under exact matching is not free in a directory.
- **A retired identifier stays retired.**<br /> Issuing a leaver's `jbloggs` to a new starter as `JBloggs` would, in every system that ignores case, give the new starter the leaver's mailbox, group memberships and access.
- **One answer, whenever JIM asks.**<br /> JIM's own records and a probe of the target answer alike, so whether a clash is caught never depends on whether the other account has been imported yet.

**Case changes still flow.** Uniqueness asks whether a value is the same as *other* identities' values; change detection asks whether an identity's value has changed from its own previous one. A person's own values never count against them: if Jane Smith's existing account is `JSmith` and JIM issues her `jsmith`, the account is hers, so the value is free for her, and the change of case is exported to her account like any other change. Make the target's export [Initial Export Only](../configuration/synchronisation-rules.md#initial-export-only-outbound) if you want JIM to set the value when it creates an account and leave the account's own case alone afterwards.

**Where a target cannot be asked to ignore case.** A SCIM attribute that the service provider declares case exact, such as `externalId` in the core schema, can only be searched for an exact match. A value that differs only in case is then not found by the probe, but the provider would not refuse it either, and JIM's own records of the accounts it has imported still ignore case. See the SCIM 2.0 Client Connector's [Probing for values already in use](../connectors/jim-scim-connector.md#probing-for-values-already-in-use).

## 🔠 When a target changes the case of a value

Because JIM confirms an export by comparing values exactly, a Connected System that stores a value in a case of its own (lower-casing email addresses as it saves them, say) never hands back what JIM sent. JIM then cannot confirm the export: it retries the change on later exports, and once the retries are spent it reports the export as failed. Nothing is lost on the target, which holds the value in its own case; what fails is JIM's confirmation that its value arrived.

Fold the case before the value reaches that system, so JIM sends what the system keeps:

- **On an import Attribute Flow**<br /> Set **Case normalisation** in [Value processing](../configuration/synchronisation-rules.md#value-processing-inbound), so the Metaverse holds the value in the case the target uses.
- **On an export Attribute Flow, or a generated value's base expression**<br /> Wrap the value in `Lower()` (or `Upper()`), for example `Lower(mv["Email"])`.

Build the identifiers JIM generates in lower case for the same reason, as in `Lower(cs["firstName"]) + "." + Lower(cs["lastName"])`: every Connected System then holds the same value, however it treats case.

## 🧮 Case sensitivity in expressions

Two different questions are asked inside an [expression](expressions.md), and keeping them straight avoids most surprises:

- **Attribute names identify an attribute, so they ignore case.**<br /> `mv["Department"]` and `mv["department"]` refer to the same attribute.
- **Comparing a value is a condition, so it is exact.**<br /> `Eq()` is an exact, case-sensitive match. To compare without regard to case, lower-case both sides first:

```csharp
Eq(Lower(mv["Status"]), "active")
```

This is why the [Expression Language Guide](expressions.md) recommends `Eq()` (never `==`) for text, and `Lower()` when you want a case-insensitive check.
