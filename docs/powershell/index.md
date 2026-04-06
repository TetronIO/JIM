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
| [Connection](connection.md) | 3 | Connect, disconnect, and test JIM sessions |
| [Connected Systems](connected-systems.md) | 19 | Manage connected systems, schemas, partitions, and connector space objects |
| [Run Profiles](run-profiles.md) | 5 | Create and execute import, sync, and export operations |
| [Sync Rules](sync-rules.md) | 17 | Define attribute mappings, scoping criteria, and object matching rules |
| [Metaverse](metaverse.md) | 8 | Query objects, manage schema types and attributes, review pending deletions |
| [Schedules](schedules.md) | 11 | Automate synchronisation workflows with scheduled execution |
| [Activities](activities.md) | 3 | Monitor operation history, statistics, and execution items |
| [API Keys](api-keys.md) | 4 | Create, manage, and revoke API keys |
| [Certificates](certificates.md) | 6 | Manage trusted certificates for connector authentication |
| [Service Settings](service-settings.md) | 3 | View and modify runtime configuration |
| [Security](security.md) | 1 | Query role definitions |
| [History](history.md) | 3 | Query deleted objects and manage change history retention |
| [Example Data](example-data.md) | 3 | Generate sample data for testing and evaluation |
| [Expressions](expressions.md) | 1 | Test sync rule expressions before deployment |

## Quick Start

```powershell
# Install and connect
Install-Module -Name JIM
Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

# List connected systems
Get-JIMConnectedSystem

# Run a full import
Start-JIMRunProfile -ConnectedSystemName "HR System" -RunProfileName "Full Import" -Wait

# Check metaverse objects
Get-JIMMetaverseObject -ObjectTypeName "person" -All

# View recent activity
Get-JIMActivity -PageSize 5
```

## Pipeline Support

Most cmdlets accept pipeline input and produce pipeline-friendly output, enabling powerful one-liners:

```powershell
# Execute all "Full Import" run profiles across all connected systems
Get-JIMConnectedSystem | ForEach-Object {
    Start-JIMRunProfile -ConnectedSystemId $_.id -RunProfileName "Full Import" -Wait
}

# Find all sync rules for a specific connected system
Get-JIMSyncRule -ConnectedSystemName "HR System"

# Bulk-disable expired API keys
Get-JIMApiKey | Where-Object { $_.expiresAt -and $_.expiresAt -lt (Get-Date) } |
    ForEach-Object { Set-JIMApiKey -Id $_.id -Disable }

# Validate all certificates
Get-JIMCertificate | ForEach-Object { Test-JIMCertificate -Id $_.id }
```

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
