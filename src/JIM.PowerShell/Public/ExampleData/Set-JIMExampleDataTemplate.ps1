# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMExampleDataTemplate {
    <#
    .SYNOPSIS
        Updates a Data Generation Template in JIM.

    .DESCRIPTION
        Renames a Data Generation Template and/or replaces its Object Type configuration.
        Built-in Data Generation Templates cannot be updated.

    .PARAMETER Id
        The unique identifier of the Data Generation Template to update.

    .PARAMETER Name
        The name of the Data Generation Template to update.

    .PARAMETER NewName
        The new name for the Data Generation Template.

    .PARAMETER ObjectType
        When specified, replaces the template's ENTIRE Object Type graph: every Object Type and every
        attribute configuration not present in the supplied hashtables is removed. To add a single
        attribute without restating the rest, use Add-JIMExampleDataTemplateAttribute instead.
        Hashtable shape is as New-JIMExampleDataTemplate's -ObjectType parameter.

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the Data Generation
        Template's configuration change history.

    .PARAMETER PassThru
        If specified, returns the updated Data Generation Template object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Data Generation Template object.

    .EXAMPLE
        Set-JIMExampleDataTemplate -Id 7 -NewName "Demo Users v2"

        Renames the Data Generation Template.

    .EXAMPLE
        Set-JIMExampleDataTemplate -Name "Demo Users" -ObjectType @{ MetaverseObjectType = "User"; ObjectsToCreate = 1000 } -PassThru

        Replaces the template's entire Object Type graph with a single Object Type configuration.

    .EXAMPLE
        Set-JIMExampleDataTemplate -Id 7 -NewName "Demo Users v2" -ChangeReason "Renaming for clarity (CHG0201)"

        Renames the template and records the reason on its configuration change history.

    .LINK
        Get-JIMExampleDataTemplate
        New-JIMExampleDataTemplate
        Remove-JIMExampleDataTemplate
        Add-JIMExampleDataTemplateAttribute
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$NewName,

        [Parameter()]
        [hashtable[]]$ObjectType,

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

        # Only send what the caller named. An omitted setting must reach the API as absent rather than
        # as a default, or every call would silently rewrite settings it was never asked about.
        if (-not $PSBoundParameters.ContainsKey('NewName') -and -not $PSBoundParameters.ContainsKey('ObjectType')) {
            Write-Error "No settings were supplied to change. Supply at least one of -NewName or -ObjectType."
            return
        }

        # Resolve name to ID if using ByName parameter set
        $displayName = if ($PSCmdlet.ParameterSetName -eq 'ByName') { $Name } else { $Id }
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

        if ($PSCmdlet.ShouldProcess($displayName, "Update Data Generation Template")) {
            Write-Verbose "Updating Data Generation Template: $Id"

            try {
                $body = @{}
                if ($PSBoundParameters.ContainsKey('NewName')) {
                    $body.name = $NewName
                }
                if ($PSBoundParameters.ContainsKey('ObjectType')) {
                    $body.objectTypes = @(ConvertTo-JIMExampleDataTemplateObjectTypes -ObjectType $ObjectType)
                }
                if ($ChangeReason) {
                    $body.changeReason = $ChangeReason
                }

                $response = Invoke-JIMApi -Endpoint "/api/v1/example-data/templates/$Id" -Method 'PUT' -Body $body
                Write-Verbose "Updated Data Generation Template: $Id"

                if ($PassThru) {
                    $response
                }
            }
            catch {
                Write-Error "Failed to update Data Generation Template: $_"
            }
        }
    }
}
