# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMRetiredGeneratedValue {
    <#
    .SYNOPSIS
        Lists the retired generated values for an attribute.

    .DESCRIPTION
        Returns the retired values register for an attribute that a generated Attribute Flow produces:
        the values JIM issued for it and will never issue again, whichever Attribute Flow generates it,
        newest first. Every page is read, so the whole register is returned.

        A value is retired when the object that held it is deleted (ObjectDeleted), when another
        Attribute Flow takes the attribute over (Superseded), or when the generating Attribute Flow is
        removed (Recalled), provided the flow has "Never reuse a value" on (always the case for a
        Sequence). This cmdlet is read-only: the only way to forget retired values is
        Restart-JIMGeneratedValues on a Sequence flow.

        Name the attribute in one of three ways: a Metaverse Attribute by id or by name (for an import
        flow), or a Connected System attribute by its Connected System, Object Type and attribute ids
        (for an export flow).

    .PARAMETER MetaverseAttributeId
        The ID of the Metaverse Attribute, for an import Attribute Flow. A Metaverse Attribute piped from
        Get-JIMMetaverseAttribute binds here through its Id.

    .PARAMETER MetaverseAttributeName
        The name of the Metaverse Attribute, for an import Attribute Flow, such as Account Name.

    .PARAMETER ConnectedSystemId
        For an export Attribute Flow, the ID of the Connected System whose attribute is generated.

    .PARAMETER ObjectTypeId
        For an export Attribute Flow, the ID of the Connected System Object Type the attribute belongs to.

    .PARAMETER AttributeId
        For an export Attribute Flow, the ID of the Connected System attribute.

    .PARAMETER Search
        Returns only the values containing this text, or held by an object whose name contains it, ignoring case.

    .OUTPUTS
        One object per retired value, newest first. Every page is read, so the whole register is returned.

        JIM.RetiredGeneratedValue
        HeldBy (string): Who held the value, for reading: the name of the object that held it, with "(deleted)" once
            that object no longer exists.

    .EXAMPLE
        Get-JIMRetiredGeneratedValue -MetaverseAttributeName 'Account Name'

        Lists every retired Account Name value.

    .EXAMPLE
        Get-JIMRetiredGeneratedValue -MetaverseAttributeName 'Account Name' -Search 'fenwick' |
            Select-Object Value, RetiredAt, Reason, HeldBy

        Finds whether a former holder's value is retired, and why.

    .EXAMPLE
        Get-JIMMetaverseAttribute -Name 'Employee Number' | Get-JIMRetiredGeneratedValue

        Pipes a Metaverse Attribute straight in.

    .EXAMPLE
        Get-JIMRetiredGeneratedValue -ConnectedSystemId 3 -ObjectTypeId 7 -AttributeId 70

        Lists the retired values of an attribute an export Attribute Flow generates.

    .LINK
        Restart-JIMGeneratedValues
        Get-JIMGeneratedValue
        Get-JIMMetaverseAttribute
        Get-JIMSyncRuleMapping
    #>
    [CmdletBinding(DefaultParameterSetName = 'ByMetaverseAttributeId')]
    [OutputType('JIM.RetiredGeneratedValue')]
    param(
        # Aliased to Id so a Metaverse Attribute can be piped straight in (Get-JIMMetaverseAttribute exposes Id). This
        # is the parent attribute's id, not a register entry's; documentation always spells out -MetaverseAttributeId
        # (see the parameter alias rules in src/JIM.PowerShell/CLAUDE.md). The cmdlet is read-only, so the alias cannot
        # aim a destructive action at the wrong thing.
        [Parameter(Mandatory, ParameterSetName = 'ByMetaverseAttributeId', ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$MetaverseAttributeId,

        [Parameter(Mandatory, ParameterSetName = 'ByMetaverseAttributeName')]
        [ValidateNotNullOrEmpty()]
        [string]$MetaverseAttributeName,

        [Parameter(Mandatory, ParameterSetName = 'ByConnectedSystemAttribute')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ByConnectedSystemAttribute')]
        [int]$ObjectTypeId,

        [Parameter(Mandatory, ParameterSetName = 'ByConnectedSystemAttribute')]
        [int]$AttributeId,

        [string]$Search
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        switch ($PSCmdlet.ParameterSetName) {
            'ByConnectedSystemAttribute' {
                $endpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/object-types/$ObjectTypeId/attributes/$AttributeId/retired-generated-values"
            }
            'ByMetaverseAttributeName' {
                try {
                    $attribute = Resolve-JIMMetaverseAttribute -Name $MetaverseAttributeName
                }
                catch {
                    Write-Error $_
                    return
                }
                $endpoint = "/api/v1/metaverse/attributes/$($attribute.id)/retired-generated-values"
            }
            default {
                $endpoint = "/api/v1/metaverse/attributes/$MetaverseAttributeId/retired-generated-values"
            }
        }

        if ($PSBoundParameters.ContainsKey('Search') -and -not [string]::IsNullOrWhiteSpace($Search)) {
            $endpoint += "?search=$([uri]::EscapeDataString($Search))"
        }

        Write-Verbose "Getting retired generated values: $endpoint"
        $items = Get-JIMPagedItems -Endpoint $endpoint

        foreach ($item in $items) {
            # A reading convenience matching the portal's "Held by" column; the raw fields stay alongside it.
            $heldBy = $item.FromObjectDisplayName
            if ($heldBy -and -not $item.FromObjectExists) {
                $heldBy = "$heldBy (deleted)"
            }
            $item | Add-Member -NotePropertyName 'HeldBy' -NotePropertyValue $heldBy -Force -PassThru
        }
    }
}
