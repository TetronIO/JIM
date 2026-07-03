# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConfigurationChangeHistory {
    <#
    .SYNOPSIS
        Gets the configuration change history for a Synchronisation Rule, Connected System, or Schedule in JIM.

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
        The kind of configuration object: 'SynchronisationRule', 'ConnectedSystem', or 'Schedule'.

    .PARAMETER Id
        The unique identifier of the configuration object: an integer for a Synchronisation Rule or Connected
        System, or a GUID for a Schedule. Accepts the 'id' property from the pipeline, so a piped
        Synchronisation Rule, Connected System, or Schedule binds automatically.

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
        [ValidateSet('SynchronisationRule', 'ConnectedSystem', 'Schedule')]
        [string]$Type,

        # A string rather than [int] so it can carry either an integer (Synchronisation Rule / Connected System)
        # or a GUID (Schedule); validated per -Type in the process block.
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
        # Synchronisation Rules and Connected Systems are integer-keyed, Schedules are GUID-keyed.
        if ($Type -eq 'Schedule') {
            $parsedGuid = [Guid]::Empty
            if (-not [Guid]::TryParse($Id, [ref]$parsedGuid)) {
                Write-Error "For -Type Schedule, -Id must be a GUID (Schedules are GUID-keyed). Got: '$Id'."
                return
            }
            $base = "/api/v1/schedules/$Id/change-history"
        }
        else {
            $parsedInt = 0
            if (-not [int]::TryParse($Id, [ref]$parsedInt)) {
                Write-Error "For -Type $Type, -Id must be an integer. Got: '$Id'."
                return
            }
            $segment = if ($Type -eq 'SynchronisationRule') { 'sync-rules' } else { 'connected-systems' }
            $base = "/api/v1/synchronisation/$segment/$Id/change-history"
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
