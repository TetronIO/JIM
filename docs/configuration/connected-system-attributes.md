---
title: Connected System Attributes
description: "How JIM records whether it can write each attribute, what data type it holds, and why credential attributes are never managed."
type: object
audience: administrator
---

# Connected System Attributes

Each attribute JIM discovers in a Connected System's schema carries what JIM learned about it: whether the system lets JIM write it, what type of value it holds, and whether it is a credential. This page covers all three.

## Attribute writability

When JIM retrieves a Connected System's schema, each discovered attribute is recorded with how the system will let JIM write to it. You can see this in the Schema tab's **Writability** column, filter the attribute list by it, and read it from the REST API and PowerShell as the attribute's `writability` value. It is discovered, never set by an administrator: it reflects what the Connected System told JIM.

There are three states.

| Shown as | `writability` | What it means |
|----------|---------------|---------------|
| Writable | `Writable` | The Connected System accepts writes to this attribute. An export Attribute Flow can target it and keep it up to date. |
| Read-Only | `ReadOnly` | The Connected System will not accept writes at all. The attribute can still be imported (`whenCreated` and `objectSid` are useful to hold in the Metaverse), but no export Attribute Flow may target it; JIM refuses the mapping when you try to create it. |
| Set on creation only | `WritableOnCreate` | The Connected System accepts a value only as part of creating the object. An export Attribute Flow may target it, and usually should: without one the object cannot be provisioned. JIM sends the value with the Create Pending Export and never sends it again. |

### Why "Set on creation only" exists

Some attributes are what the Connected System uses to identify the object. A relational table's primary key is the clearest case: JIM has to supply it when it inserts the row, and from then on it is what ties the Connected System Object to that row. Rewriting it later would not update the row, it would point JIM at a different one, and the object JIM thought it was managing would be orphaned. A directory's relative distinguished name has the same shape, being changed by a rename operation rather than by an ordinary attribute write.

JIM therefore treats these attributes as write-once, and enforces it on the export path rather than trusting the configuration to be right:

- **Provisioning**<br /> The value flows normally. It is part of the Create Pending Export, exactly like any other mapped attribute.
- **Updates**<br /> The attribute is excluded from every Update Pending Export, **even when the Metaverse value has changed**. Nothing is sent, and no error is raised: this is the intended behaviour, not a failure.
- **Drift Correction**<br /> A value that has diverged in the Connected System is not treated as drift and is not corrected, because correcting it would mean rewriting the identifier.

If a source value feeding one of these attributes genuinely does change (an employee number is reissued, say), JIM will not chase it into the Connected System. That is deliberate: re-identifying an existing object is a decision for an administrator, not something a synchronisation run should do quietly.

The Attribute Flow editor marks an export mapping whose target is set on creation only, so it is clear at a glance which mappings apply during provisioning alone, and a Connected System Object's detail page marks the attribute itself, so the same is obvious when looking at a single object's values.

## Attribute data types

Every discovered attribute is recorded with a JIM data type, shown in the Schema tab's **Type** column. An Attribute Flow requires its source and target to be the same type, so this is what decides which Metaverse Attributes an attribute can be mapped to.

Most of the time the Connected System states its type unambiguously and JIM records it. A directory's schema and a SCIM service provider's schema are both definitive, so their attribute types are fixed and cannot be changed.

### When the source cannot say

Two cases leave JIM inferring rather than reading.

A delimited file names no types at all, so the File Connector has always asked you to choose.

A relational database states a type for every column, but not every database distinguishes types the way JIM does. Microsoft SQL Server does: `int` is a whole number, `bigint` a 64-bit whole number, `decimal(9,4)` a fractional figure, and JIM records each accordingly. **Oracle has a single numeric type.** An employee identifier, a large counter and a fractional figure are all `NUMBER`, distinguished only by the precision and scale the column was declared with.

