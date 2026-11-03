---
title: Expression Recipes
description: "Worked expressions for common Attribute Flows: account state, names and email addresses, distinguished names, and delimited values."
type: reference
audience: administrator
---

# Expression Recipes

These are worked [expressions](expressions.md) for the Attribute Flows most deployments need, each ready to adapt to your own attribute names. Every recipe shows the expression, what it produces, and what to watch for; the functions they use are described in full on [Expressions](expressions.md#built-in-functions), and what happens when an input attribute is missing is set out in [Nulls and Missing Inputs](expression-nulls.md).

```jim-diagram
archetype: decision
title: Which group of recipes covers the value you are building
desc: "Start from the target attribute you want to set. If it controls whether an account is enabled or when it expires, use the Account State recipes. Otherwise, if it is a distinguished name or decides an organisational unit, use the Distinguished Names and OUs recipes. Otherwise, if it holds several values, or several values in one delimited field, use the Multi-Valued Attributes recipes. Otherwise, use the Names and Addresses recipes, which build single values such as email addresses, display names and initials."
start: { label: The target attribute }
questions:
  - { id: account, label: "Controls an account?", sub: "enabled, or when it expires" }
  - { id: dn, label: "An LDAP location?", sub: "a DN, or which OU" }
  - { id: multi, label: "Several values?", sub: "or one delimited field" }
outcomes:
  - { id: state, label: Account State, sub: "`IIF()`, `ToFileTime()`" }
  - { id: dns, label: Distinguished Names and OUs, sub: "`EscapeDN()`, nested `IIF()`" }
  - { id: lists, label: Multi-Valued Attributes, sub: "`Split()`, `Join()`" }
  - { id: names, label: Names and Addresses, sub: "`Lower()`, `Coalesce()`, `Left()`" }
answers:
  - { from: account, to: state, label: "yes" }
  - { from: account, to: dn, label: "no" }
  - { from: dn, to: dns, label: "yes" }
  - { from: dn, to: multi, label: "no" }
  - { from: multi, to: lists, label: "yes" }
  - { from: multi, to: names, label: "no" }
```

## :material-account-cancel-outline: Account State

Active Directory decides whether an account can sign in, and until when, from two attributes: `userAccountControl` and `accountExpires`. These recipes set both from what the Metaverse knows about the person.

### Enabling and Disabling User Accounts

Active Directory uses a `userAccountControl` attribute to control whether an account is enabled or disabled. The common values are:

| Value   | Meaning                                        |
|---------|------------------------------------------------|
| `512`   | Normal account, enabled                        |
| `514`   | Normal account, disabled                       |
| `66048` | Normal account, enabled, password never expires |

**Enable or disable based on employee status:**

```csharp
IIF(Eq(mv["Employee Status"], "Active"), 512, 514)
```

This reads as: "If the Employee Status is Active, set the value to 512 (enabled), otherwise set it to 514 (disabled)."

!!! note "Creating accounts enabled on Active Directory"
    Active Directory refuses to create an enabled account that has no password, so on a Synchronisation Rule that provisions accounts, give the rule an [Initial Password](../configuration/initial-password.md) as well. JIM then creates the account disabled and enables it once the password is set, and the expression keeps deciding the enabled state from then on. See [Enabling new Active Directory accounts](../connectors/jim-ldap-connector-passwords.md#enabling-new-active-directory-accounts).

    An account this expression creates disabled (`514`), such as a pre-hire's, stays disabled when its password is delivered, whatever the Initial Password's **Enable the Connected System Object once the password is set** says: the expression decides the enabled state. See [Accounts your Attribute Flow creates disabled](../connectors/jim-ldap-connector-passwords.md#accounts-your-attribute-flow-creates-disabled).

**Check if an account is currently disabled:**

```csharp
IIF(HasBit(cs["userAccountControl"], 2), "Disabled", "Enabled")
```

### Setting Account Expiration Dates

Active Directory stores expiration dates in a special format called FILETIME. JIM provides functions to convert between normal dates and this format automatically.

**Set the account expiration from an employee's end date:**

```csharp
ToFileTime(mv["Employee End Date"])
```

**Convert an AD expiration date back to a readable date:**

```csharp
FromFileTime(cs["accountExpires"])
```

**Important notes:**

- If the date attribute is empty or missing, these functions return nothing (null), which asserts "no value" for the target: for a sole contributor any existing value is removed; see [Nulls and Missing Inputs](expression-nulls.md)
- AD treats `0` and very large numbers as "never expires", so `FromFileTime()` returns nothing for these values
- `ToFileTime()` safely handles empty text, missing values, and invalid dates by returning nothing

## :material-card-account-details-outline: Names and Addresses

Most Attribute Flows build a value from parts of a name. These recipes join, trim and reshape name attributes into the values target systems expect.

### Building Email Addresses

```csharp
Lower(mv["First Name"]) + "." + Lower(mv["Last Name"]) + "@company.com"
```

For "Jane Smith", this produces: `jane.smith@company.com`

### Building a Display Name with Title

```csharp
Coalesce(mv["Title"], "") + " " + mv["First Name"] + " " + mv["Last Name"]
```

`Coalesce` uses the title if one exists, otherwise uses an empty string, avoiding a "null" appearing in the output.

### Generating Initials

```csharp
Upper(Left(mv["First Name"], 1)) + Upper(Left(mv["Last Name"], 1))
```

For "Jane Smith", this produces: `JS`

### Adding a Prefix to Account Names

```csharp
IIF(Eq(mv["Department"], "IT"), "tech-" + mv["Account Name"], mv["Account Name"])
```

IT department users get a "tech-" prefix; everyone else keeps their account name as-is.

## :material-file-tree-outline: Distinguished Names and OUs

A distinguished name (DN) places an object in an LDAP directory. These recipes build one safely, and choose the organisational unit (OU) from an attribute.

### Building Distinguished Names (DNs)

When constructing LDAP distinguished names, always use `EscapeDN()` to handle special characters (commas, quotes, etc.) in names:

```csharp
"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"
```

Without `EscapeDN()`, a name like "Smith, Jane" would break the DN because of the comma.

### Placing Users in Different OUs by Department

```csharp
IIF(Eq(mv["Department"], "IT"),
    "OU=IT,OU=Users,DC=domain,DC=local",
    IIF(Eq(mv["Department"], "HR"),
        "OU=HR,OU=Users,DC=domain,DC=local",
        "OU=General,OU=Users,DC=domain,DC=local"))
```

This nests `IIF` functions to check multiple conditions: first IT, then HR, with a default of General for everyone else.

## :material-format-list-bulleted: Multi-Valued Attributes

Some systems keep several values in one delimited field, and some accept only one value per attribute. These recipes convert between the two.

### Splitting Delimited Values into a List

When a source system stores multiple values in a single field separated by a delimiter (e.g., `"COURSE1|COURSE2|COURSE3"`), use `Split()` to break them into separate values.

**Example (training courses from a CSV file):**

The source CSV has pipe-separated courses in a single column:

```csv
employeeId,coursesCompleted
E001,"SOFT101|SOFT201|SEC101"
```

Use this expression to create individual values on the Metaverse Object:

```csharp
Split(cs["coursesCompleted"], "|")
```

This creates three separate values: `SOFT101`, `SOFT201`, and `SEC101`.

**Notes:**

- Empty entries are automatically removed
- Whitespace is trimmed from each value
- Returns nothing for missing or empty input
- The delimiter can be any text (e.g., `","`, `"|"`, `";"`)
- Each value is stored in the target attribute's type (`"1|2"` into a Number attribute stores 1 and 2); if any value cannot be, nothing flows and the object is reported
- Into a single-valued attribute, a list of more than one value flows nothing and the object is reported
- `Join()` puts the values back together in the same order, so `Join(Split(x, "|"), "|")` gives back `x`

### Combining a List into a Single Value

When exporting to a system that does not support multi-valued attributes, use `Join()` to combine them into one delimited value.

```csharp
Join(mv["Group Memberships"], "|")
```

If the attribute has the values Admin, Users, and Developers, this produces `Admin|Developers|Users`: an attribute's values are joined in their fixed order (see [Multi-Valued Attributes](expressions.md#multi-valued-attributes)), so the result is the same after every import.

**Notes:**

- Returns nothing for empty lists
- Empty and missing values are filtered out
- Uses a comma as the default delimiter if none is specified
