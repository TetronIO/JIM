# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Remove-JIMExampleDataTemplate {
    <#
    .SYNOPSIS
        Removes a Data Generation Template from JIM.

    .DESCRIPTION
        Deletes a Data Generation Template, including its whole per-Object-Type attribute configuration.
        Built-in Data Generation Templates cannot be removed. This action cannot be undone; objects the
        template has already generated are unaffected.

    .PARAMETER Id
        The unique identifier of the Data Generation Template to remove.

    .PARAMETER Name
        The name of the Data Generation Template to remove.

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the Data Generation
        Template's configuration change history.

    .PARAMETER Force
        Bypasses confirmation prompts.

    .OUTPUTS
        None.

    .EXAMPLE
        Remove-JIMExampleDataTemplate -Id 7

        Removes the specified Data Generation Template (with confirmation).

    .EXAMPLE
        Remove-JIMExampleDataTemplate -Name "Demo Users" -Force

        Removes the named Data Generation Template without confirmation.

    .EXAMPLE
        Remove-JIMExampleDataTemplate -Id 7 -Force -ChangeReason "Retiring demo template (CHG0202)"

        Removes the Data Generation Template and records the reason on its configuration change history.

    .LINK
        Get-JIMExampleDataTemplate
        New-JIMExampleDataTemplate
        Set-JIMExampleDataTemplate
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ById')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$Force
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Resolve name to ID if using ByName parameter set
        $templateName = $Id
        if ($PSCmdlet.ParameterSetName -eq 'ByName') {
            try {
                $resolvedTemplate = Resolve-JIMExampleDataTemplate -Name $Name
                $Id = $resolvedTemplate.id
                $templateName = $resolvedTemplate.name
            }
            catch {
                Write-Error $_
                return
            }
        }
        else {
            # Get template name for confirmation message
            try {
                $template = Invoke-JIMApi -Endpoint "/api/v1/example-data/templates/$Id"
                $templateName = $template.name
            }
            catch {
                # Continue with ID if we can't get the name
            }
        }

        if ($Force -or $PSCmdlet.ShouldProcess($templateName, "Remove Data Generation Template")) {
            Write-Verbose "Removing Data Generation Template: $Id ($templateName)"

            try {
                $endpoint = "/api/v1/example-data/templates/$Id"
                if ($ChangeReason) {
                    $endpoint += "?changeReason=$([uri]::EscapeDataString($ChangeReason))"
                }
                Invoke-JIMApi -Endpoint $endpoint -Method 'DELETE'
                Write-Verbose "Removed Data Generation Template: $Id"
            }
            catch {
                Write-Error "Failed to remove Data Generation Template: $_"
            }
        }
    }
}
