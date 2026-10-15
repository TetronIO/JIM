# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMMetaverseObjectType {
    <#
    .SYNOPSIS
        Creates a new Metaverse Object Type in JIM.

    .DESCRIPTION
        Creates a new Object Type in the Metaverse schema. Object Types define what kinds
        of identity records JIM stores (Users, Groups, Devices, custom types, etc.). The
        new type is created with BuiltIn = false so it can be removed via Reset-JIMSystem
        during test teardown or by administrators in the UI later.

        When the deletion rule is WhenLastConnectorDisconnected and enabled provisioning export
        Synchronisation Rules exist for the type, the API attaches a configuration advisory (provisioned
        target accounts count as connectors, so objects outlive their last source and keep its values as
        last known state) and the cmdlet surfaces it as a warning; the returned object carries it as
        DeletionRuleAdvisory.

        When the deletion rule is WhenAuthoritativeSourceDisconnected and a Connected System projects into the type
        without being one of its authoritative sources, the cmdlet writes one warning per such system: objects only it
        holds will never be deleted, and objects it shares with an authoritative source are deleted when that source
        disconnects. The returned object lists them as DeletionSourceWarnings. Join-only contributors (projection off)
        are never listed.

    .PARAMETER Name
        The singular name of the new Object Type, such as Device. It must be unique.

    .PARAMETER PluralName
        The plural name of the new Object Type, such as Devices. It must be unique.

    .PARAMETER Icon
        The name of the MudBlazor icon the portal shows for the Object Type, such as Devices.

    .PARAMETER AttributeIds
        The IDs of existing Metaverse Attributes to bind to the new Object Type. Attributes can also be bound later,
        with Add-JIMMetaverseObjectTypeAttribute.

    .PARAMETER DeletionRule
        When JIM deletes this type's Metaverse Objects on its own. With Manual, it never does. With
        WhenLastConnectorDisconnected, it deletes an object once no Connected System Object is joined to it; an
        account JIM has provisioned counts as one. With WhenAuthoritativeSourceDisconnected, it deletes an object
        when the sources named in -DeletionTriggerConnectedSystemIds disconnect, as -DeletionTriggerMode decides.
        WhenLastConnectorDisconnected is the default, as in the portal.

    .PARAMETER DeletionGracePeriod
        How long JIM waits, once the deletion rule is met, before it deletes the object. [TimeSpan]::Zero deletes at
        once. Ignored when -DeletionRule is Manual.

    .PARAMETER DeletionTriggerConnectedSystemIds
        The IDs of the Connected Systems whose disconnection triggers deletion. Required when -DeletionRule is
        WhenAuthoritativeSourceDisconnected.

    .PARAMETER DeletionTriggerMode
        For WhenAuthoritativeSourceDisconnected, how the selected sources trigger deletion. With
        AllSourcesDisconnect, an object is deleted only once no selected source keeps a joined Connected System
        Object. With SpecificSourcesDisconnect, it is deleted when any one selected source disconnects, even while
        others remain. AllSourcesDisconnect is the default.

    .PARAMETER ChangeReason
        An optional reason for the change, recorded in the Object Type's Configuration Change History.

    .OUTPUTS
        The created Object Type in full. When its deletion settings draw a warning, in DeletionRuleAdvisory or
        DeletionSourceWarnings, the cmdlet also writes it as a warning.

    .EXAMPLE
        New-JIMMetaverseObjectType -Name "Device" -PluralName "Devices"

        Creates a new "Device" Metaverse Object Type with the default Deletion Rule, WhenLastConnectorDisconnected.

    .EXAMPLE
        New-JIMMetaverseObjectType -Name "Contractor" -PluralName "Contractors" -AttributeIds 1,2,3

        Creates a new "Contractor" type and associates Metaverse attributes with IDs 1, 2, 3.

    .EXAMPLE
        New-JIMMetaverseObjectType -Name "ServiceAccount" -PluralName "ServiceAccounts" `
            -DeletionRule WhenAuthoritativeSourceDisconnected `
            -DeletionTriggerConnectedSystemIds 5 `
            -DeletionGracePeriod ([TimeSpan]::FromDays(7))

        Creates a new type that is automatically deleted seven days after the authoritative
        source (connected system ID 5) disconnects.

    .EXAMPLE
        New-JIMMetaverseObjectType -Name "Contractor" -PluralName "Contractors" `
            -DeletionRule WhenAuthoritativeSourceDisconnected `
            -DeletionTriggerConnectedSystemIds 3, 7 `
            -DeletionTriggerMode SpecificSourcesDisconnect

        Creates a new type whose objects are deleted as soon as either HR system (connected
        system ID 3 or 7) disconnects, even if the other remains connected.

    .LINK
        Get-JIMMetaverseObjectType
        Set-JIMMetaverseObjectType
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType('JIM.MetaverseObjectTypeDetailDto')]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$PluralName,

        [Parameter()]
        [string]$Icon,

        [Parameter()]
        [int[]]$AttributeIds,

        [Parameter()]
        [ValidateSet('Manual', 'WhenLastConnectorDisconnected', 'WhenAuthoritativeSourceDisconnected')]
        [string]$DeletionRule,

        [Parameter()]
        [TimeSpan]$DeletionGracePeriod,

        [Parameter()]
        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [Parameter()]
        [int[]]$DeletionTriggerConnectedSystemIds,

        [Parameter()]
        [ValidateSet('AllSourcesDisconnect', 'SpecificSourcesDisconnect')]
        [string]$DeletionTriggerMode
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Build request body
        $body = @{
            name = $Name
            pluralName = $PluralName
        }

        if ($PSBoundParameters.ContainsKey('Icon')) {
            $body.icon = $Icon
        }

        if ($AttributeIds) {
            $body.attributeIds = $AttributeIds
        }

        if ($PSBoundParameters.ContainsKey('DeletionRule')) {
            # Enum sent as its string name; -DeletionRule's ValidateSet equals the
            # MetaverseObjectDeletionRule member names. The API rejects numeric ordinals
            # (JsonStringEnumConverter allowIntegerValues:false, PR #1060).
            $body.deletionRule = $DeletionRule
        }

        if ($PSBoundParameters.ContainsKey('DeletionGracePeriod')) {
            # API expects an ISO 8601 duration / TimeSpan-string-compatible value
            $body.deletionGracePeriod = $DeletionGracePeriod.ToString()
        }

        if ($DeletionTriggerConnectedSystemIds) {
            $body.deletionTriggerConnectedSystemIds = $DeletionTriggerConnectedSystemIds
        }

        if ($PSBoundParameters.ContainsKey('DeletionTriggerMode')) {
            # Enum sent as its string name; -DeletionTriggerMode's ValidateSet equals the
            # AuthoritativeSourceTriggerMode member names. Only sent when bound, so an
            # omitted parameter leaves the server-side default (AllSourcesDisconnect) to apply.
            $body.deletionTriggerMode = $DeletionTriggerMode
        }

        if ($ChangeReason) {
            $body.changeReason = $ChangeReason
        }

        if ($PSCmdlet.ShouldProcess($Name, "Create Metaverse Object Type")) {
            Write-Verbose "Creating Metaverse Object Type: $Name"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/metaverse/object-types" -Method 'POST' -Body $body

                Write-Verbose "Created Metaverse Object Type: $Name with ID: $($result.id)"

                # Configuration advisory (#1570): the API attaches advice when the stored deletion rule will
                # keep objects alive after their last source departs (provisioned targets count as connectors).
                if ($result.deletionRuleAdvisory) {
                    Write-Warning $result.deletionRuleAdvisory
                }

                # Projecting systems that are not authoritative sources (#1256): one warning per system, never blocking.
                Write-JIMDeletionSourceWarning -ObjectType $result

                $result
            }
            catch {
                Write-Error "Failed to create Metaverse Object Type: $_"
            }
        }
    }
}