JIM therefore reads that declaration and picks the narrowest type guaranteed to hold every value the column permits:

| Declared | Becomes | Why |
|----------|---------|-----|
| `NUMBER(p,0)`, p up to 9 | Number | The widest such column holds 999,999,999, which fits a 32-bit whole number. |
| `NUMBER(p,0)`, p from 10 to 18 | Long Number | Ten digits already exceed a 32-bit whole number, so the ordinary sequence-backed key lands here. |
| `NUMBER(p,0)`, p of 19 or more | Decimal | Nineteen digits can exceed a 64-bit whole number, so narrowing would risk losing a value. |
| `NUMBER(p,s)` with a scale | Decimal | The column is genuinely fractional. |
| `NUMBER` with no precision | Decimal | The declaration states no width, so JIM assumes the widest. |

`NUMBER(1)` is a whole number unless you switch on **Treat NUMBER(1) Columns as Boolean** on the Connected System, which is opt-in because a single-digit column is just as often a small number as a flag.

### Overriding an inferred type

Where a Connector's schema cannot state a type definitively, the Schema tab shows an **Edit** control on each attribute row. Choose the type the column is actually for and the attribute is recorded with it.

The attribute's **Description** states the source column type it was built from (`Source column type: NUMBER(10).`), so you can see what the inference was based on before deciding whether to disagree with it.

This is how an Oracle `NUMBER(10)` employee identifier is pointed at the built-in `Employee Number` Metaverse Attribute, which is a Number. Use the built-in attributes wherever they fit: a custom attribute created only to work around a type is one no other Connected System will match on.

!!! warning "Set the type before you build on it"
    An override is refused once the attribute is referenced by a Synchronisation Rule or already holds values, because changing it then would reinterpret data that was imported under the previous type. If you need to change it later, remove the references, or clear the Connected System Objects, first.

From PowerShell:

```powershell
Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId 5 -AttributeId 10 -Type Integer
```

`Integer` is the friendly name for the Number type, matching `New-JIMMetaverseAttribute`. There is no bulk equivalent: the bulk attribute endpoint refuses a request carrying a data type rather than ignoring it, so a scripted build cannot appear to succeed having changed nothing.

The same field is available on the REST API's attribute update, `PUT api/v1/synchronisation/connected-systems/{connectedSystemId}/object-types/{objectTypeId}/attributes/{attributeId}`.

An override survives a schema refresh. JIM records that the type was chosen rather than inferred, so a refresh restates everything the Connector discovered (writability, plurality, the source column type) and leaves your choice alone. To go back to the inferred type, set it back yourself; a refresh will not do it for you.

!!! note "Existing Connected Systems"
    Improvements to how JIM infers a type apply when a schema is next retrieved. An existing Connected System keeps the types it already holds until you refresh its schema.

## Case-exact values

Each Text attribute says whether its values are **case-exact**: whether `JBloggs` and `jbloggs` are two different values or one. Most identifiers are not case-exact (directory account names, email addresses, distinguished names), so a Text attribute ignores case unless its Connected System's schema says otherwise. [Case Sensitivity](../concepts/case-sensitivity.md#case-exact-attributes) lists what each Connector discovers, and what the declaration decides.

The Schema tab shows a **Case-exact** column for any Object Type with a case-exact attribute, and the tooltip on each says whether the Connected System's schema stated it or an administrator set it.

### Changing the declaration

A Connected System's schema cannot always say. A delimited file says nothing about case, and a Connected System can compare values more strictly than its schema states. Every Connector therefore lets you set it: open the attribute's **Edit** control on the Schema tab, set **Values are case-exact**, confirm, and save the schema.

From PowerShell:

```powershell
Set-JIMConnectedSystemAttribute -ConnectedSystemId 1 -ObjectTypeId 5 -AttributeId 12 -CaseExact $true
```

