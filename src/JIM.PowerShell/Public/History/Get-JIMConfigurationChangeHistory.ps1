# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConfigurationChangeHistory {
    <#
    .SYNOPSIS
        Gets the configuration change history for a Synchronisation Rule, Connected System, Schedule, Service
        Setting, Metaverse Object Type, Metaverse Attribute, Trusted Certificate, API Key, Role, or Predefined
        Search in JIM.

    .DESCRIPTION
        Retrieves the recorded configuration changes for a configuration object. Three modes are supported:

        - List (default): a paginated summary of changes, newest version first, each with who changed it,
          when, the optional reason, and a one-line summary of what changed. Use -All to page automatically.
        - Single version (-Version): the full change at that version, including the redacted snapshot and the
          structured diff against the previous version. Add -AsDiff to render a git-style coloured diff.
        - Compare (-CompareFrom / -CompareTo): the structured diff between any two versions. Add -AsDiff to
          render a git-style coloured diff.

        Secret values (for example encrypted Connected System settings) are never returned; a changed secret
        is reported only as "changed", never by value.

    .PARAMETER Type
        The kind of configuration object: 'SynchronisationRule', 'ConnectedSystem', 'Schedule',
        'ServiceSetting', 'MetaverseObjectType', 'MetaverseAttribute', 'TrustedCertificate', 'ApiKey', 'Role',
        or 'PredefinedSearch'.

    .PARAMETER Id
        The unique identifier of the configuration object: an integer for a Synchronisation Rule, Connected
        System, Metaverse Object Type, Metaverse Attribute, Role, or Predefined Search, a GUID for a Schedule,
        Trusted Certificate, or API Key, or the dot-notation setting key for a Service Setting (e.g.
        "History.RetentionPeriod"). Accepts the 'id' property from the pipeline, so a piped object binds
        automatically.

    .PARAMETER All
        Automatically paginate through all change-history entries and return every row. Cannot be used with -Page.

    .PARAMETER Page
        Page number for the change-history list. Defaults to 1. Cannot be used with -All.

    .PARAMETER PageSize
        Number of items per page for the change-history list. Defaults to 50. Maximum is 100.

    .PARAMETER Version
        Retrieve a single change by its per-object version number, returning the snapshot and the diff against
        the previous version.

    .PARAMETER CompareFrom
        The earlier version to compare from (used with -CompareTo).

    .PARAMETER CompareTo
        The later version to compare to (used with -CompareFrom).

    .PARAMETER AsDiff
        For -Version or -CompareFrom/-CompareTo, render the change as a git-style coloured unified diff instead
        of returning the structured object.

    .PARAMETER Raw
        For -Version or -CompareFrom/-CompareTo, return the underlying structured change object. This is the
        default behaviour; the switch is provided for explicitness.

    .OUTPUTS
        PSCustomObject change-history record(s) or change detail, or, with -AsDiff, the rendered diff as strings.

    .EXAMPLE
        Get-JIMConfigurationChangeHistory -Type SynchronisationRule -Id 5

        Lists the most recent configuration changes for Synchronisation Rule 5.

    .EXAMPLE
        Get-JIMConfigurationChangeHistory -Type ConnectedSystem -Id 9 -All

        Returns every recorded configuration change for Connected System 9.

    .EXAMPLE
        Get-JIMConfigurationChangeHistory -Type ConnectedSystem -Id 9 -Version 7 -AsDiff

        Shows version 7's change as a git-style coloured diff against version 6.

    .EXAMPLE
        Get-JIMSyncRule -Name "HR Inbound" | Get-JIMConfigurationChangeHistory -Type SynchronisationRule -Version 7 -AsDiff

        Pipes a Synchronisation Rule in (binding its id) and shows version 7 as a coloured diff.

    .EXAMPLE
        Get-JIMConfigurationChangeHistory -Type SynchronisationRule -Id 5 -CompareFrom 6 -CompareTo 8 -AsDiff

        Compares versions 6 and 8 of Synchronisation Rule 5 as a coloured diff.

    .EXAMPLE
        Get-JIMSchedule -Name "Nightly Sync" | Get-JIMConfigurationChangeHistory -Type Schedule

        Pipes a Schedule in (binding its GUID id) and lists its recorded configuration changes.

    .EXAMPLE
        Get-JIMConfigurationChangeHistory -Type ServiceSetting -Id 'History.RetentionPeriod'

        Lists the recorded configuration changes for the history retention period Service Setting.

    .EXAMPLE
        Get-JIMMetaverseAttribute -Name 'Email' | Get-JIMConfigurationChangeHistory -Type MetaverseAttribute

        Pipes a Metaverse Attribute in (binding its id) and lists its recorded configuration changes.

    .EXAMPLE
        Get-JIMCertificate | Get-JIMConfigurationChangeHistory -Type TrustedCertificate

        Pipes a Trusted Certificate in (binding its GUID id) and lists its recorded configuration changes.

    .EXAMPLE
        Get-JIMApiKey | Get-JIMConfigurationChangeHistory -Type ApiKey

        Pipes an API Key in (binding its GUID id) and lists its recorded configuration changes.

    .EXAMPLE
        Get-JIMRole -Name "Administrator" | Get-JIMConfigurationChangeHistory -Type Role

        Pipes a Role in (binding its integer id) and lists its recorded configuration changes, covering both
        its definition and every membership change.

    .EXAMPLE
        Get-JIMPredefinedSearch -Uri 'people' | Get-JIMConfigurationChangeHistory -Type PredefinedSearch

        Pipes a Predefined Search in (binding its integer id) and lists its recorded configuration changes,
        covering its own definition as well as every criteria-group and criterion change.

    .LINK
        Get-JIMSyncRule

    .LINK
        Get-JIMConnectedSystem

    .LINK
        Get-JIMActivity
    #>
    [CmdletBinding(DefaultParameterSetName = 'Page')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('SynchronisationRule', 'ConnectedSystem', 'Schedule', 'ServiceSetting', 'MetaverseObjectType', 'MetaverseAttribute', 'TrustedCertificate', 'ApiKey', 'Role', 'PredefinedSearch')]
        [string]$Type,

        # A string rather than [int] so it can carry an integer (Synchronisation Rule / Connected System), a GUID
        # (Schedule), or a setting key (Service Setting); validated per -Type in the process block.
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [ValidateNotNullOrEmpty()]
        [string]$Id,

        [Parameter(Mandatory, ParameterSetName = 'All')]
        [switch]$All,

        [Parameter(ParameterSetName = 'Page')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'Page')]
        [Parameter(ParameterSetName = 'All')]
        [ValidateRange(1, 100)]
        [int]$PageSize = 50,

        [Parameter(Mandatory, ParameterSetName = 'Version')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Version,

        [Parameter(Mandatory, ParameterSetName = 'Compare')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$CompareFrom,

        [Parameter(Mandatory, ParameterSetName = 'Compare')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$CompareTo,

        [Parameter(ParameterSetName = 'Version')]
        [Parameter(ParameterSetName = 'Compare')]
        [switch]$AsDiff,

        [Parameter(ParameterSetName = 'Version')]
        [Parameter(ParameterSetName = 'Compare')]
        [switch]$Raw
    )

    process {
        # Validate the id shape per object type before anything else (so a bad id fails fast, even offline):
        # Synchronisation Rules, Connected Systems, Metaverse Object Types, Metaverse Attributes, Roles and
        # Predefined Searches are integer-keyed, Schedules, Trusted Certificates and API Keys are GUID-keyed,
        # and Service Settings are keyed by their dot-notation setting key.
        if ($Type -in 'Schedule', 'TrustedCertificate', 'ApiKey') {
            $parsedGuid = [Guid]::Empty
            if (-not [Guid]::TryParse($Id, [ref]$parsedGuid)) {
                Write-Error "For -Type $Type, -Id must be a GUID ($($Type)s are GUID-keyed). Got: '$Id'."
                return
            }
            $base = switch ($Type) {
                'Schedule' { "/api/v1/schedules/$Id/change-history" }
                'TrustedCertificate' { "/api/v1/certificates/$Id/change-history" }
                'ApiKey' { "/api/v1/apikeys/$Id/change-history" }
            }
        }
        elseif ($Type -eq 'ServiceSetting') {
            # Any non-empty string is a candidate key; escape it for the URL path.
            $base = "/api/v1/service-settings/$([uri]::EscapeDataString($Id))/change-history"
        }
        else {
            $parsedInt = 0
            if (-not [int]::TryParse($Id, [ref]$parsedInt)) {
                Write-Error "For -Type $Type, -Id must be an integer. Got: '$Id'."
                return
            }
            $base = switch ($Type) {
                'SynchronisationRule' { "/api/v1/synchronisation/sync-rules/$Id/change-history" }
                'ConnectedSystem' { "/api/v1/synchronisation/connected-systems/$Id/change-history" }
                'MetaverseObjectType' { "/api/v1/metaverse/object-types/$Id/change-history" }
                'MetaverseAttribute' { "/api/v1/metaverse/attributes/$Id/change-history" }
                'Role' { "/api/v1/security/roles/$Id/change-history" }
                'PredefinedSearch' { "/api/v1/predefined-searches/$Id/change-history" }
            }
        }

        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        switch ($PSCmdlet.ParameterSetName) {
            'Version' {
                Write-Verbose "Getting $Type $Id configuration change version $Version"
                $detail = Invoke-JIMApi -Endpoint "$base/$Version"
                if ($AsDiff) {
                    Format-JIMConfigurationDiff -Diff $detail.diff
                }
                else {
                    $detail
                }
            }
            'Compare' {
                Write-Verbose "Comparing $Type $Id configuration versions $CompareFrom and $CompareTo"
                $diff = Invoke-JIMApi -Endpoint "${base}/compare?fromVersion=$CompareFrom&toVersion=$CompareTo"
                if ($AsDiff) {
                    Format-JIMConfigurationDiff -Diff $diff
                }
                else {
                    $diff
                }
            }
            default {
                # 'Page' or 'All': the change-history list.
                $currentPage = if ($All) { 1 } else { $Page }
                do {
                    Write-Verbose "Getting $Type $Id configuration change history (Page: $currentPage, PageSize: $PageSize)"
                    $response = Invoke-JIMApi -Endpoint "${base}?page=$currentPage&pageSize=$PageSize"

                    foreach ($item in $response.items) {
                        $item
                    }

                    $hasMore = $All -and $response.hasNextPage -eq $true
                    if ($hasMore) {
                        $currentPage++
                    }
                } while ($hasMore)
            }
        }
    }
}
