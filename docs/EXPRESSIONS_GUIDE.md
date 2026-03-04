# Expression Language Guide

| | |
|---|---|
| **Created** | 2026-01-22 |
| **Last Updated** | 2026-02-20 |
| **Status** | Active |

This guide covers JIM's expression language — a simple, readable syntax for transforming and mapping identity attributes in sync rules. No programming experience is required.

## Overview

JIM includes a built-in expression engine that lets you write short formulas to control how identity data flows between systems. If you've ever written a formula in a spreadsheet, you'll find this familiar.

Expressions are used in:

- **Export attribute mappings** — Transform metaverse attributes before sending them to a connected system
- **Import attribute mappings** — Transform connected system attributes before storing them in the metaverse
- **Conditional logic** — Choose different values based on conditions (e.g., enable or disable an account based on employee status)
- **Scoping filters** — Determine which objects are in scope for a sync rule

## Quick Examples

Before diving into the details, here are a few examples to give you a feel for how expressions work:

```csharp
// Build an email address from first and last name
Lower(mv["First Name"]) + "." + Lower(mv["Last Name"]) + "@company.com"

// Enable or disable an account based on employee status
IIF(Eq(mv["Employee Status"], "Active"), 512, 514)

// Use the preferred name if available, otherwise fall back to the first name
Coalesce(mv["Preferred Name"], mv["First Name"])
```

## Attribute Access

Expressions work with two sources of data:

- **`mv`** — attributes on the Metaverse Object (the central identity record)
- **`cs`** — attributes on the Connected System Object (the external system record)

Access an attribute by putting its name in square brackets and quotes:

```csharp
mv["Display Name"]
mv["Employee Status"]
mv["Department"]

cs["sAMAccountName"]
cs["userAccountControl"]
```

Attribute names must match the exact casing as defined in JIM — for example, `mv["Department"]` and `mv["department"]` are treated as different names. If an expression returns nothing unexpectedly, double-check the attribute name casing matches what's shown in the JIM admin UI.

## Operators

You can use standard operators to combine values, do arithmetic, and make comparisons.

### Joining Text

Use `+` to join text values together:

```csharp
mv["First Name"] + " " + mv["Last Name"]
// If First Name is "Jane" and Last Name is "Smith", the result is "Jane Smith"

"CN=" + mv["Account Name"] + ",OU=Users,DC=company,DC=local"
```

### Arithmetic

Standard maths operators work with numbers:

| Operator | Meaning        | Example              | Result |
|----------|----------------|----------------------|--------|
| `+`      | Add            | `10 + 3`             | `13`   |
| `-`      | Subtract       | `10 - 3`             | `7`    |
| `*`      | Multiply       | `10 * 3`             | `30`   |
| `/`      | Divide         | `10 / 3`             | `3`    |
| `%`      | Remainder      | `10 % 3`             | `1`    |

### Comparisons

Compare values using these operators. The result is either `true` or `false`:

| Operator | Meaning                  | Example            | Result  |
|----------|--------------------------|--------------------|---------|
| `>`      | Greater than             | `10 > 3`           | `true`  |
| `<`      | Less than                | `10 < 3`           | `false` |
| `>=`     | Greater than or equal to | `10 >= 10`         | `true`  |
| `<=`     | Less than or equal to    | `3 <= 10`          | `true`  |
| `!=`     | Not equal to             | `10 != 3`          | `true`  |

