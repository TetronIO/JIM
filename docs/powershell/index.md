---
title: PowerShell Module Overview
---

# PowerShell Module

The JIM PowerShell module provides a cross-platform command-line interface for managing and automating JIM operations. It wraps the JIM REST API in idiomatic PowerShell cmdlets, making it straightforward to script identity management workflows.

## Requirements

- **PowerShell 7.0 or later** (cross-platform: Windows, macOS, and Linux)

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

## Connecting to JIM

The module supports two authentication methods: interactive browser-based SSO and API keys.

### Interactive SSO

```powershell
Connect-JIM -Url "https://jim.example.com"
```

This opens a browser window for OIDC authentication. Once authenticated, the session token is used for subsequent commands.

### API Key

```powershell
Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"
```

API keys can be created via the JIM web UI or using the `New-JIMApiKey` cmdlet.

### Verifying the Connection

```powershell
Test-JIMConnection
```

## Capabilities

The JIM PowerShell module provides cmdlets for managing all core JIM resources, including:

- **Connected Systems:** create, configure, and manage connected systems and their schemas
- **Sync Rules:** define and manage synchronisation rules between connected systems and the metaverse
- **Metaverse:** query and manage metaverse objects and attributes
- **Run Profiles:** create and execute import, export, and synchronisation run profiles
- **Activities:** monitor activity history and results
- **API Keys:** create, list, and revoke API keys
- **Certificates:** manage certificates used for secure connector communication
- **Example Data:** generate sample data for testing and evaluation

## Quick Start

```powershell
# Install and connect
Install-Module -Name JIM
Connect-JIM -Url "https://jim.example.com" -ApiKey "jim_xxxxxxxxxxxx"

# Verify connection
Test-JIMConnection

# List connected systems
Get-JIMConnectedSystem

# Get metaverse objects
Get-JIMMetaverseObject -ObjectType "person"
```

## Further Reading

- [Cmdlet Reference](cmdlets.md): detailed documentation for all available cmdlets
- [API Authentication](../api/authentication.md): authentication methods and security recommendations
- [API Overview](../api/index.md): REST API documentation
