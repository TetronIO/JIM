@{
    # Script module or binary module file associated with this manifest.
    RootModule = 'JIM.psm1'

    # Version number of this module.
    ModuleVersion = '0.3.0'

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

        # Schedules
        'Get-JIMSchedule',
        'New-JIMSchedule',
        'Set-JIMSchedule',
        'Remove-JIMSchedule',
        'Enable-JIMSchedule',
        'Disable-JIMSchedule',
        'Start-JIMSchedule',
        'Add-JIMScheduleStep',
        'Remove-JIMScheduleStep',

        # Schedule Executions
        'Get-JIMScheduleExecution',
        'Stop-JIMScheduleExecution',

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
## 0.3.0

### New Features
- Scheduler service with cron and interval-based triggers, multi-step execution, and full management UI
- Change history with full audit trail, timeline UI, and configurable retention
- Real-time progress indication for running operations
- Dashboard redesign with informative cards and version display
- Interactive browser-based authentication for PowerShell module
- API key authentication for sync endpoints
- LDAP schema discovery enhancements (writability detection, omSyntax 66 support)
- Split and Join functions for multi-valued attribute transforms

### Performance
- Raw SQL for import/export bulk writes (replacing EF Core)
- Parallel batch export processing and reference resolution
- LDAP async pipelining with configurable concurrency
- CSO lookup index eliminating N+1 import queries
- Worker heartbeat-based crash recovery

### Module Changes
- 75 cmdlets (11 new scheduler cmdlets)
- Flattened directory structure
- Server version display on Connect-JIM

For full changelog, see: https://github.com/TetronIO/JIM/blob/main/CHANGELOG.md
'@

            # Prerelease string of this module
            # Prerelease = ''

            # Flag to indicate whether the module requires explicit user acceptance for install/update/save
            RequireLicenseAcceptance = $false
        }
    }

    # HelpInfo URI of this module
    HelpInfoURI = 'https://github.com/TetronIO/JIM/wiki'
}
