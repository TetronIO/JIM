function Get-JIMDataGenerationTemplate {
    <#
    .SYNOPSIS
        Gets data generation templates from JIM.

    .DESCRIPTION
        Retrieves data generation templates that define how test data should be
        generated, including which object types to create and how many instances
        of each.

    .PARAMETER Id
        The unique identifier of a specific template to retrieve.

    .PARAMETER Name
        The name of a specific template to retrieve.

    .PARAMETER Page
        Page number for paginated results. Defaults to 1.

    .PARAMETER PageSize
        Number of items per page. Defaults to 100.

    .OUTPUTS
        PSCustomObject representing template(s).

    .EXAMPLE
        Get-JIMDataGenerationTemplate

        Gets all data generation templates.

    .EXAMPLE
        Get-JIMDataGenerationTemplate -Id 1

        Gets a specific template by ID.

    .EXAMPLE
        Get-JIMDataGenerationTemplate -Name 'Test Users'

        Gets a specific template by name.

    .EXAMPLE
        Get-JIMDataGenerationTemplate | Select-Object Id, Name, Description

        Gets all templates with specific properties.

    .LINK
        Get-JIMExampleDataSet
        Invoke-JIMDataGenerationTemplate
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, [int]::MaxValue)]
        [int]$Page = 1,

        [Parameter(ParameterSetName = 'List')]
        [ValidateRange(1, 1000)]
        [int]$PageSize = 100
    )

    process {
        # Resolve name to ID if using ByName parameter set
        if ($PSCmdlet.ParameterSetName -eq 'ByName') {
            try {
                $resolvedTemplate = Resolve-JIMDataGenerationTemplate -Name $Name
                $Id = $resolvedTemplate.id
            }
            catch {
                Write-Error $_
                return
            }
        }

        switch ($PSCmdlet.ParameterSetName) {
            { $_ -in 'ById', 'ByName' } {
                Write-Verbose "Getting data generation template with ID: $Id"
                $result = Invoke-JIMApi -Endpoint "/api/v1/data-generation/templates/$Id"
                $result
            }

            'List' {
                Write-Verbose "Getting all data generation templates"

                $queryParams = @(
                    "page=$Page",
                    "pageSize=$PageSize"
                )
                $queryString = $queryParams -join '&'

                $response = Invoke-JIMApi -Endpoint "/api/v1/data-generation/templates?$queryString"

                # Handle paginated response
                $templates = if ($response.items) { $response.items } else { $response }

                # Output each template individually for pipeline support
                foreach ($template in $templates) {
                    $template
                }
            }
        }
    }
}
