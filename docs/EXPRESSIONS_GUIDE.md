# Expression Language Guide

This guide covers the expression language used in JIM for attribute mappings, transformations, and conditional logic in sync rules.

## Overview

JIM uses [DynamicExpresso](https://github.com/dynamicexpresso/DynamicExpresso) to evaluate C#-like expressions at runtime. Expressions are used in:

- **Export attribute mappings** - Transform MVO attributes to CSO attributes
- **Import attribute mappings** - Transform CSO attributes to MVO attributes
- **Conditional logic** - Determine attribute values based on conditions

## Attribute Access

Access attributes using dictionary-style syntax:

```csharp
// Metaverse Object attributes
mv["Display Name"]
mv["Employee Status"]
mv["Department"]

// Connected System Object attributes
cs["sAMAccountName"]
cs["userAccountControl"]
```

## String Comparison - IMPORTANT

**Always use `Eq()` for string comparisons, NOT `==`.**

The `==` operator can fail for string comparisons because the attribute accessor returns `object?`, and comparing `object?` to `string` uses reference equality instead of value equality.

```csharp
// WRONG - may fail unpredictably
IIF(mv["Employee Status"] == "Active", 512, 514)

// CORRECT - always use Eq() for string comparisons
IIF(Eq(mv["Employee Status"], "Active"), 512, 514)
```

For case-insensitive comparisons, use `Lower()`:

```csharp
Eq(Lower(mv["Status"]), "active")
```

## Built-in Functions

### String Functions

| Function | Description | Example |
|----------|-------------|---------|
| `Trim(value)` | Remove leading/trailing whitespace | `Trim(mv["Name"])` |
| `Upper(value)` | Convert to uppercase | `Upper(mv["Name"])` |
| `Lower(value)` | Convert to lowercase | `Lower(mv["Name"])` |
| `Capitalise(value)` | Title case (handles hyphenated names) | `Capitalise(mv["Name"])` |
| `Left(value, count)` | Get leftmost characters | `Left(mv["Name"], 3)` |
| `Right(value, count)` | Get rightmost characters | `Right(mv["Name"], 4)` |
| `Substring(value, start, length)` | Extract substring | `Substring(mv["Name"], 2, 4)` |
| `Replace(value, old, new)` | Replace text | `Replace(mv["Email"], "@old.com", "@new.com")` |
| `Length(value)` | Get string length | `Length(mv["Name"])` |
| `IsNullOrEmpty(value)` | Check if null or empty | `IsNullOrEmpty(mv["Name"])` |
| `IsNullOrWhitespace(value)` | Check if null or whitespace | `IsNullOrWhitespace(mv["Name"])` |
| `StartsWith(value, prefix)` | Check string prefix | `StartsWith(mv["Email"], "admin")` |
| `EndsWith(value, suffix)` | Check string suffix | `EndsWith(mv["Email"], "@company.com")` |
| `Contains(value, search)` | Check if string contains | `Contains(mv["Email"], "@company")` |

### Conditional Functions

| Function | Description | Example |
|----------|-------------|---------|
| `IIF(condition, trueValue, falseValue)` | If-then-else | `IIF(Eq(mv["Status"], "Active"), 512, 514)` |
| `Coalesce(value1, value2)` | Return first non-null | `Coalesce(mv["Preferred Name"], mv["First Name"])` |
| `Eq(value1, value2)` | Value equality comparison | `Eq(mv["Department"], "IT")` |

### Conversion Functions

| Function | Description | Example |
|----------|-------------|---------|
| `ToString(value)` | Convert to string | `ToString(mv["EmployeeId"])` |
| `ToInt(value)` | Parse as integer (0 if invalid) | `ToInt(mv["Age"])` |

### Date Functions

| Function | Description | Example |
|----------|-------------|---------|
| `Now()` | Current UTC date/time | `Now()` |
| `Today()` | Current UTC date (midnight) | `Today()` |
| `FormatDate(date, format)` | Format date as string | `FormatDate(mv["HireDate"], "yyyy-MM-dd")` |
| `ToFileTime(date)` | Convert DateTime to Windows FILETIME | `ToFileTime(mv["Account Expires"])` |
| `FromFileTime(filetime)` | Convert FILETIME to DateTime | `FromFileTime(cs["accountExpires"])` |

### DN Helper Functions

| Function | Description | Example |
|----------|-------------|---------|
| `EscapeDN(value)` | Escape special DN characters | `"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"` |

### Password Generation

| Function | Description | Example |
|----------|-------------|---------|
| `RandomPassword(length, extendedChars)` | Generate random password | `RandomPassword(16, true)` |
| `RandomPassphrase(wordCount, separator)` | Generate passphrase | `RandomPassphrase(4, "-")` |

### Collection Functions

| Function | Description | Example |
|----------|-------------|---------|
| `CollectionContains(collection, value)` | Check if collection contains value | `CollectionContains(cs["memberOf"], "CN=Admins")` |
| `Split(value, delimiter)` | Split delimited string into array | `Split(cs["coursesCompleted"], "\|")` |
| `Join(collection, delimiter)` | Join collection into delimited string | `Join(mv["Groups"], ",")` |

### Bitwise Functions (Active Directory userAccountControl)

| Function | Description | Example |
|----------|-------------|---------|
| `EnableUser(uac)` | Clear ACCOUNTDISABLE bit | `EnableUser(cs["userAccountControl"])` |
| `DisableUser(uac)` | Set ACCOUNTDISABLE bit | `DisableUser(cs["userAccountControl"])` |
| `SetBit(value, bit)` | Set a specific bit | `SetBit(mv["uac"], 65536)` |
| `ClearBit(value, bit)` | Clear a specific bit | `ClearBit(mv["uac"], 65536)` |
| `HasBit(value, bit)` | Check if bit is set | `HasBit(cs["userAccountControl"], 2)` |

## Common Identity Management Scenarios

### User Account Enable/Disable (userAccountControl)

The `userAccountControl` attribute is a bitmask. Common values:

- `512` - Normal account (enabled)
- `514` - Normal account (disabled) - has ACCOUNTDISABLE (0x0002) bit set
- `66048` - Normal account with password never expires (512 + 65536)

**Enable/disable based on Employee Status:**

```csharp
IIF(Eq(mv["Employee Status"], "Active"), 512, 514)
```

**Check if account is disabled:**

```csharp
IIF(HasBit(cs["userAccountControl"], 2), "Disabled", "Enabled")
```

### Account Expiration (accountExpires)

The `accountExpires` attribute uses Windows FILETIME format (100-nanosecond intervals since January 1, 1601 UTC).

**Special values:**

- `0` - Account never expires
- `9223372036854775807` (Int64.MaxValue) - Account never expires

**Set account expiration from a date:**

```csharp
ToFileTime(mv["Employee End Date"])
```

**Important notes:**

- `ToFileTime()` returns `null` for null input, empty strings, or invalid dates
- `ToFileTime()` returns `null` for `DateTime.MinValue` and `DateTime.MaxValue` (representing "no date" or "never")
- When exporting to AD, a `null` result means no change to `accountExpires`
- To explicitly set "never expires", you may need to handle null separately

**Convert accountExpires back to DateTime:**

```csharp
FromFileTime(cs["accountExpires"])
```

**Notes on FromFileTime:**

- Returns `null` for `0` (never expires)
- Returns `null` for `Int64.MaxValue` (never expires)
- Returns `null` for null input or invalid values

### Email Address Generation

```csharp
Lower(mv["First Name"]) + "." + Lower(mv["Last Name"]) + "@company.com"
```

### Display Name with Title

```csharp
Coalesce(mv["Title"], "") + " " + mv["First Name"] + " " + mv["Last Name"]
```

### Distinguished Name Construction

Always escape user-provided values when building DNs:

```csharp
"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"
```

This escapes special characters like commas, plus signs, quotes, etc.

### Initials Generation

```csharp
Upper(Left(mv["First Name"], 1)) + Upper(Left(mv["Last Name"], 1))
```

### Conditional OU Assignment

```csharp
IIF(Eq(mv["Department"], "IT"),
    "OU=IT,OU=Users,DC=domain,DC=local",
    IIF(Eq(mv["Department"], "HR"),
        "OU=HR,OU=Users,DC=domain,DC=local",
        "OU=General,OU=Users,DC=domain,DC=local"))
```

### Department-Prefixed Account Names

```csharp
IIF(Eq(mv["Department"], "IT"), "tech-" + mv["Account Name"], mv["Account Name"])
```

### Converting Delimited Strings to Multi-Valued Attributes

When a source system stores multiple values in a single delimited field (e.g., `"COURSE1|COURSE2|COURSE3"`), use `Split()` to convert it to individual values in a multi-valued metaverse attribute.

**Example: Training courses from CSV to MVO**

The source CSV has a single column with pipe-separated courses:
```
employeeId,coursesCompleted
E001,"SOFT101|SOFT201|SEC101"
```

Use this expression to flow to a multi-valued "Courses Completed" MVO attribute:
```csharp
Split(cs["coursesCompleted"], "|")
```

This creates three separate values on the MVO:
- `SOFT101`
- `SOFT201`
- `SEC101`

**Notes on Split:**
- Empty entries are automatically removed
- Whitespace is trimmed from each value
- Returns an empty array for null or empty input
- The delimiter can be any string (e.g., `","`, `"|"`, `";"`)

### Converting Multi-Valued Attributes to Delimited Strings

When exporting to a system that doesn't support multi-valued attributes, use `Join()` to combine values.

**Example: MVO groups to CSV column**

```csharp
Join(mv["Group Memberships"], "|")
```

If the MVO has groups `["Admin", "Users", "Developers"]`, this produces:
```
Admin|Users|Developers
```

**Notes on Join:**
- Returns `null` for null or empty collections
- Filters out null and empty string values
- Uses comma as default delimiter if not specified

## Expression Validation

Expressions are validated when sync rules are saved. Invalid expressions will show an error with the position of the syntax error.

Common validation errors:

- Missing closing quote or bracket
- Unknown function name
- Invalid parameter types
- Syntax errors

## Debugging Expressions

When expressions don't produce expected results:

1. Check attribute names match exactly (case-sensitive)
2. Use `Eq()` instead of `==` for string comparisons
3. Verify the attribute exists on the source object
4. Check for null values that might affect the result
5. Review worker logs for expression evaluation errors

## Best Practices

1. **Always use `Eq()` for string comparisons** - The `==` operator is unreliable for comparing attribute values to string literals.

2. **Handle null values** - Use `Coalesce()` or `IsNullOrEmpty()` to handle missing attributes gracefully.

3. **Escape DN components** - Always use `EscapeDN()` when building Distinguished Names from user data.

4. **Test expressions** - Use the expression test feature in the UI to verify expressions with sample data before saving.

5. **Keep expressions simple** - Break complex logic into multiple mappings if possible.

6. **Document complex expressions** - Add comments in sync rule descriptions explaining what complex expressions do.