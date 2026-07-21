# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Invoke-JIMExampleDataTemplate {
    <#
    .SYNOPSIS
        Executes a data generation template to create test data.

    .DESCRIPTION
        Executes a data generation template to create identity objects in the
        Metaverse according to the template configuration. Execution is
        asynchronous: this cmdlet returns as soon as the server has accepted
        the request, before data generation has finished. Monitor progress and
        completion via Activities (Get-JIMActivity).

    .PARAMETER Id
        The unique identifier of the template to execute.

    .PARAMETER Name
        The name of the template to execute.

    .PARAMETER PassThru
        If specified, returns information about the execution.

    .OUTPUTS
        If -PassThru is specified, returns execution information.

    .EXAMPLE
        Invoke-JIMExampleDataTemplate -Id 1

        Executes the data generation template with ID 1.

    .EXAMPLE
        Invoke-JIMExampleDataTemplate -Name 'Test Users'

        Executes the data generation template named 'Test Users'.

    .EXAMPLE
        Get-JIMExampleDataTemplate | Where-Object { $_.name -eq "Test Users" } | Invoke-JIMExampleDataTemplate

        Executes a template from the pipeline.

    .EXAMPLE
        Invoke-JIMExampleDataTemplate -Id 1 -PassThru

        Executes the template and returns execution information.

    .LINK
        Get-JIMExampleDataTemplate
        Get-JIMExampleDataSet
        Get-JIMActivity
    #>
    [CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Resolve name to ID if using ByName parameter set
        if ($PSCmdlet.ParameterSetName -eq 'ByName') {
            try {
                $resolvedTemplate = Resolve-JIMExampleDataTemplate -Name $Name
                $Id = $resolvedTemplate.id
            }
            catch {
                Write-Error $_
                return
            }
        }

        $displayName = if ($Name) { $Name } else { "Template ID: $Id" }

        if ($PSCmdlet.ShouldProcess($displayName, "Execute Data Generation Template")) {
            Write-Verbose "Executing data generation template: $Id"

            try {
                $response = Invoke-JIMApi -Endpoint "/api/v1/example-data/templates/$Id/execute" -Method 'POST'

                Write-Verbose "Data generation template execution started"

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
