# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMRetiredGeneratedValue {
    <#
    .SYNOPSIS
        Lists the retired generated values for an attribute (Unique Value Generation, #242).

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
        The id of the Metaverse Attribute. Also accepts pipeline input via the Id property, so a
        Metaverse Attribute from Get-JIMMetaverseAttribute can be piped straight in. Alias: Id.

    .PARAMETER MetaverseAttributeName
        The name of the Metaverse Attribute, for example 'Account Name'. Resolved to its id first.

    .PARAMETER ConnectedSystemId
        For an export flow: the id of the Connected System whose attribute is generated.

    .PARAMETER ObjectTypeId
        For an export flow: the id of the Connected System Object Type the attribute belongs to.

    .PARAMETER AttributeId
        For an export flow: the id of the Connected System attribute.

    .PARAMETER Search
        Optional text matched case-insensitively against the value and the name of the object that
        held it.

    .OUTPUTS
        One PSCustomObject per retired value:

        | Property                             | Description                                                                 |
        |--------------------------------------|-----------------------------------------------------------------------------|
        | Id                                   | The register entry's own identifier                                         |
        | MetaverseAttributeId                 | The Metaverse Attribute (import flow); null for an export flow              |
        | ConnectedSystemObjectTypeAttributeId | The Connected System attribute (export flow); null for an import flow       |
        | AttributeName                        | The attribute's name                                                        |
        | Value                                | The value as it was issued                                                  |
        | RetiredAt                            | When it was retired (UTC)                                                   |
        | Reason                               | ObjectDeleted, Superseded, Recalled or Regenerated                          |
        | FromObjectId                         | The object that held it (Metaverse Object or Connected System Object)       |
        | FromObjectDisplayName                | That object's name, captured when the value was retired                     |
        | FromObjectExists                     | Whether that object still exists                                            |
        | ActivityId                           | The Activity during which it was retired, when one was recorded             |
        | HeldBy                               | Who held it, for reading: the name, with "(deleted)" once the object is gone |

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
    [OutputType([PSCustomObject])]
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
