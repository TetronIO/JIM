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

    .PARAMETER Name
        The singular name of the new Object Type. Must be unique. Example: "User", "Group".

    .PARAMETER PluralName
        The plural name of the new Object Type. Must be unique. Example: "Users", "Groups".

    .PARAMETER Icon
        Optional MudBlazor icon name to associate with the type in the UI.

    .PARAMETER AttributeIds
        Optional array of existing Metaverse Attribute IDs to associate with this type.
        Attributes can also be associated later via Set-JIMMetaverseAttribute.

    .PARAMETER DeletionRule
        Optional deletion rule controlling when Metaverse Objects of this type are
        automatically deleted. Defaults to 'Manual'.
        Valid values: Manual, WhenLastConnectorDisconnected, WhenAuthoritativeSourceDisconnected.

    .PARAMETER DeletionGracePeriod
        Optional grace period before deletion is executed (TimeSpan). Set to TimeSpan.Zero
        for immediate deletion. Ignored when DeletionRule is Manual.

    .PARAMETER DeletionTriggerConnectedSystemIds
        Required when DeletionRule is WhenAuthoritativeSourceDisconnected: the connected
        system IDs whose disconnect should trigger deletion. How they trigger deletion is
        governed by -DeletionTriggerMode.

    .PARAMETER DeletionTriggerMode
        Optional. For the WhenAuthoritativeSourceDisconnected deletion rule, controls how
        the selected authoritative sources trigger deletion.
        - AllSourcesDisconnect: the Metaverse Object is deleted only once no selected
          source retains a joined Connected System Object.
        - SpecificSourcesDisconnect: the Metaverse Object is deleted when any one of the
          selected sources disconnects, even if others remain connected.
        When omitted, the server default (AllSourcesDisconnect) applies.

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the object's
        configuration change history.

    .OUTPUTS
        PSCustomObject representing the created Object Type (id, name, pluralName, etc.).

    .EXAMPLE
        New-JIMMetaverseObjectType -Name "Device" -PluralName "Devices"

        Creates a new "Device" Metaverse Object Type with default deletion rule (Manual).

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
    [OutputType([PSCustomObject])]
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

                $result
            }
            catch {
                Write-Error "Failed to create Metaverse Object Type: $_"
            }
        }
    }
}
