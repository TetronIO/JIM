# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Add-JIMMetaverseObjectTypeAttribute {
    <#
    .SYNOPSIS
        Binds a custom Metaverse Attribute to a Metaverse Object Type.

    .DESCRIPTION
        Creates a binding between an existing custom Metaverse Attribute and a Metaverse Object
        Type, making the attribute available on objects of that type. Binding an attribute that is
        already bound to the Object Type is a no-op. Built-in attributes cannot be re-bound.

    .PARAMETER AttributeId
        The unique identifier of the Metaverse Attribute to bind.

    .PARAMETER ObjectTypeId
        The unique identifier of the Metaverse Object Type to bind the attribute to.

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the object's
        configuration change history.

    .PARAMETER PassThru
        If specified, returns the updated Attribute object.

    .OUTPUTS
        If -PassThru is specified, returns the updated Attribute object.

    .EXAMPLE
        Add-JIMMetaverseObjectTypeAttribute -AttributeId 42 -ObjectTypeId 1

        Binds attribute 42 to Object Type 1.

    .EXAMPLE
        Get-JIMMetaverseAttribute -Name "CostCentre" | Add-JIMMetaverseObjectTypeAttribute -ObjectTypeId 1 -PassThru

        Binds the CostCentre attribute (from the pipeline) to Object Type 1 and returns it.

    .LINK
        Remove-JIMMetaverseObjectTypeAttribute
        Get-JIMMetaverseAttribute
        New-JIMMetaverseAttribute
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$AttributeId,

        [Parameter(Mandatory)]
        [int]$ObjectTypeId,

        [Parameter()]
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

        if (-not $PSCmdlet.ShouldProcess("Attribute $AttributeId -> Object Type $ObjectTypeId", "Bind Metaverse Attribute to Object Type")) {
            return
        }

        Write-Verbose "Binding Metaverse Attribute $AttributeId to Object Type $ObjectTypeId"

        try {
            $endpoint = "/api/v1/metaverse/attributes/$AttributeId/object-types/$ObjectTypeId"
            if ($ChangeReason) {
                $endpoint += "?changeReason=$([uri]::EscapeDataString($ChangeReason))"
            }
            $result = Invoke-JIMApi -Endpoint $endpoint -Method 'POST'

            Write-Verbose "Bound Metaverse Attribute $AttributeId to Object Type $ObjectTypeId"

            if ($PassThru) {
                $result
            }
        }
        catch {
            Write-Error "Failed to bind Metaverse Attribute $AttributeId to Object Type ${ObjectTypeId}: $_"
        }
    }
}
