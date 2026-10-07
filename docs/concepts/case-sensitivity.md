# Case Sensitivity

JIM takes a deliberate, consistent approach to how it compares text. The guiding principle is simple: **identity data is compared exactly (case-sensitive) by default, the identifiers JIM generates are kept unique without regard to case, and the names you use to configure JIM are forgiving (case-insensitive).** Where exact matching would be too strict for a real-world data source, you can relax it per rule.

Understanding this model helps you predict when a change will flow, when two objects will join, and why an [expression](expressions.md) behaves the way it does.

## ⚖️ The principle

- **Data is exact by default.**<br /> Comparisons that decide whether data changes, whether objects link, or whether provisioning happens are case-sensitive. If a source system distinguishes `JSmith` from `jsmith`, JIM respects that distinction and propagates the change rather than silently dropping it.
- **Identifiers are unique without regard to case.**<br /> When JIM checks whether a value it generates is already in use, `JBloggs` and `jbloggs` are the same value. Whether a value has *changed* and whether a value is *taken* are different questions, and JIM answers them differently on purpose; see [Identifier uniqueness](#identifier-uniqueness-ignores-case).
- **Configuration names are forgiving.**<br /> When you refer to an attribute, Connected System, or object type by name, JIM matches it case-insensitively. A capital letter in the wrong place should not stop your configuration from working.
- **Search is forgiving.**<br /> Searching and filtering in the admin UI and logs ignores case, for convenience.

## Where each rule applies

### Data flow (exact by default)

These comparisons determine whether data moves. They are case-sensitive so that a genuine change of case from a source system is detected and synchronised, not lost.

| Comparison | Default | Configurable? |
|------------|---------|---------------|
| Attribute value change detection | Case-sensitive | ❌ No |
| Export confirmation (did the value JIM sent arrive?) | Case-sensitive | ❌ No |
| Drift Detection | Case-sensitive | ❌ No |
| External ID matching | Case-sensitive | ❌ No |
| Reference (link) value matching | Case-sensitive | ❌ No |
| Object Matching Rules | Case-sensitive | ✅ Per rule |
| Scoping criteria | Case-sensitive | ✅ Per criterion |

Object Matching Rules and scoping criteria expose a **case sensitive** toggle, so you can opt into case-insensitive behaviour where a data source is inconsistent. For example, if an HR feed sometimes records a department as `Sales` and sometimes `SALES`, you can make a scoping criterion match both by turning case sensitivity off for that criterion. The same applies to a matching rule joining objects across systems that disagree on casing.

A Connected System that changes the case of a value as it stores it needs one extra step; see [When a target changes the case of a value](#when-a-target-changes-the-case-of-a-value).

### Identifier uniqueness (ignores case)

These checks decide whether a value JIM generates, such as an account name, is free to issue (see [Generated values](../configuration/synchronisation-rules.md#generated-values)). They ignore case, and cannot be switched to exact matching.

| Check | Behaviour |
|-------|-----------|
| Values issued earlier in the same run | Case-insensitive |
| Metaverse Objects' values for the attribute | Case-insensitive |
| Accounts JIM has imported from each Connected System the value is exported to | Case-insensitive |
| Retired values, which are never issued again | Case-insensitive |
| Probing a Connected System for accounts JIM has not imported | Case-insensitive, wherever the target can be asked |

Why case is ignored here, when it is not for data flow:

- **The systems that hold identifiers ignore case.**<br /> Directory account names, user principal names, email addresses, LDAP `uid`, SCIM `userName` and Microsoft SQL Server's default collation all treat `JBloggs` and `jbloggs` as one value. A directory refuses the second, and a mail system delivers both to one mailbox.
- **One value has to be unique everywhere it goes.**<br /> A generated value is usually exported to several Connected Systems, so it has to satisfy the strictest of them. A value that is free only under exact matching is not free in a directory.
- **A retired identifier stays retired.**<br /> Issuing a leaver's `jbloggs` to a new starter as `JBloggs` would, in every system that ignores case, give the new starter the leaver's mailbox, group memberships and access.
- **One answer, whenever JIM asks.**<br /> JIM's own records and a probe of the target answer alike, so whether a clash is caught never depends on whether the other account has been imported yet.

**Case changes still flow.** Uniqueness compares a value with *other* identities' values; change detection compares an identity's value with its own previous one. A person's own values never count against them: if Jane Smith's existing account is `JSmith` and JIM issues her `jsmith`, the account is hers, so the value is free for her, and the change of case is exported to her account like any other change. Make the target's export [Initial Export Only](../configuration/synchronisation-rules.md#initial-export-only-outbound) if you want JIM to set the value when it creates an account and leave the account's own case alone afterwards.

**Where a target cannot be asked to ignore case.** A SCIM attribute that the service provider declares case exact, such as `externalId` in the core schema, can only be searched for an exact match. A value that differs only in case is then not found by the probe, but the provider would not refuse it either, and JIM's own records of the accounts it has imported still ignore case. See the SCIM 2.0 Client Connector's [Probing for values already in use](../connectors/jim-scim-connector.md#probing-for-values-already-in-use).

### Configuration names (forgiving)

| Lookup | Behaviour |
|--------|-----------|
| Attribute names (including `mv["..."]` / `cs["..."]` in expressions) | Case-insensitive |
| Connected System names | Case-insensitive |
| Object type names | Case-insensitive |

### Search and display (forgiving)

Admin search, UI filtering, and log searching all ignore case.

## 🔠 When a target changes the case of a value

Because JIM compares values exactly, a Connected System that stores a value in a case of its own (lower-casing email addresses as it saves them, say) never hands back what JIM sent. JIM then cannot confirm the export: it retries the change on later exports, and once the retries are spent it reports the export as failed. Nothing is lost on the target, which holds the value in its own case; what fails is JIM's confirmation that its value arrived.

Fold the case before the value reaches that system, so JIM sends what the system keeps:

- **On an import Attribute Flow**<br /> Set **Case normalisation** in [Value processing](../configuration/synchronisation-rules.md#value-processing-inbound), so the Metaverse holds the value in the case the target uses.
- **On an export Attribute Flow, or a generated value's base expression**<br /> Wrap the value in `Lower()` (or `Upper()`), for example `Lower(mv["Email"])`.

Build the identifiers JIM generates in lower case for the same reason, as in `Lower(cs["firstName"]) + "." + Lower(cs["lastName"])`: every Connected System then holds the same value, however it treats case.

## 🧮 Case sensitivity in expressions

Two different rules apply inside an [expression](expressions.md), and keeping them straight avoids most surprises:

- **Attribute names are case-insensitive.** `mv["Department"]` and `mv["department"]` refer to the same attribute.
- **Attribute values are case-sensitive.** Comparing a value to text with `Eq()` is an exact, case-sensitive match. To compare without regard to case, lower-case both sides first:

```csharp
Eq(Lower(mv["Status"]), "active")
```

This is why the [Expression Language Guide](expressions.md) recommends `Eq()` (never `==`) for text, and `Lower()` when you want a case-insensitive check.

## Advanced: system-wide case-insensitivity

The defaults above suit the vast majority of deployments, and the per-rule toggles cover the common exceptions. If your environment genuinely requires case-insensitive behaviour across *all* data (for example, to mirror the collation of a legacy system being replaced), this can be arranged at the database level through PostgreSQL collation configuration. Treat this as an advanced option of last resort; prefer the per-rule toggles wherever they are sufficient.