> **Important**: For comparing text values (strings), always use the `Eq()` function instead of `==`. See [String Comparison](#string-comparison---important) below.

### Logic

Combine multiple conditions using logical operators:

| Operator | Meaning | Example                        | Result  |
|----------|---------|--------------------------------|---------|
| `&&`     | AND     | `10 > 3 && 5 > 2`             | `true`  |
| `\|\|`   | OR      | `10 > 3 \|\| 5 < 2`           | `true`  |
| `!`      | NOT     | `!(10 > 3)`                    | `false` |

Example — check that an employee is both active and in the IT department:

```csharp
Eq(mv["Employee Status"], "Active") && Eq(mv["Department"], "IT")
```

## String Comparison — IMPORTANT

**Always use `Eq()` for comparing text values, NOT `==`.**

The `==` operator can give incorrect results when comparing attribute values to text. This is a technical limitation of how attribute values are stored internally.

```csharp
// WRONG — may give incorrect results
IIF(mv["Employee Status"] == "Active", 512, 514)

// CORRECT — always use Eq() for text comparisons
IIF(Eq(mv["Employee Status"], "Active"), 512, 514)
```

For case-insensitive comparisons (where "Active", "ACTIVE", and "active" should all match), wrap the value in `Lower()` first:

```csharp
Eq(Lower(mv["Status"]), "active")
```

> **Rule of thumb**: Use `Eq()` whenever you're comparing attribute values to text. Use `>`, `<`, `>=`, `<=`, `!=` for number comparisons.

## Built-in Functions

### String Functions

| Function | Description | Example |
|----------|-------------|---------|
| `Trim(value)` | Remove spaces from the start and end | `Trim(mv["Name"])` |
| `Upper(value)` | Convert to uppercase | `Upper(mv["Name"])` |
| `Lower(value)` | Convert to lowercase | `Lower(mv["Name"])` |
| `Capitalise(value)` | Capitalise each word (handles hyphenated names like "O'Brien-Smith") | `Capitalise(mv["Name"])` |
| `Left(value, count)` | Take the first N characters | `Left(mv["Name"], 3)` |
| `Right(value, count)` | Take the last N characters | `Right(mv["Name"], 4)` |
| `Substring(value, start, length)` | Extract a portion of text | `Substring(mv["Name"], 2, 4)` |
| `Replace(value, old, new)` | Replace one piece of text with another | `Replace(mv["Email"], "@old.com", "@new.com")` |
| `Length(value)` | Count the number of characters | `Length(mv["Name"])` |
| `IsNullOrEmpty(value)` | Check if a value is missing or blank | `IsNullOrEmpty(mv["Name"])` |
| `IsNullOrWhitespace(value)` | Check if a value is missing, blank, or only spaces | `IsNullOrWhitespace(mv["Name"])` |
| `StartsWith(value, prefix)` | Check if text begins with a specific value | `StartsWith(mv["Email"], "admin")` |
| `EndsWith(value, suffix)` | Check if text ends with a specific value | `EndsWith(mv["Email"], "@company.com")` |
| `Contains(value, search)` | Check if text contains a specific value | `Contains(mv["Email"], "@company")` |

### Conditional Functions

| Function | Description | Example |
|----------|-------------|---------|
| `IIF(condition, trueValue, falseValue)` | Return one value if a condition is true, another if false | `IIF(Eq(mv["Status"], "Active"), 512, 514)` |
| `Coalesce(value1, value2)` | Use the first value if it exists, otherwise use the second | `Coalesce(mv["Preferred Name"], mv["First Name"])` |
| `Eq(value1, value2)` | Check if two values are equal (required for text comparisons) | `Eq(mv["Department"], "IT")` |

### Conversion Functions

| Function | Description | Example |
|----------|-------------|---------|
| `ToString(value)` | Convert a value to text | `ToString(mv["EmployeeId"])` |
| `ToInt(value)` | Convert text to a whole number (returns 0 if not a valid number) | `ToInt(mv["Age"])` |

### Date Functions

| Function | Description | Example |
|----------|-------------|---------|
| `Now()` | Current date and time (UTC) | `Now()` |
| `Today()` | Current date at midnight (UTC) | `Today()` |
| `FormatDate(date, format)` | Format a date as text (e.g., "2026-03-15") | `FormatDate(mv["HireDate"], "yyyy-MM-dd")` |
| `ToFileTime(date)` | Convert a date to Active Directory's FILETIME format | `ToFileTime(mv["Account Expires"])` |
| `FromFileTime(filetime)` | Convert an Active Directory FILETIME back to a date | `FromFileTime(cs["accountExpires"])` |

### Distinguished Name (DN) Functions

| Function | Description | Example |
|----------|-------------|---------|
| `EscapeDN(value)` | Safely escape special characters for use in LDAP distinguished names | `"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"` |

### Password Generation

| Function | Description | Example |
|----------|-------------|---------|
| `RandomPassword(length, extendedChars)` | Generate a random password. Set extendedChars to `true` for special characters (!@#$%^&*) | `RandomPassword(16, true)` |
| `RandomPassphrase(wordCount, separator)` | Generate a passphrase from random words | `RandomPassphrase(4, "-")` |

### Collection Functions

These functions work with multi-valued attributes (attributes that hold a list of values rather than a single value).

| Function | Description | Example |
|----------|-------------|---------|
| `CollectionContains(collection, value)` | Check if a list of values contains a specific value | `CollectionContains(cs["memberOf"], "CN=Admins")` |
| `Split(value, delimiter)` | Split a delimited text value into a list | `Split(cs["coursesCompleted"], "\|")` |
| `Join(collection, delimiter)` | Combine a list of values into a single delimited text value | `Join(mv["Groups"], ",")` |

### Account Control Functions

These functions are primarily used with Active Directory's `userAccountControl` attribute, which stores account settings as a single number using individual bit flags.

| Function | Description | Example |
|----------|-------------|---------|
| `EnableUser(uac)` | Enable a user account | `EnableUser(cs["userAccountControl"])` |
| `DisableUser(uac)` | Disable a user account | `DisableUser(cs["userAccountControl"])` |
| `SetBit(value, bit)` | Turn on a specific flag | `SetBit(mv["uac"], 65536)` |
| `ClearBit(value, bit)` | Turn off a specific flag | `ClearBit(mv["uac"], 65536)` |
| `HasBit(value, bit)` | Check if a specific flag is turned on | `HasBit(cs["userAccountControl"], 2)` |

## Common Scenarios

This section shows practical examples of expressions you'll use when setting up identity synchronisation.

### Enabling and Disabling User Accounts

Active Directory uses a `userAccountControl` attribute to control whether an account is enabled or disabled. The common values are:

| Value   | Meaning                                    |
|---------|--------------------------------------------|
| `512`   | Normal account, enabled                    |
| `514`   | Normal account, disabled                   |
| `66048` | Normal account, enabled, password never expires |

**Enable or disable based on employee status:**

```csharp
IIF(Eq(mv["Employee Status"], "Active"), 512, 514)
```

This reads as: "If the Employee Status is Active, set the value to 512 (enabled), otherwise set it to 514 (disabled)."

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

- If the date attribute is empty or missing, these functions return nothing (null) — no change is made to the target
- AD treats `0` and very large numbers as "never expires" — `FromFileTime()` returns nothing for these values
- `ToFileTime()` safely handles empty text, missing values, and invalid dates by returning nothing

### Building Email Addresses

```csharp
Lower(mv["First Name"]) + "." + Lower(mv["Last Name"]) + "@company.com"
```

For "Jane Smith", this produces: `jane.smith@company.com`

### Building a Display Name with Title

```csharp
Coalesce(mv["Title"], "") + " " + mv["First Name"] + " " + mv["Last Name"]
```

`Coalesce` uses the title if one exists, otherwise uses an empty string — avoiding a "null" appearing in the output.

### Building Distinguished Names (DNs)

When constructing LDAP distinguished names, always use `EscapeDN()` to handle special characters (commas, quotes, etc.) in names:

```csharp
"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"
```

Without `EscapeDN()`, a name like "Smith, Jane" would break the DN because of the comma.

### Generating Initials

```csharp
Upper(Left(mv["First Name"], 1)) + Upper(Left(mv["Last Name"], 1))
```

For "Jane Smith", this produces: `JS`

### Placing Users in Different OUs by Department

```csharp
IIF(Eq(mv["Department"], "IT"),
    "OU=IT,OU=Users,DC=domain,DC=local",
    IIF(Eq(mv["Department"], "HR"),
        "OU=HR,OU=Users,DC=domain,DC=local",
        "OU=General,OU=Users,DC=domain,DC=local"))
```

This nests `IIF` functions to check multiple conditions: first IT, then HR, with a default of General for everyone else.

### Adding a Prefix to Account Names

```csharp
IIF(Eq(mv["Department"], "IT"), "tech-" + mv["Account Name"], mv["Account Name"])
```

IT department users get a "tech-" prefix; everyone else keeps their account name as-is.

### Splitting Delimited Values into a List

When a source system stores multiple values in a single field separated by a delimiter (e.g., `"COURSE1|COURSE2|COURSE3"`), use `Split()` to break them into separate values.

**Example — training courses from a CSV file:**

The source CSV has pipe-separated courses in a single column:
```
employeeId,coursesCompleted
E001,"SOFT101|SOFT201|SEC101"
```

Use this expression to create individual values on the metaverse object:
```csharp
Split(cs["coursesCompleted"], "|")
```

This creates three separate values: `SOFT101`, `SOFT201`, and `SEC101`.

**Notes:**
- Empty entries are automatically removed
- Whitespace is trimmed from each value
- Returns nothing for missing or empty input
- The delimiter can be any text (e.g., `","`, `"|"`, `";"`)

### Combining a List into a Single Value

When exporting to a system that doesn't support multi-valued attributes, use `Join()` to combine them into one delimited value.

```csharp
Join(mv["Group Memberships"], "|")
```

If the attribute has the values Admin, Users, and Developers, this produces: `Admin|Users|Developers`

**Notes:**
- Returns nothing for empty lists
- Empty and missing values are filtered out
- Uses a comma as the default delimiter if none is specified

## Validation and Troubleshooting

### Validation

JIM validates expressions when you save a sync rule. If an expression has a syntax error, you'll see an error message indicating what went wrong and where in the expression the problem is.

Common errors:

- **Missing closing quote or bracket** — check that every `"` and `(` has a matching pair
- **Unknown function name** — check the spelling; function names are case-sensitive
- **Wrong number of parameters** — check the function reference above for the correct parameters
- **Syntax errors** — look for misplaced operators or missing commas between function parameters

### Troubleshooting

When an expression doesn't produce the result you expect:

1. **Check attribute names carefully** — names must match the exact casing shown in the JIM admin UI, so `mv["department"]` and `mv["Department"]` are treated differently
2. **Use `Eq()` for text comparisons** — using `==` for text is a common mistake (see [String Comparison](#string-comparison---important))
3. **Check for missing values** — if an attribute doesn't exist on the object, it returns nothing (null), which can affect the result. Use `Coalesce()` or `IsNullOrEmpty()` to handle this
4. **Test with sample data** — use the expression test feature in the sync rule editor to try your expression with real attribute values before saving
5. **Check the worker logs** — if expressions fail during sync, the worker service logs the error details

## Best Practices

1. **Always use `Eq()` for text comparisons** — the `==` operator can give incorrect results when comparing attribute values.

2. **Handle missing values** — use `Coalesce()` to provide a fallback, or `IsNullOrEmpty()` to check before using a value. This prevents unexpected results when an attribute hasn't been populated yet.

3. **Always use `EscapeDN()` in distinguished names** — this prevents special characters in names from breaking LDAP paths.

4. **Test before saving** — use the expression test feature in the UI to verify your expressions with sample data.

5. **Keep expressions simple** — if an expression is getting complex, consider splitting the logic across multiple attribute mappings.

6. **Document complex expressions** — add a note in the sync rule's description explaining what complex expressions do, so the next person can understand them.