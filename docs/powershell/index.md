---
title: PowerShell Module
description: "How to install the JIM PowerShell module, connect it to JIM, and the conventions its cmdlets share: pipelines, output and confirmation."
type: reference
audience: automation-author
---

# PowerShell Module

The JIM PowerShell module provides a cross-platform command-line interface for managing and automating JIM operations. It wraps the JIM REST API in idiomatic PowerShell cmdlets with full pipeline support, so identity management workflows can be scripted.

## :material-clipboard-list-outline: Requirements

- **[PowerShell 7.0 or later](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell)** (cross-platform: Windows, macOS, and Linux)

## :material-download-outline: Installation

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

## :material-shield-key-outline: Authentication

The module supports two authentication methods: interactive browser-based SSO and API keys.

### Interactive SSO

```powershell
Connect-JIM -Url "https://jim.example.com"
```

This opens a browser window for OIDC authentication. Once authenticated, the session token is cached and automatically refreshed.

### API Key

```powershell
Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_ak_xxxxxxxxxxxx"
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

## :material-view-grid-outline: Cmdlet Categories

| Category | Cmdlets | Description |
|----------|---------|-------------|
| [System](system.md) | 6 | Health checks, Service Health of the Worker and Scheduler, version, auth config, user info, and system reset |
| [Connection](connection.md) | 3 | Connect, disconnect, and test JIM sessions |
| [Connected Systems](connected-systems.md) | 7 | Create, configure, clear and delete Connected Systems, and preview what a deletion removes |
| [Connectors](connectors.md) | 1 | List the Connectors a Connected System can be created from, and read one Connector's settings and capabilities |
| [Connected System Schema](connected-system-schema.md) | 4 | Import a Connected System's schema, and choose which Object Types and attributes JIM manages |
| [Auxiliary Object Classes](connected-system-auxiliary-classes.md) | 5 | Choose the auxiliary classes an LDAP Connected System's Object Types carry, and discover which are in use |
| [Partitions and Containers](partitions-and-containers.md) | 7 | Import a Connected System's hierarchy, and choose which partitions and containers JIM imports from |
| [Connected System Objects](connected-system-objects.md) | 7 | Read the Connected System Objects in a Connector Space, their history and unresolved references, and set their passwords |
| [Pending Exports](pending-exports.md) | 1 | Read the Pending Exports waiting to be written to a Connected System |
| [Run Profiles](run-profiles.md) | 5 | Create and execute import, sync, and export operations |
| [Synchronisation Rules](synchronisation-rules.md) | 4 | List, create, change and delete Synchronisation Rules |
| [Attribute Mappings](attribute-mappings.md) | 4 | List, create, change and delete a Synchronisation Rule's Attribute Flows, including generated values |
| [Scoping Criteria](scoping-criteria.md) | 7 | Decide which objects a Synchronisation Rule processes, with groups of criteria |
| [Object Matching Rules](object-matching-rules.md) | 9 | Switch a Connected System's matching mode, and manage its Object Matching Rules |
| [Initial Password](initial-password.md) | 2 | Read and change the initial password a Synchronisation Rule sets on the Connected System Objects it provisions |
| [Metaverse](metaverse.md) | 6 | Find and read Metaverse Objects, explain their values and connections, and review pending deletions |
| [Metaverse Object Types](metaverse-object-types.md) | 4 | List, create, change and delete Object Types, and set their deletion rules |
| [Metaverse Attributes](metaverse-attributes.md) | 11 | Manage attributes, bind them to Object Types, and set Attribute Priority |
| [Password Synchronisation](password-synchronisation.md) | 4 | Set a Metaverse Object's password, and choose which Connected Systems receive synchronised passwords |
| [Password Queue](password-queue.md) | 3 | List, retry and cancel queued password changes |
| [Generated Values](generated-values.md) | 7 | List the values JIM generated and retired, read and restart a Sequence's counter, and decide on values a target rejected |
| [Predefined Searches](predefined-searches.md) | 9 | List and toggle the searches that drive portal list views and the fast search API, and manage their filter criteria (groups and criteria) |
| [Schedules](schedules.md) | 9 | Automate synchronisation workflows with scheduled execution, and set what each Schedule and step does when a step fails |
| [Schedule Executions](schedule-executions.md) | 3 | Run a Schedule now, and watch or stop its runs |
| [Activities](activities.md) | 3 | Monitor operation history, a run's statistics, and child Activities |
| [Execution Items](execution-items.md) | 2 | Read what happened to one object in a run, what caused it, and what it went on to cause |
| [API Keys](api-keys.md) | 4 | Create, manage, and revoke API keys |
| [Certificates](certificates.md) | 8 | Manage trusted certificates for connector authentication, and approve the certificate a Connected System's server presents |
| [Identity Providers](identity-providers.md) | 1 | Register JIM with its identity provider, and repair the registration |
| [Service Settings](service-settings.md) | 3 | View and modify runtime configuration |
| [Feature Flags](feature-flags.md) | 3 | View and toggle Preview features |
| [Security](security.md) | 5 | Manage security roles and their memberships, including listing the roles a Metaverse Object is in |
| [History](history.md) | 4 | Query Configuration Change History, query deleted objects, and manage change history retention |
| [Previews](previews.md) | 4 | Preview a configuration change before saving it |
| [Sync Preview](sync-preview.md) | 2 | Preview what synchronising one object would do |
| [Example Data](example-data.md) | 10 | Generate sample data for testing and evaluation, create, update, and remove reusable Example Data Sets, and build Data Generation Templates including their per-attribute generation configuration |
| [Expressions](expressions.md) | 1 | Test Synchronisation Rule expressions before deployment |
| [Worker Tasks](worker-tasks.md) | 2 | Monitor and cancel in-flight background worker tasks |
| [File System](file-system.md) | 2 | Browse and validate server-side paths when configuring file-based connectors |
| [Logs](logs.md) | 3 | Query, retrieve, and tail JIM service log files for remote troubleshooting |
| [Data Flow](data-flow.md) | 1 | List every configured attribute data flow across all Connected Systems (`Get-JIMDataFlow`) |

## :material-rocket-launch-outline: Quick Start

```powershell
# Install and connect
Install-Module -Name JIM
Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_ak_xxxxxxxxxxxx"

# List Connected Systems
Get-JIMConnectedSystem

# Run a full import
Start-JIMRunProfile -ConnectedSystemName "HR System" -RunProfileName "Full Import" -Wait

# Check Metaverse Objects
Get-JIMMetaverseObject -ObjectTypeName "person" -All

# View recent activity
Get-JIMActivity -PageSize 5
```

## :material-pipe: Pipeline Support

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

## :material-code-braces: Output Object Conventions

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

## :material-help-circle-outline: Confirmation Prompts

Destructive operations (deletions, clears) use PowerShell's `ShouldProcess` mechanism and prompt for confirmation by default. Use `-Force` to suppress prompts in scripts:

```powershell
# Prompts for confirmation
Remove-JIMConnectedSystem -Id 1

# Suppresses confirmation
Remove-JIMConnectedSystem -Id 1 -Force
```

## :material-book-open-variant-outline: Further Reading

- [API Authentication](../api/authentication.md): authentication methods and security recommendations
- [API Reference](../api/index.md): REST API documentation that maps to these cmdlets
