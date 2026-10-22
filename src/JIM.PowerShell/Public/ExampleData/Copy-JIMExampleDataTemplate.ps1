# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Copy-JIMExampleDataTemplate {
    <#
    .SYNOPSIS
        Copies a Data Generation Template under a new name.

    .DESCRIPTION
        Creates a new Data Generation Template with the same Object Types and generation rules as an existing one.
        Built-in Data Generation Templates can be copied, which is how to customise one: the copy is never built in,
        so it can be changed with Set-JIMExampleDataTemplate and removed with Remove-JIMExampleDataTemplate.

    .PARAMETER Id
        The Data Generation Template to copy.

    .PARAMETER Name
        The name of the Data Generation Template to copy.

    .PARAMETER NewName
        The name of the new Data Generation Template. It must not already be in use by another template.

    .PARAMETER ChangeReason
        A reason for the change, recorded on its Activity and shown in the new Data Generation Template's
        Configuration Change History.

    .PARAMETER PassThru
        Returns the new Data Generation Template.

    .OUTPUTS
        With -PassThru, the new Data Generation Template; otherwise nothing.

    .EXAMPLE
        Copy-JIMExampleDataTemplate -Name "Users & Groups" -NewName "Contoso Demo"

        Copies the built-in template so it can be customised.

    .EXAMPLE
        $copy = Copy-JIMExampleDataTemplate -Id 3 -NewName "Contoso Demo" -ChangeReason "Demo for Contoso (CHG0300)" -PassThru
        $copy.Id

        Copies a template, records the reason on the new template's change history, and returns the new template.

    .LINK
        Get-JIMExampleDataTemplate
        New-JIMExampleDataTemplate
        Set-JIMExampleDataTemplate
        Remove-JIMExampleDataTemplate
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType('JIM.ExampleDataTemplateDto')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$NewName,

        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ParameterSetName -eq 'ByName') {
            try {
                $Id = (Resolve-JIMExampleDataTemplate -Name $Name).id
            }
            catch {
                Write-Error $_
                return
            }
        }

        if ($PSCmdlet.ShouldProcess($NewName, "Copy Data Generation Template $Id")) {
            Write-Verbose "Copying Data Generation Template $Id as: $NewName"

            try {
                $body = @{ name = $NewName }
                if ($ChangeReason) {
                    $body.changeReason = $ChangeReason
                }

                $response = Invoke-JIMApi -Endpoint "/api/v1/example-data/templates/$Id/copy" -Method 'POST' -Body $body
                Write-Verbose "Created Data Generation Template: $($response.id)"

                # What the save said about the template's expressions without refusing it: each written as the server
                # phrased it.
                foreach ($saveWarning in @($response.Warnings | Where-Object { $_ })) {
                    Write-Warning $saveWarning
                }

                if ($PassThru) {
                    $response
                }
            }
            catch {
                Write-Error "Failed to copy Data Generation Template: $_"
            }
        }
    }
}
