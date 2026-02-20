function Get-JIMActivity {
    <#
    .SYNOPSIS
        Gets Activities from JIM.

    .DESCRIPTION
        Retrieves activity history from JIM. Activities track all operations performed
        in JIM, including sync runs, data generation, certificate management, and other
        administrative actions.

    .PARAMETER Id
        The unique identifier (GUID) of a specific Activity to retrieve.

    .PARAMETER Search
        Search query to filter activities by target name or type.

    .PARAMETER Page
        Page number for pagination. Defaults to 1.

    .PARAMETER PageSize
        Number of items per page. Defaults to 20.

    .PARAMETER ExecutionItems
        If specified with -Id, retrieves the execution items for a run profile activity.
        Items are streamed individually for pipeline support.

    .OUTPUTS
        PSCustomObject representing Activity/Activities or execution items.

    .EXAMPLE
        Get-JIMActivity

        Gets the most recent activities.

    .EXAMPLE
        Get-JIMActivity -Page 2 -PageSize 50

        Gets page 2 of activities with 50 items per page.

    .EXAMPLE
        Get-JIMActivity -Search "Full Import"

        Gets activities matching "Full Import".

    .EXAMPLE
        Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890"

        Gets a specific activity by ID.

    .EXAMPLE
        Get-JIMActivity -Id "a1b2c3d4-e5f6-7890-abcd-ef1234567890" -ExecutionItems

        Gets the execution items for a specific run profile activity.

    .LINK
        Get-JIMActivityStats
        Start-JIMRunProfile
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [Parameter(Mandatory, ParameterSetName = 'ExecutionItems', ValueFromPipelineByPropertyName)]
        [guid]$Id,

        [Parameter(ParameterSetName = 'List')]
        [string]$Search,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, 100)]
        [int]$PageSize = 20,

        [Parameter(Mandatory, ParameterSetName = 'ExecutionItems')]
        [switch]$ExecutionItems
    )

    process {
        switch ($PSCmdlet.ParameterSetName) {
            'ById' {
                Write-Verbose "Getting Activity with ID: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/activities/$Id"
                $result
            }

            'ExecutionItems' {
                Write-Verbose "Getting execution items for Activity ID: $Id"
                $currentPage = 1
                $hasMore = $true

                while ($hasMore) {
                    $response = Invoke-JIMApi -Endpoint "/api/v1/activities/$Id/items?page=$currentPage&pageSize=100"

                    # Stream items individually
                    foreach ($item in $response.items) {
                        $item
                    }

                    # Check if there are more pages
                    $hasMore = ($response.items.Count -eq 100) -and ($currentPage * 100 -lt $response.totalCount)
                    $currentPage++
                }
            }

            'List' {
                Write-Verbose "Getting Activities (Page: $Page, PageSize: $PageSize)"

                $endpoint = "/api/v1/activities?page=$Page&pageSize=$PageSize"
                if ($Search) {
                    $encodedSearch = [System.Uri]::EscapeDataString($Search)
                    $endpoint += "&search=$encodedSearch"
                }

                $response = Invoke-JIMApi -Endpoint $endpoint

                # Output each activity individually for pipeline support
                foreach ($activity in $response.items) {
                    $activity
                }

                # Add pagination info to verbose output
                Write-Verbose "Returned $($response.items.Count) of $($response.totalCount) total activities"
            }
        }
    }
}
