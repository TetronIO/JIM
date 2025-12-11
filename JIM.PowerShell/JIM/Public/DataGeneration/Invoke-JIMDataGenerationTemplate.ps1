function Invoke-JIMDataGenerationTemplate {
    <#
    .SYNOPSIS
        Executes a data generation template to create test data.

    .DESCRIPTION
        Executes a data generation template to create identity objects in the
        Metaverse according to the template configuration. The operation runs
        asynchronously on the server.

    .PARAMETER Id
        The unique identifier of the template to execute.

    .PARAMETER Wait
        If specified, waits for the operation to complete before returning.
        Note: The API returns 202 Accepted immediately; this parameter will
        poll for completion if supported.

    .PARAMETER PassThru
        If specified, returns information about the execution.

    .OUTPUTS
        If -PassThru is specified, returns execution information.

    .EXAMPLE
        Invoke-JIMDataGenerationTemplate -Id 1

        Executes the data generation template with ID 1.

    .EXAMPLE
        Get-JIMDataGenerationTemplate | Where-Object { $_.name -eq "Test Users" } | Invoke-JIMDataGenerationTemplate

        Executes a template by name.

    .EXAMPLE
        Invoke-JIMDataGenerationTemplate -Id 1 -PassThru

        Executes the template and returns execution information.

    .LINK
        Get-JIMDataGenerationTemplate
        Get-JIMExampleDataSet
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$Id,

        [switch]$Wait,

        [switch]$PassThru
    )

    process {
        if ($PSCmdlet.ShouldProcess("Template ID: $Id", "Execute Data Generation Template")) {
            Write-Verbose "Executing data generation template: $Id"

            try {
                $response = Invoke-JIMApi -Endpoint "/api/v1/data-generation/templates/$Id/execute" -Method 'POST'

                Write-Verbose "Data generation template execution started"

                if ($Wait) {
                    Write-Verbose "Waiting for data generation to complete..."
                    # The API returns 202 Accepted - we could poll for completion
                    # but currently the API doesn't provide a status endpoint
                    Write-Warning "The -Wait parameter is not fully implemented. The operation has been started but completion status cannot be determined."
                }

                if ($PassThru) {
                    [PSCustomObject]@{
                        TemplateId = $Id
                        Status = 'Started'
                        Message = 'Data generation template execution has been started.'
                    }
                }
            }
            catch {
                Write-Error "Failed to execute data generation template: $_"
            }
        }
    }
}