In bulk, give `caseExact` in an attribute's hashtable: `-AttributeUpdates @{ 12 = @{ caseExact = $true } }`. Through the REST API, the attribute update and bulk attribute update both take `caseExact` (`PUT /api/v1/synchronisation/connected-systems/{connectedSystemId}/object-types/{objectTypeId}/attributes/{attributeId}`). A non-text attribute cannot be declared case-exact, and the request says so rather than ignoring it.

Your answer survives a schema refresh. JIM records that the declaration was set rather than discovered, exactly as it does for an overridden data type, so a refresh restates what the Connector discovered and leaves your choice alone.

!!! note "Existing Connected Systems"
    A Connected System created before JIM recorded case-exactness has every attribute ignoring case until its schema is next refreshed, when the discovered declarations are filled in. Refresh the schema to see them.

## Credential attributes are never managed

Some attributes hold credential material, or a hash of it. JIM will never import them, never let you select them for management, and never let you name them as the source or target of an Attribute Flow:

`unicodePwd`, `userPassword`, `dBCSPwd`, `ntPwdHistory`, `lmPwdHistory`, `supplementalCredentials`, `unixUserPassword`, `msDS-ManagedPassword`

There are two reasons. Most of these cannot be read back meaningfully (a directory returns nothing at all for `unicodePwd`, and opaque blobs for the history attributes), so anything imported would be empty or meaningless and every subsequent synchronisation would see a spurious change. The rest hold live credential material, and anything that reaches the Metaverse is replicated onward to every other Connected System in scope, written into change history, and rendered in the portal.

Passwords are synchronised through JIM's dedicated password channel instead. That channel writes a password to a Connected System and never reads it back, so it is never held in the Metaverse. For LDAP and Active Directory the LDAP Connector writes `unicodePwd` itself, with the correct encoding; see [Setting Passwords](../connectors/jim-ldap-connector-passwords.md#setting-passwords) for the connection requirements.

What you will see:

- **Schema refresh**<br /> Credential attributes found in the Connected System are reported as blocked. They are counted as neither added nor removed, because neither is true.
- **Attribute selection**<br /> The Selected switch is disabled, with a tooltip explaining why. Selecting one through the REST API or PowerShell is rejected.
- **Attribute Flow**<br /> Credential attributes do not appear in the source or target attribute lists, and naming one through the REST API or PowerShell is rejected.
- **Upgrades**<br /> If a credential attribute was selected on an existing deployment, the next schema refresh deselects and locks it rather than deleting it, so any Synchronisation Rule that references it stays intact. Remove those Attribute Flows and use the password channel instead.

Attributes that merely *look* credential-bearing, such as `pwdLastSet`, `badPwdCount` and `pwdProperties`, are unaffected and remain fully selectable; they carry no credential material.

### When JIM warns instead of blocking

The list above is the set JIM knows by name. It cannot cover a directory or line-of-business system with a password field of its own naming, and that is exactly where somebody is most likely to reach for the wrong answer: mapping the password as an ordinary attribute.

So when an Attribute Flow targets an attribute whose *name* suggests it carries a password, and that name is not on the list above, the Synchronisation Rule shows a warning naming the attribute, saying what flowing a password as an attribute would store it in, and pointing at Password Synchronisation instead. It appears wherever the rule's validity is shown: the Synchronisation Rule editor, the REST API and PowerShell alike.

**The warning never blocks anything.** It is a judgement about a name, not about the data, and it is deliberately broad enough to catch a field JIM has never seen. Attributes such as `pwdLastSet`, `badPwdCount`, `pwdProperties` and `passwordHistoryLength` match it and are perfectly ordinary things to flow, so the wording says so: if the attribute is not a password, no action is needed and the rule saves exactly as it is. Anything that genuinely must be refused belongs on the blocked list instead, not behind a warning.

If the attribute *does* carry a password, the answer is [Password Synchronisation](connected-system-passwords.md#password-synchronisation), which delivers it to the system without it ever being stored as a value.
