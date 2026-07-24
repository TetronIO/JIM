---
title: PowerShell Module
---

# PowerShell Module

The JIM PowerShell module provides a cross-platform command-line interface for managing and automating JIM operations. It wraps the JIM REST API in idiomatic PowerShell cmdlets with full pipeline support, making it straightforward to script identity management workflows.

## Requirements

- **[PowerShell 7.0 or later](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell)** (cross-platform: Windows, macOS, and Linux)

## Installation

### From PowerShell Gallery

```powershell
Install-Module -Name JIM
```

### Air-Gapped Environments

For environments without internet connectivity, the module is included in JIM release bundles. Copy the module folder to a PowerShell module path on the target machine:

```powershell
# Check available module paths
$env:PSModulePath -split [IO.Path]::PathSeparator

# Copy the module to one of those paths, then import it
Import-Module JIM
```

## Authentication

The module supports two authentication methods: interactive browser-based SSO and API keys.

### Interactive SSO

```powershell
Connect-JIM -Url "https://jim.example.com"
```

This opens a browser window for OIDC authentication. Once authenticated, the session token is cached and automatically refreshed.

### API Key

```powershell
Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"
```

API keys are the recommended method for automation, CI/CD pipelines, and scripting. Keys can be created via the JIM web UI or using `New-JIMApiKey`.

### Verifying the Connection

```powershell
# Detailed connection status
Test-JIMConnection

# Boolean check for scripts
if (Test-JIMConnection -Quiet) {
    Get-JIMConnectedSystem
}
```

See [Connection](connection.md) for full details on all connection cmdlets.

## Cmdlet Categories

| Category | Cmdlets | Description |
|----------|---------|-------------|
| [System](system.md) | 5 | Health checks, version, auth config, user info, and system reset |
| [Connection](connection.md) | 3 | Connect, disconnect, and test JIM sessions |
| [Connected Systems](connected-systems.md) | 20 | Manage Connected Systems, schemas, partitions, connector space objects, and connector definitions |
| [Run Profiles](run-profiles.md) | 5 | Create and execute import, sync, and export operations |
| [Synchronisation Rules](synchronisation-rules.md) | 23 | Define attribute mappings, scoping criteria, and Object Matching Rules |
| [Metaverse](metaverse.md) | 14 | Query objects, manage schema types and attributes, set Attribute Priority, and review pending deletions |
| [Predefined Searches](predefined-searches.md) | 9 | List and toggle the searches that drive portal list views and the fast search API, and manage their filter criteria (groups and criteria) |
| [Schedules](schedules.md) | 11 | Automate synchronisation workflows with scheduled execution |
| [Activities](activities.md) | 3 | Monitor operation history, statistics, and execution items |
| [API Keys](api-keys.md) | 4 | Create, manage, and revoke API keys |
| [Certificates](certificates.md) | 6 | Manage trusted certificates for connector authentication |
| [Service Settings](service-settings.md) | 3 | View and modify runtime configuration |
| [Security](security.md) | 5 | Manage security roles and their memberships, including listing the roles a Metaverse Object is in |
| [History](history.md) | 4 | Query configuration change history, query deleted objects, and manage change history retention |
| [Example Data](example-data.md) | 6 | Generate sample data for testing and evaluation, and create, update, and remove reusable Example Data Sets |
| [Expressions](expressions.md) | 1 | Test Synchronisation Rule expressions before deployment |
| [Worker Tasks](worker-tasks.md) | 2 | Monitor and cancel in-flight background worker tasks |
| [File System](file-system.md) | 2 | Browse and validate server-side paths when configuring file-based connectors |
| [Logs](logs.md) | 3 | Query, retrieve, and tail JIM service log files for remote troubleshooting |

## Quick Start

```powershell
# Install and connect
Install-Module -Name JIM
Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

# List Connected Systems
Get-JIMConnectedSystem

# Run a full import
Start-JIMRunProfile -ConnectedSystemName "HR System" -RunProfileName "Full Import" -Wait

# Check Metaverse Objects
Get-JIMMetaverseObject -ObjectTypeName "person" -All

# View recent activity
Get-JIMActivity -PageSize 5
```

## Pipeline Support

Most cmdlets accept pipeline input and produce pipeline-friendly output, enabling powerful one-liners:

```powershell
# Execute all "Full Import" Run Profiles across all Connected Systems
Get-JIMConnectedSystem | ForEach-Object {
    Start-JIMRunProfile -ConnectedSystemId $_.Id -RunProfileName "Full Import" -Wait
}

# Find all Synchronisation Rules for a specific Connected System
Get-JIMSyncRule -ConnectedSystemName "HR System"

# Bulk-disable expired API keys
Get-JIMApiKey | Where-Object { $_.ExpiresAt -and $_.ExpiresAt -lt (Get-Date) } |
    ForEach-Object { Set-JIMApiKey -Id $_.Id -Disable }

# Validate all certificates
Get-JIMCertificate | ForEach-Object { Test-JIMCertificate -Id $_.Id }
```

## Output Object Conventions

Cmdlet output objects use **PascalCase** property names, following PowerShell convention, even though JIM's REST API serialises its JSON in camelCase:

```powershell
$mvo = Get-JIMMetaverseObject -Id $id
$mvo.DisplayName        # not $mvo.displayName
$mvo.Type.Name          # nested objects are PascalCase too
```

PowerShell member access is case-insensitive, so a script that reads the wire casing (`$mvo.displayName`) still resolves; but `Get-Member`, `Format-Table`, `ConvertTo-Json` and tab-completion all present the PascalCase names.

**Exception: dictionaries keyed by your data keep their keys exactly as supplied.** A Metaverse Object's `Attributes` map is keyed by attribute name, so those keys follow your schema's casing rather than PascalCase:

The `Attributes` map is only present on the list form, and only carries the attributes you asked for with `-Attributes`; retrieving a single object by `-Id` returns an `AttributeValues` list instead.

```powershell
$person = Get-JIMMetaverseObject -Search "j.smith" -Attributes mail, employeeID |
    Select-Object -First 1
$person.Attributes.mail          # attribute-name keys are verbatim...
$person.Attributes.employeeID    # ...not 'Mail' / 'EmployeeID'
```

The same applies to any other data-keyed map, such as a log entry's `Properties`.

## Confirmation Prompts

Destructive operations (deletions, clears) use PowerShell's `ShouldProcess` mechanism and prompt for confirmation by default. Use `-Force` to suppress prompts in scripts:

```powershell
# Prompts for confirmation
Remove-JIMConnectedSystem -Id 1

# Suppresses confirmation
Remove-JIMConnectedSystem -Id 1 -Force
```

## Further Reading

- [API Authentication](../api/authentication.md): authentication methods and security recommendations
- [API Reference](../api/index.md): REST API documentation that maps to these cmdlets
