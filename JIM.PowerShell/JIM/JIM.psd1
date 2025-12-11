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
        'Remove-JIMConnectedSystem',

        # Sync Rules
        'Get-JIMSyncRule',

        # Run Profiles
        'Get-JIMRunProfile',
        'Start-JIMRunProfile',

        # Activities
        'Get-JIMActivity',
        'Get-JIMActivityStats'
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
- Connect-JIM: Connect to JIM instance with API key authentication
- Disconnect-JIM: Disconnect from JIM instance
- Test-JIMConnection: Test connection to JIM instance

This is an early preview release. More cmdlets will be added in future releases.

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
