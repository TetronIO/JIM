---
title: Case Sensitivity
description: The four questions JIM asks whenever it compares text, and how each treats upper and lower case.
type: concept
audience: administrator
---

# Case Sensitivity

Whenever JIM compares two pieces of text, it is answering one of four questions. Each question has one rule, so once you know which question a comparison asks, you know how it treats case.

Understanding this model helps you predict when a change will flow, when two objects will join, and why an [expression](expressions.md) behaves the way it does.

## :material-format-letter-case: The policy

| Question | Rule | Where JIM asks it |
|----------|------|-------------------|
| **Has this value changed?** | Exact; for an [identifier](#identifiers-a-target-reports-in-its-own-case), export confirmation and Drift Detection ask "Are these the same thing?" instead | Change detection, export confirmation, Drift Detection |
| **Are these the same thing?** | Ignore case, unless the attribute is [case-exact](#case-exact-attributes) | External IDs, Secondary External IDs, references, Object Matching Rules, generated-value uniqueness, attribute names in expressions, [configuration names](#configuration-names-ignore-case) |
| **Does this value meet a condition?** | Exact, unless you say otherwise | Scoping criteria, Predefined Search criteria, comparisons in expressions |
| **Finding something** | Ignore case | Search boxes and list filters in the portal, log search |

- **A change of case is a change.**<br /> If a source system changes `jsmith` to `JSmith`, JIM detects the change and synchronises it rather than dropping it.
- **A difference of case is not a different identity.**<br /> The systems that hold identifiers ignore case: directory account names, user principal names, email addresses, LDAP distinguished names, SCIM `userName` and Microsoft SQL Server's default collation all treat `JBloggs` and `jbloggs` as one value. Comparing them exactly would make JIM disagree with itself: it would refuse to issue `jbloggs` because `JBloggs` is taken, yet decline to join HR's `JBloggs` to the directory's `jbloggs` and create a duplicate identity instead. The exceptions are identifiers a Connected System declares [case-exact](#case-exact-attributes).
- **A condition means what it says.**<br /> A scoping criterion for `Sales` matches `Sales` and not `SALES`. Where a source is inconsistent about casing, you switch that one criterion to ignore case.
- **Finding things is forgiving.**<br /> A capital letter in the wrong place should not hide what you are looking for.

## :material-table-large: Quick reference

Every feature that compares text, the question it asks, and the rule that answers it.

| Area | Feature | Question | Rule | Can you change it? |
|------|---------|----------|------|--------------------|
| Importing | Finding the existing Connected System Object for an incoming object, by External ID or Secondary External ID | Same thing? | Ignore case, unless the attribute is case-exact | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } Per attribute |
| Importing | Detecting two incoming objects with the same External ID | Same thing? | Ignore case, unless the External ID is case-exact | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } Per attribute |
| Importing | Resolving references, such as group members and managers, to the objects they point to | Same thing? | Ignore case, unless the referenced object's External ID or Secondary External ID is case-exact | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } Per attribute |
| Importing | Whether an attribute value changed | Changed? | Exact | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } No |
| Synchronising | [Object Matching Rules](#object-matching-rules): joining on import, and finding an existing account before provisioning on export | Same thing? | Ignore case, unless a source attribute is case-exact | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } Per rule |
| Synchronising | [Scoping criteria](../configuration/scoping-criteria.md) | Condition? | Exact | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } Per criterion |
| Synchronising | Attribute Flow: whether a Metaverse value changed | Changed? | Exact | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } No |
| Synchronising | [Generated values](#identifier-uniqueness-ignores-case): whether a candidate is already taken | Same thing? | Ignore case | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } No |
| Synchronising | [Case normalisation](../configuration/attribute-mappings.md#value-processing-inbound) in value processing | Changes case rather than comparing it | Upper, lower or title case, as you choose | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } Per Attribute Flow |
| Exporting | Whether a value needs exporting, export confirmation (did the value JIM sent arrive?) and Drift Detection | Changed? | Exact | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } No |
| Exporting | The same three, for an [identifier](#identifiers-a-target-reports-in-its-own-case): an External ID, a Secondary External ID or a reference | Same thing? | Ignore case, unless the attribute is case-exact | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } Per attribute, by its case-exact declaration |
| Exporting | Drift Detection: whether a Connected System contributes an attribute that an export expression names | Same thing? | Ignore case | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } No |
| Exporting | Renaming a directory object whose name changed only in case | Changed? | Exact for the naming attribute, such as `cn`; the rest of the distinguished name ignores case | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } No |
| Importing | Matching a file's column headers to attribute names | Same thing? | Ignore case | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } No |
| Importing | Matching a database's related-table and change-log rows to their object | Same thing? | As the database compares the anchor column: ignore case unless its collation is case-sensitive | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } Per attribute |
| Expressions | Attribute names in `mv["..."]` and `cs["..."]` | Same thing? | Ignore case | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } No |
| Schema | [Case-exact declaration](#case-exact-attributes) on a Connected System attribute | Says how its values answer "Same thing?" | Discovered from the Connected System's schema | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } Per attribute |
| Expressions | `==`, `Eq` | Condition? | Exact, character by character | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } Wrap both sides in `Lower()` |
| Expressions | `StartsWith`, `EndsWith`, `Contains`, `IsMatch`, `Where` | Condition? | Exact, character by character | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } Pass `true` as the last argument |
| Expressions | `RegexReplace` | Changes text rather than comparing it | Exact | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } Pass `true` as the fourth argument |
| Expressions | `Replace`, `Split`, `RegexExtract` | Changes text rather than comparing it | Exact | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } No |
| Searching | Predefined Search criteria | Condition? | Exact | :material-check-circle-outline:{ .jim-yes role="img" aria-label="Yes" } Per criterion |
| Searching | Portal search boxes and list filters, log search | Finding? | Ignore case | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } No |
| Configuration | [Names you give JIM](#configuration-names-ignore-case): attribute, Object Type, Connected System, Run Profile, Connector and Predefined Search names | Same thing? | Ignore case | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } No |
| Configuration | [Schema refresh](../configuration/connected-system-schema.md#refreshing-the-schema): matching the Object Types and attributes a Connected System reports to the ones JIM holds | Same thing? | Ignore case | :material-close-circle-outline:{ .jim-no role="img" aria-label="No" } No |

## :material-vector-link: Object Matching Rules

An Object Matching Rule asks whether an account and an identity are the same thing, so it ignores case: HR's `JBloggs` joins the directory's `jbloggs`. The exception is a rule reading an attribute declared [case-exact](#case-exact-attributes): a new rule on such an attribute respects case, because its Connected System treats `JBloggs` and `jbloggs` as two identifiers. A rule matching on an expression ignores case. The default is the same however the rule is created, in the portal, through the REST API or with PowerShell.

Turn on **Case sensitive** for any other rule only when its Connected System really does treat values that differ only in case as different objects. Do it sparingly: with case sensitivity on, an account whose value differs from the identity's only in case does not join it, and JIM creates a duplicate identity instead. A rule takes its default when it is created; declaring an attribute case-exact later does not change rules that already read it.

Rules created through the REST API or PowerShell before JIM ignored case by default, without stating a setting, respect case. Nothing about them changed: each rule keeps the setting it was saved with. In simple matching mode the Connected System's Object Matching tab shows each rule's setting as a switch you can turn off. In either mode, `Get-JIMMatchingRule` and `Get-JIMSyncRuleMatchingRule` show it as `CaseSensitive`, and `Set-JIMMatchingRule` or `Set-JIMSyncRuleMatchingRule` with `-CaseSensitive $false` turns it off.

## :material-format-letter-case-upper: Case-exact attributes

Some identifiers really are different when their case differs: SCIM's `id`, an Oracle column, a SQL Server column with a case-sensitive collation, an Active Directory attribute with a case-sensitive string syntax. JIM records this on each Connected System attribute as its **case-exact** declaration, which says how the attribute's values answer "are these the same thing?".

Where the Connected System's schema says so, JIM discovers it when it retrieves the schema:

| Connected System | Case-exact when |
|------------------|-----------------|
| LDAP directories | The attribute's equality matching rule is exact (`caseExactMatch`, `caseExactIA5Match`, `octetStringMatch` or `uuidMatch`), declared on the attribute or inherited from the attribute it derives from |
| Active Directory and Samba AD | The attribute's syntax is a case-sensitive string: String(Case Sensitive), String(IA5) or String(Printable) |
| SCIM service providers | The schema declares the attribute `caseExact`; `id` and `externalId` always are |
| Microsoft SQL Server | The column's collation is case-sensitive; a SELECT statement's columns have no collation to read and ignore case |
| Oracle Database | The column's collation does not ignore case, which is Oracle's own default; a SELECT statement's columns are case-exact |
| Delimited files | Never discovered: a file says nothing about case |

Only Text attributes carry the declaration. The Schema tab shows a **Case-exact** column wherever an Object Type has a case-exact attribute.

To change it, open the attribute's **Edit** dialog on the Schema tab and set **Values are case-exact**, or use `Set-JIMConnectedSystemAttribute -CaseExact` (see [Case-exact values](../configuration/connected-system-attributes.md#case-exact-values)). Your answer is kept when the schema is next refreshed; one you have not changed is restated from the Connected System's schema each time.

The declaration decides how JIM recognises an object by its identifiers. On an External ID or Secondary External ID declared case-exact, `JBloggs` and `jbloggs` are two objects:

- **Importing** finds the Connected System Object JIM already holds only by its value in its own case, treats two incoming objects whose External IDs differ only in case as two objects, and resolves a reference only to the object whose identifier it names in that case.
- **Exporting** counts a value the Connected System reports in another case as a different value (see [Identifiers a target reports in its own case](#identifiers-a-target-reports-in-its-own-case)).
- **Object Matching Rules** created on the attribute respect case by default.

Every other identifier ignores case in all of these.

## :material-filter-outline: Scoping and search criteria

Scoping criteria and Predefined Search criteria ask whether a value meets a condition, so each one is exact unless you switch it to ignore case. That is the right default for a condition: `Sales` means `Sales`. Where a source is inconsistent, for example an HR feed that sometimes records a department as `Sales` and sometimes `SALES`, switch off case sensitivity on that criterion and it matches both.

Updating a criterion through the REST API or PowerShell keeps its case sensitivity unless the update states it, so `Set-JIMScopingCriterion` or `Set-JIMPredefinedSearchCriterion` without `-CaseSensitive` leaves a criterion that ignores case as it is.

## :material-identifier: Identifier uniqueness (ignores case)

These checks decide whether a value JIM generates, such as an account name, is free to issue (see [Generated values](../configuration/generated-values.md)). They ignore case, and cannot be switched to exact matching.

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

**Case changes still flow.** Uniqueness asks whether a value is the same as *other* identities' values; change detection asks whether an identity's value has changed from its own previous one. A person's own values never count against them: if Jane Smith's existing account is `JSmith` and JIM issues her `jsmith`, the account is hers, so the value is free for her, and the change of case is exported to her account like any other change. Make the target's export [Initial Export Only](../configuration/attribute-mappings.md#initial-export-only-outbound) if you want JIM to set the value when it creates an account and leave the account's own case alone afterwards.

**Where a target cannot be asked to ignore case.** A SCIM attribute that the service provider declares case exact, such as `externalId` in the core schema, can only be searched for an exact match. A value that differs only in case is then not found by the probe, but the provider would not refuse it either, and JIM's own records of the accounts it has imported still ignore case. See the SCIM 2.0 Client Connector's [Probing for values already in use](../connectors/jim-scim-connector.md#probing-for-values-already-in-use).

## :material-swap-vertical: When a target changes the case of a value

Because JIM confirms an export of ordinary text by comparing values exactly, a Connected System that stores a value in a case of its own (lower-casing email addresses as it saves them, say) never hands back what JIM sent. JIM then cannot confirm the export: it sends the change again at every later export, and reports it as not confirmed on every import, for as long as the two disagree. Nothing is lost on the target, which holds the value in its own case; what fails is JIM's confirmation that its value arrived. An identifier is the exception; see [Identifiers a target reports in its own case](#identifiers-a-target-reports-in-its-own-case).

Fold the case before the value reaches that system, so JIM sends what the system keeps:

- **On an import Attribute Flow**<br /> Set **Case normalisation** in [Value processing](../configuration/attribute-mappings.md#value-processing-inbound), so the Metaverse holds the value in the case the target uses.
- **On an export Attribute Flow, or a generated value's base expression**<br /> Wrap the value in `Lower()` (or `Upper()`), for example `Lower(mv["Email"])`.

Build the identifiers JIM generates in lower case for the same reason, as in `Lower(cs["firstName"]) + "." + Lower(cs["lastName"])`: every Connected System then holds the same value, however it treats case.

### Identifiers a target reports in its own case

An identifier is different. A directory reports a distinguished name in the case it stored, and for the parts above the object's own name (`DC=panoply,DC=local`, say) that case is the directory's, not JIM's: an export of `CN=Joe Bloggs,OU=Users,DC=panoply,DC=local` can come back as `CN=Joe Bloggs,OU=Users,DC=PANOPLY,DC=LOCAL`, naming the same entry. So for an External ID, a Secondary External ID and a reference, JIM asks whether the value names the same thing rather than whether it changed:

- **Export confirmation** confirms the value when the Connected System holds it in any case.
- **Exporting** does not stage a value the Connected System already holds in another case.
- **Drift Detection** does not report a case-only difference as drift.

A reference JIM has resolved to another Connected System Object is confirmed when the Connected System Object refers to that object, whatever its distinguished name looks like.

The object's own name is JIM's, though. When the value of a directory object's naming attribute changes only in case, from `joe bloggs` to `Joe Bloggs` say, the LDAP Connector renames the entry so that the directory holds the new case, keeping the parts above it as the directory has them.

An identifier declared [case-exact](#case-exact-attributes) still compares exactly, because its Connected System treats `JBloggs` and `jbloggs` as two identifiers.

## :material-text-box-edit-outline: Configuration names ignore case

A name you give JIM identifies a piece of configuration, so it asks whether two names are the same thing and ignores case. `Get-JIMMetaverseObject -AttributeName 'employee id'` finds Employee Id, `Get-JIMActivity -ConnectedSystem 'hr'` finds HR's Activities, and an Example Data pattern's `{first name}` reads First Name. This covers attribute and Object Type names, Connected System and Run Profile names in Activity filters, Connector names and Predefined Search URIs, across the REST API, PowerShell and the portal.

Some Connected Systems can hold two attributes whose names differ only in case, such as a CSV file with both a `Mail` and a `mail` column. An exact name always wins, so each still finds its own attribute. A name that matches neither exactly, such as `MAIL`, finds nothing, because choosing one of them would be a guess.

The same rule applies when a [schema refresh](../configuration/connected-system-schema.md#refreshing-the-schema) finds an Object Type or attribute under a name that differs only in case from the one JIM holds. It is the same Object Type or attribute: it keeps every Synchronisation Rule mapping and Object Matching Rule that uses it, takes the Connected System's new casing, and the preview lists it as renamed.

## :material-function-variant: Case sensitivity in expressions

Two different questions are asked inside an [expression](expressions.md), and keeping them straight avoids most surprises:

- **Attribute names identify an attribute, so they ignore case.**<br /> `mv["Department"]` and `mv["department"]` refer to the same attribute.
- **Comparing a value is a condition, so it is exact.**<br /> `==`, `Eq()`, `StartsWith()` and `EndsWith()` compare character by character, so case counts, and so do characters that look alike but are stored differently, such as a hidden soft hyphen. To compare without regard to case, lower-case both sides first:

```csharp
Eq(Lower(mv["Status"]), "active")
```

Use `Lower()` whenever you want a case-insensitive check; [Expressions](expressions.md#string-comparison) covers where `==` and `Eq()` differ.
