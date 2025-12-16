@{
    # Script module or binary module file associated with this manifest.
    RootModule = 'JIM.psm1'

    # Version number of this module.
    ModuleVersion = '0.1.0'

    # Supported PSEditions
    CompatiblePSEditions = @('Core', 'Desktop')

    # ID used to uniquely identify this module
    GUID = 'f7e8d9c0-1234-5678-9abc-def012345678'

    # Author of this module
    Author = 'Tetron'

    # Company or vendor of this module
    CompanyName = 'Tetron'

    # Copyright statement for this module
    Copyright = '(c) Tetron Limited. All rights reserved.'

    # Description of the functionality provided by this module
    Description = 'PowerShell module for administering JIM (Junctional Identity Manager). Provides cmdlets for managing Connected Systems, Sync Rules, Run Profiles, Metaverse Objects, Activities, API Keys, Certificates, and more. Supports both interactive (SSO) and non-interactive (API Key) authentication.'

    # Minimum version of the PowerShell engine required by this module
    PowerShellVersion = '7.0'

    # Functions to export from this module, for best performance, do not use wildcards and do not delete the entry, use an empty array if there are no functions to export.
    FunctionsToExport = @(
        # Connection
        'Connect-JIM',
        'Disconnect-JIM',
        'Test-JIMConnection',

        # Connected Systems
        'Get-JIMConnectedSystem',
        'New-JIMConnectedSystem',
        'Set-JIMConnectedSystem',
        'Remove-JIMConnectedSystem',
        'Import-JIMConnectedSystemSchema',
        'Import-JIMConnectedSystemHierarchy',
        'Get-JIMConnectorDefinition',
        'Set-JIMConnectedSystemObjectType',
        'Set-JIMConnectedSystemAttribute',
        'Get-JIMConnectedSystemPartition',
        'Set-JIMConnectedSystemPartition',
        'Set-JIMConnectedSystemContainer',

        # Sync Rules
        'Get-JIMSyncRule',
        'New-JIMSyncRule',
        'Set-JIMSyncRule',
        'Remove-JIMSyncRule',

        # Sync Rule Mappings
        'Get-JIMSyncRuleMapping',
        'New-JIMSyncRuleMapping',
        'Remove-JIMSyncRuleMapping',

        # Object Matching Rules
        'Get-JIMMatchingRule',
        'New-JIMMatchingRule',
        'Set-JIMMatchingRule',
        'Remove-JIMMatchingRule',

        # Run Profiles
        'Get-JIMRunProfile',
        'New-JIMRunProfile',
        'Set-JIMRunProfile',
        'Remove-JIMRunProfile',
        'Start-JIMRunProfile',

        # Activities
        'Get-JIMActivity',
        'Get-JIMActivityStats',

        # Metaverse
        'Get-JIMMetaverseObject',
        'Get-JIMMetaverseObjectType',
        'Get-JIMMetaverseAttribute',
        'New-JIMMetaverseAttribute',
        'Set-JIMMetaverseAttribute',
        'Remove-JIMMetaverseAttribute',

        # API Keys
        'Get-JIMApiKey',
        'New-JIMApiKey',
        'Set-JIMApiKey',
        'Remove-JIMApiKey',

        # Certificates
        'Get-JIMCertificate',
        'Add-JIMCertificate',
        'Set-JIMCertificate',
        'Remove-JIMCertificate',
        'Test-JIMCertificate',
        'Export-JIMCertificate',

        # Security
        'Get-JIMRole',

        # Data Generation
        'Get-JIMExampleDataSet',
        'Get-JIMDataGenerationTemplate',
        'Invoke-JIMDataGenerationTemplate'
    )

    # Cmdlets to export from this module, for best performance, do not use wildcards and do not delete the entry, use an empty array if there are no cmdlets to export.
    CmdletsToExport = @()

    # Variables to export from this module
    VariablesToExport = @()

    # Aliases to export from this module, for best performance, do not use wildcards and do not delete the entry, use an empty array if there are no aliases to export.
    AliasesToExport = @()

    # Private data to pass to the module specified in RootModule/ModuleToProcess. This may also contain a PSData hashtable with additional module metadata used by PowerShell.
    PrivateData = @{
        PSData = @{
            # Tags applied to this module. These help with module discovery in online galleries.
            Tags = @('Identity', 'IAM', 'IdentityManagement', 'JIM', 'Synchronisation', 'Administration', 'Automation', 'DevOps')

            # A URL to the license for this module.
            LicenseUri = 'https://github.com/TetronIO/JIM/blob/main/LICENSE'

            # A URL to the main website for this project.
            ProjectUri = 'https://github.com/TetronIO/JIM'

            # A URL to an icon representing this module.
            # IconUri = ''

            # ReleaseNotes of this module
            ReleaseNotes = @'
## 0.1.0 - Initial Release

### Connection
- Connect-JIM: Connect to JIM instance with API key authentication
- Disconnect-JIM: Disconnect from JIM instance
- Test-JIMConnection: Test connection to JIM instance

### Connected Systems
- Get-JIMConnectedSystem: Get connected systems (list, by ID, by name)
- New-JIMConnectedSystem: Create a new connected system
- Set-JIMConnectedSystem: Update connected system properties and settings
- Remove-JIMConnectedSystem: Remove a connected system

### Sync Rules
- Get-JIMSyncRule: Get synchronisation rules
- New-JIMSyncRule: Create a new sync rule
- Set-JIMSyncRule: Update sync rule properties
- Remove-JIMSyncRule: Delete a sync rule

### Run Profiles
- Get-JIMRunProfile: Get run profiles for a connected system
- New-JIMRunProfile: Create a new run profile
- Set-JIMRunProfile: Update run profile properties
- Remove-JIMRunProfile: Delete a run profile
- Start-JIMRunProfile: Execute a run profile with optional wait

### Activities
- Get-JIMActivity: Get activities with pagination and filtering
- Get-JIMActivityStats: Get execution statistics for activities

### Metaverse
- Get-JIMMetaverseObject: Get metaverse objects with attribute selection
- Get-JIMMetaverseObjectType: Get metaverse object type definitions
- Get-JIMMetaverseAttribute: Get metaverse attribute definitions

### API Keys
- Get-JIMApiKey: Get API keys
- New-JIMApiKey: Create a new API key
- Set-JIMApiKey: Update an API key
- Remove-JIMApiKey: Delete an API key

### Certificates
- Get-JIMCertificate: Get trusted certificates
- Add-JIMCertificate: Add a certificate to the store
- Set-JIMCertificate: Update certificate properties
- Remove-JIMCertificate: Remove a certificate
- Test-JIMCertificate: Validate a certificate
- Export-JIMCertificate: Download certificate data

### Security
- Get-JIMRole: Get security role definitions

### Data Generation
- Get-JIMExampleDataSet: Get example data sets
- Get-JIMDataGenerationTemplate: Get data generation templates
- Invoke-JIMDataGenerationTemplate: Execute a data generation template

This is an early preview release. For full documentation, see: https://github.com/TetronIO/JIM
'@

            # Prerelease string of this module
            Prerelease = 'alpha'

            # Flag to indicate whether the module requires explicit user acceptance for install/update/save
            RequireLicenseAcceptance = $false
        }
    }

    # HelpInfo URI of this module
    HelpInfoURI = 'https://github.com/TetronIO/JIM/wiki'
}
