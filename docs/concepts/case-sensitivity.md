# Case Sensitivity

JIM takes a deliberate, consistent approach to how it compares text. The guiding principle is simple: **identity data is compared exactly (case-sensitive) by default, while the names you use to configure JIM are forgiving (case-insensitive).** Where exact matching would be too strict for a real-world data source, you can relax it per rule.

Understanding this model helps you predict when a change will flow, when two objects will join, and why an [expression](expressions.md) behaves the way it does.

## ⚖️ The principle

- **Data is exact by default.**<br /> Comparisons that decide whether data changes, whether objects link, or whether provisioning happens are case-sensitive. If a source system distinguishes `JSmith` from `jsmith`, JIM respects that distinction and propagates the change rather than silently dropping it.
- **Configuration names are forgiving.**<br /> When you refer to an attribute, Connected System, or object type by name, JIM matches it case-insensitively. A capital letter in the wrong place should not stop your configuration from working.
- **Search is forgiving.**<br /> Searching and filtering in the admin UI and logs ignores case, for convenience.

## Where each rule applies

### Data flow (exact by default)

These comparisons determine whether data moves. They are case-sensitive so that a genuine change of case from a source system is detected and synchronised, not lost.

| Comparison | Default | Configurable? |
|------------|---------|---------------|
| Attribute value change detection | Case-sensitive | ❌ No |
| External ID matching | Case-sensitive | ❌ No |
| Reference (link) value matching | Case-sensitive | ❌ No |
| Object Matching Rules | Case-sensitive | ✅ Per rule |
| Scoping criteria | Case-sensitive | ✅ Per criterion |

Object Matching Rules and scoping criteria expose a **case sensitive** toggle, so you can opt into case-insensitive behaviour where a data source is inconsistent. For example, if an HR feed sometimes records a department as `Sales` and sometimes `SALES`, you can make a scoping criterion match both by turning case sensitivity off for that criterion. The same applies to a matching rule joining objects across systems that disagree on casing.

### Configuration names (forgiving)

| Lookup | Behaviour |
|--------|-----------|
| Attribute names (including `mv["..."]` / `cs["..."]` in expressions) | Case-insensitive |
| Connected System names | Case-insensitive |
| Object type names | Case-insensitive |

### Search and display (forgiving)

Admin search, UI filtering, and log searching all ignore case.

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
