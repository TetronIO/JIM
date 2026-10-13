# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMMetaverseObjectType {
    <#
    .SYNOPSIS
        Updates a Metaverse Object Type in JIM.

    .DESCRIPTION
        Updates an existing Metaverse Object Type: its identity (name, plural name, icon) and/or its
        deletion rule settings (which control how and when Metaverse Objects of this type are
        automatically deleted). Built-in types (User, Group) accept deletion-rule changes but reject
        changes to Name, Plural Name and Icon.

        When the stored deletion rule is WhenLastConnectorDisconnected and enabled provisioning export
        Synchronisation Rules exist for the type, the API attaches a configuration advisory (provisioned
        target accounts count as connectors, so objects outlive their last source and keep its values as
        last known state) and the cmdlet surfaces it as a warning; the returned object carries it as
        DeletionRuleAdvisory.

        When the deletion rule is WhenAuthoritativeSourceDisconnected and a Connected System projects into the type
        without being one of its authoritative sources, the cmdlet writes one warning per such system: objects only it
        holds will never be deleted, and objects it shares with an authoritative source are deleted when that source
        disconnects. The returned object lists them as DeletionSourceWarnings. Join-only contributors (projection off)
        are never listed.

    .PARAMETER Id
        The ID of the Object Type to change.

    .PARAMETER Name
        The name of the Object Type to change. To rename it, use -NewName.

    .PARAMETER InputObject
        An Object Type from the pipeline, for example from Get-JIMMetaverseObjectType.

    .PARAMETER NewName
        A new singular name for the Object Type. It must be unique, ignoring case. A built-in Object Type cannot be
        renamed.

    .PARAMETER PluralName
        A new plural name for the Object Type. It must be unique, ignoring case. A built-in Object Type keeps its
        plural name.

    .PARAMETER Icon
        The name of the MudBlazor icon the portal shows for the Object Type, such as Devices. $null or '' clears it.
        A built-in Object Type keeps its icon.

    .PARAMETER DeletionRule
        When JIM deletes this type's Metaverse Objects on its own. With Manual, it never does. With
        WhenLastConnectorDisconnected, it deletes an object once no Connected System Object is joined to it; an
        account JIM has provisioned counts as one. With WhenAuthoritativeSourceDisconnected, it deletes an object
        when the sources named in -DeletionTriggerConnectedSystemIds disconnect, as -DeletionTriggerMode decides.

    .PARAMETER DeletionGracePeriod
        How long JIM waits, once the deletion rule is met, before it deletes the object, for example
        [TimeSpan]::FromDays(30). [TimeSpan]::Zero deletes at once.

    .PARAMETER DeletionTriggerConnectedSystemIds
        The IDs of the Connected Systems whose disconnection triggers deletion. Required when the deletion rule is
        WhenAuthoritativeSourceDisconnected, and ignored otherwise.

    .PARAMETER DeletionTriggerMode
        For WhenAuthoritativeSourceDisconnected, how the selected sources trigger deletion. With
        AllSourcesDisconnect, an object is deleted only once no selected source keeps a joined Connected System
        Object. With SpecificSourcesDisconnect, it is deleted when any one selected source disconnects, even while
        others remain. Omit it to leave the mode unchanged.

    .PARAMETER ChangeReason
        An optional reason for the change, recorded in the Object Type's Configuration Change History.

    .PARAMETER PreviewActivityId
        The ActivityId of the Configuration Change Preview read before making this change, from
        New-JIMConfigurationChangePreview. The change's Activity records it, so the audit shows what the change was
        expected to do as well as what it did.

    .PARAMETER PassThru
        Returns the updated Object Type.

    .OUTPUTS
        With -PassThru, the updated Object Type in full; otherwise nothing. When its deletion settings draw a
        warning, in DeletionRuleAdvisory or DeletionSourceWarnings, the cmdlet also writes it as a warning.

    .EXAMPLE
        Set-JIMMetaverseObjectType -Id 1 -DeletionRule WhenLastConnectorDisconnected -DeletionGracePeriod ([TimeSpan]::FromDays(30))

        Configures User type to delete 30 days after last connector disconnects.

    .EXAMPLE
        Set-JIMMetaverseObjectType -Name 'User' -DeletionGracePeriod ([TimeSpan]::Zero)

        Configures immediate deletion for User type when connectors disconnect.

    .EXAMPLE
        Get-JIMMetaverseObjectType -Name 'User' | Set-JIMMetaverseObjectType -DeletionGracePeriod ([TimeSpan]::FromDays(7)) -PassThru

        Updates from pipeline and returns the updated object.

    .EXAMPLE
        Set-JIMMetaverseObjectType -Id 1 -DeletionGracePeriod ([TimeSpan]::FromMinutes(1))

        Configures a 1-minute grace period (useful for testing).

    .EXAMPLE
        Set-JIMMetaverseObjectType -Id 1 -DeletionRule WhenAuthoritativeSourceDisconnected -DeletionTriggerConnectedSystemIds 1,2

        Configure deletion to trigger when HR system (ID 1) or AD system (ID 2) disconnects.

    .EXAMPLE
        Set-JIMMetaverseObjectType -Id 1 -DeletionTriggerConnectedSystemIds 1,2 -DeletionTriggerMode AllSourcesDisconnect

        Configure deletion to trigger only once both HR systems (IDs 1 and 2) have
        disconnected; while either retains a joined Connected System Object, the
        Metaverse Object is kept.

    .EXAMPLE
        Set-JIMMetaverseObjectType -Id 5 -NewName 'Gadget' -PluralName 'Gadgets' -Icon 'Devices'

        Renames a custom Object Type and sets its UI icon.

    .EXAMPLE
        Set-JIMMetaverseObjectType -Id 5 -Icon $null

        Clears the Object Type's icon (passing '' is equivalent).

    .EXAMPLE
        $preview = New-JIMConfigurationChangePreview -MetaverseObjectTypeId 1 -DeletionRule WhenLastConnectorDisconnected -Wait
        Set-JIMMetaverseObjectType -Id 1 -DeletionRule WhenLastConnectorDisconnected -PreviewActivityId $preview.ActivityId

        Applies the change that was previewed, recording which preview informed it.

    .LINK
        Get-JIMMetaverseObjectType
        Remove-JIMMetaverseObjectType
        Get-JIMMetaverseObject
        New-JIMConfigurationChangePreview
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType('JIM.MetaverseObjectTypeDetailDto')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$NewName,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$PluralName,

        [Parameter()]
        [string]$Icon,

        [Parameter()]
        [ValidateSet('Manual', 'WhenLastConnectorDisconnected', 'WhenAuthoritativeSourceDisconnected')]
        [string]$DeletionRule,

        [Parameter()]
        [TimeSpan]$DeletionGracePeriod,

        [Parameter()]
        [int[]]$DeletionTriggerConnectedSystemIds,

        [Parameter()]
        [ValidateSet('AllSourcesDisconnect', 'SpecificSourcesDisconnect')]
        [string]$DeletionTriggerMode,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [Parameter()]
        [guid]$PreviewActivityId,

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
                $resolvedType = Resolve-JIMMetaverseObjectType -Name $Name
                $Id = $resolvedType.id
            }
            catch {
                Write-Error $_
                return
            }
        }
        elseif ($InputObject) {
            $Id = $InputObject.id
        }

        # Build update body
        $body = @{}

        if ($PSBoundParameters.ContainsKey('NewName')) {
            $body.name = $NewName
        }

        if ($PSBoundParameters.ContainsKey('PluralName')) {
            $body.pluralName = $PluralName
        }

        # Icon is clearable: both $null and '' clear it (the binder coerces $null to '' for [string]).
        if ($PSBoundParameters.ContainsKey('Icon')) {
            $body.icon = $Icon
        }

        if ($DeletionRule) {
            # Enum sent as its string name; -DeletionRule's ValidateSet equals the
            # MetaverseObjectDeletionRule member names. The API rejects numeric ordinals
            # (JsonStringEnumConverter allowIntegerValues:false, PR #1060).
            $body.deletionRule = $DeletionRule
        }

        if ($PSBoundParameters.ContainsKey('DeletionGracePeriod')) {
            # API expects TimeSpan as "d.hh:mm:ss" string format
            $body.deletionGracePeriod = $DeletionGracePeriod.ToString()
        }

        if ($PSBoundParameters.ContainsKey('DeletionTriggerConnectedSystemIds')) {
            $body.deletionTriggerConnectedSystemIds = $DeletionTriggerConnectedSystemIds
        }

        if ($PSBoundParameters.ContainsKey('DeletionTriggerMode')) {
            # Enum sent as its string name; -DeletionTriggerMode's ValidateSet equals the
            # AuthoritativeSourceTriggerMode member names. Only sent when bound, so an
            # omitted parameter leaves the stored mode unchanged.
            $body.deletionTriggerMode = $DeletionTriggerMode
        }

        if ($body.Count -eq 0) {
            Write-Warning "No updates specified."
            return
        }

        if ($ChangeReason) {
            $body.changeReason = $ChangeReason
        }

        # Set after the "no updates specified" guard above deliberately: a preview link on its own is not
        # a change, and recording one against an update that changes nothing would be a false audit entry.
        if ($PSBoundParameters.ContainsKey('PreviewActivityId')) {
            $body.previewActivityId = $PreviewActivityId
        }

        $displayName = $Name ?? "ID $Id"

        if ($PSCmdlet.ShouldProcess($displayName, "Update Metaverse Object Type")) {
            Write-Verbose "Updating Metaverse Object Type: $Id"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/metaverse/object-types/$Id" -Method 'PUT' -Body $body

                Write-Verbose "Updated Metaverse Object Type: $Id"

                # Configuration advisory (#1570): the API attaches advice when the stored deletion rule will
                # keep objects alive after their last source departs (provisioned targets count as connectors).
                if ($result.deletionRuleAdvisory) {
                    Write-Warning $result.deletionRuleAdvisory
                }

                # Projecting systems that are not authoritative sources (#1256): one warning per system, never blocking.
                Write-JIMDeletionSourceWarning -ObjectType $result

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update Metaverse Object Type: $_"
            }
        }
    }
}
