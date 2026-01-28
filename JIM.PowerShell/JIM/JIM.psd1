@{
    # Script module or binary module file associated with this manifest.
    RootModule = 'JIM.psm1'

    # Version number of this module.
    ModuleVersion = '0.2.0'

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

        # Scoping Criteria
        'Get-JIMScopingCriteria',
        'New-JIMScopingCriteriaGroup',
        'Set-JIMScopingCriteriaGroup',
        'Remove-JIMScopingCriteriaGroup',
        'New-JIMScopingCriterion',
        'Remove-JIMScopingCriterion',

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
        'Set-JIMMetaverseObjectType',
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
        'Invoke-JIMDataGenerationTemplate',

        # Expressions
        'Test-JIMExpression',

        # History
        'Get-JIMDeletedObject',
        'Get-JIMHistoryCount',
        'Invoke-JIMHistoryCleanup'
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
## 0.2.0 - Full Management Cmdlets

Major expansion from 3 to 64 cmdlets, providing complete administrative coverage of JIM.

### New in 0.2.0

#### Connected Systems (12 cmdlets)
- Full CRUD for connected systems
- Schema import and attribute/object type configuration
- Hierarchy import with partition and container management
- Connector definition discovery

#### Sync Rules (4 cmdlets)
- Full CRUD for synchronisation rules

#### Sync Rule Mappings (3 cmdlets)
- Mapping management with expression support

#### Object Matching Rules (4 cmdlets)
- Full CRUD for object matching rules with reordering

#### Scoping Criteria (5 cmdlets)
- Group and criterion management for sync rule scoping

#### Run Profiles (5 cmdlets)
- Full CRUD plus execution with real-time progress tracking

#### Metaverse (7 cmdlets)
- Object and object type queries
- Full attribute CRUD (create, update, remove)
- MVO deletion rule configuration

#### Activities (2 cmdlets)
- Activity queries with pagination and filtering
- Execution statistics

#### API Keys (4 cmdlets)
- Full CRUD for API key management

#### Certificates (6 cmdlets)
- Full CRUD plus validation and export

#### Security (1 cmdlet)
- Role definitions

#### Data Generation (3 cmdlets)
- Example data sets, templates, and execution

#### Expressions (1 cmdlet)
- Expression testing and validation

#### History (3 cmdlets)
- Deleted object queries, history counts, and cleanup

#### General
- Name-based parameter alternatives for all cmdlets
- Consistent error handling and pagination support

For full documentation, see: https://github.com/TetronIO/JIM
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
