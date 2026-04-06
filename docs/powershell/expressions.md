---
title: Expressions
---

# Expressions

Test sync rule expressions with sample data before deploying them to production mappings. Expressions use DynamicExpresso syntax with `mv["AttributeName"]` and `cs["AttributeName"]` for attribute access.

## Test-JIMExpression

Tests an expression with sample metaverse and connected system attribute values.

### Syntax

```powershell
Test-JIMExpression -Expression <string> [-MvAttributes <hashtable>] [-CsAttributes <hashtable>]
```

### Parameters

| Name | Type | Required | Default | Description |
|------|------|----------|---------|-------------|
| Expression | string | Yes | | The expression to test (DynamicExpresso syntax) |
| MvAttributes | hashtable | No | | Sample metaverse attribute values (name-value pairs) |
| CsAttributes | hashtable | No | | Sample connected system attribute values (name-value pairs) |

### Output

Object with properties: `IsValid` (bool), `Result` (the computed value), `ResultType` (string), `ErrorMessage` (string, null if valid), `ErrorPosition` (int, null if valid).

### Examples

```powershell title="Simple string concatenation"
Test-JIMExpression -Expression 'cs["FirstName"] + " " + cs["LastName"]' -CsAttributes @{
    FirstName = "Jane"
    LastName  = "Smith"
}
```

```powershell title="Generate email address"
Test-JIMExpression -Expression 'Lower(cs["FirstName"]) + "." + Lower(cs["LastName"]) + "@company.com"' -CsAttributes @{
    FirstName = "Jane"
    LastName  = "Smith"
}
```

```powershell title="DN construction with escaping"
Test-JIMExpression -Expression '"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"' -MvAttributes @{
    "Display Name" = "O'Brien, Jane"
}
```

```powershell title="Conditional expression"
Test-JIMExpression -Expression 'cs["Department"] == "IT" ? "Technology" : cs["Department"]' -CsAttributes @{
    Department = "IT"
}
```

```powershell title="Check for expression errors"
$result = Test-JIMExpression -Expression 'mv["Missing"'
if (-not $result.IsValid) {
    Write-Warning "Expression error at position $($result.ErrorPosition): $($result.ErrorMessage)"
}
```

---

!!! tip "Expression Syntax"
    Expressions use DynamicExpresso syntax. Use `mv["AttributeName"]` for metaverse attributes and `cs["AttributeName"]` for connected system attributes. Built-in functions include `Lower()`, `Upper()`, `Trim()`, `EscapeDN()`, `Left()`, `Right()`, `Mid()`, and more. See the [Expressions concept guide](../concepts/expressions.md) for the full function reference.

## See also

- [Concepts: Expressions](../concepts/expressions.md)
- [Sync Rules](sync-rules.md)
