# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMSyncRule {
    <#
    .SYNOPSIS
        Creates a Synchronisation Rule.

    .DESCRIPTION
        Creates a Synchronisation Rule, which decides how values flow between a Connected System Object Type and a
        Metaverse Object Type, and in which direction. An import rule can also create Metaverse Objects
        (-ProjectToMetaverse), and an export rule can create Connected System Objects (-ProvisionToConnectedSystem).
        Attribute Flows, scoping criteria and Object Matching Rules are added to it afterwards.

    .PARAMETER Name
        The name of the Synchronisation Rule.

    .PARAMETER Description
        What the Synchronisation Rule is for, for administrators.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System the rule synchronises with.

    .PARAMETER ConnectedSystemName
        The name of the Connected System the rule synchronises with, matched exactly.

    .PARAMETER ConnectedSystemObjectTypeId
        The ID of the Connected System Object Type the rule applies to.

    .PARAMETER MetaverseObjectTypeId
        The ID of the Metaverse Object Type the rule applies to.

    .PARAMETER Direction
        Import to flow values from the Connected System into the Metaverse, Export to flow them out to it.

    .PARAMETER ProjectToMetaverse
        On an import rule, creates a Metaverse Object for each Connected System Object in scope that matches none.

    .PARAMETER ProvisionToConnectedSystem
        On an export rule, creates a Connected System Object for each Metaverse Object in scope that has none.

    .PARAMETER Enabled
        Whether the rule takes part in synchronisation.

    .PARAMETER OutboundDeprovisionAction
        On an export rule, what happens to a Connected System Object when its Metaverse Object leaves the rule's
        scope or is deleted: Disconnect breaks the join and leaves the object in the Connected System, and Delete
        removes it from there. Disconnect when omitted.

    .PARAMETER ChangeReason
        An optional reason for the change, recorded against this Synchronisation Rule's change history.

    .PARAMETER PassThru
        Returns the rule created.

    .OUTPUTS
        With -PassThru, the rule created; otherwise nothing. A deletion source warning is written to the warning
        stream either way. AwaitingSynchronisation lists the Connected System Object Type an enabled import rule makes
        its Connected System's next synchronisation process in full, with how many objects that is and about how long it
        takes; it is empty for an export rule and for a rule created disabled, and a line saying the same is written to
        the information stream (shown with -InformationAction Continue).

    .EXAMPLE
        New-JIMSyncRule -Name "Import Users" -ConnectedSystemId 1 -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Import -ProjectToMetaverse

        Creates an import Synchronisation Rule that projects users to the Metaverse.

    .EXAMPLE
        New-JIMSyncRule -Name "Import Users" -ConnectedSystemName 'Contoso AD' -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Import -ProjectToMetaverse

        Creates an import Synchronisation Rule using the Connected System name.

    .EXAMPLE
        New-JIMSyncRule -Name "Export Users to AD" -ConnectedSystemId 2 -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Export -ProvisionToConnectedSystem -PassThru

        Creates an export Synchronisation Rule that provisions users to the Connected System.

    .EXAMPLE
        New-JIMSyncRule -Name "Import Users" -ConnectedSystemId 1 -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Import -Description "Imports user accounts from the HR system"

        Creates an import Synchronisation Rule with a description of what the rule does.

    .EXAMPLE
        New-JIMSyncRule -Name "Export Users to AD" -ConnectedSystemId 2 -ConnectedSystemObjectTypeId 1 -MetaverseObjectTypeId 1 -Direction Export -ProvisionToConnectedSystem -OutboundDeprovisionAction Delete

        Creates an export Synchronisation Rule that provisions users and deletes the corresponding objects from the Connected System when their Metaverse Objects are deleted or fall out of scope.

    .LINK
        Get-JIMSyncRule
        Set-JIMSyncRule
        Remove-JIMSyncRule
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType('JIM.SyncRuleSaveResponse')]
    param(
        [Parameter(Mandatory, Position = 0)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter()]
        [string]$Description,

        [Parameter(Mandatory, ParameterSetName = 'ById')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [string]$ConnectedSystemName,

        [Parameter(Mandatory)]
        [int]$ConnectedSystemObjectTypeId,

        [Parameter(Mandatory)]
        [int]$MetaverseObjectTypeId,

        [Parameter(Mandatory)]
        [ValidateSet('Import', 'Export')]
        [string]$Direction,

        [switch]$ProjectToMetaverse,

        [switch]$ProvisionToConnectedSystem,

        [bool]$Enabled = $true,

        [Parameter()]
        [ValidateSet('Disconnect', 'Delete')]
        [string]$OutboundDeprovisionAction,

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

        # Resolve ConnectedSystemName to ConnectedSystemId if specified
        if ($PSBoundParameters.ContainsKey('ConnectedSystemName')) {
            $connectedSystem = Resolve-JIMConnectedSystem -Name $ConnectedSystemName
            $ConnectedSystemId = $connectedSystem.id
        }

        if ($PSCmdlet.ShouldProcess($Name, "Create Synchronisation Rule")) {
            Write-Verbose "Creating Synchronisation Rule: $Name"

            $body = @{
                name = $Name
                connectedSystemId = $ConnectedSystemId
                connectedSystemObjectTypeId = $ConnectedSystemObjectTypeId
                metaverseObjectTypeId = $MetaverseObjectTypeId
                # Send the enum as its string name; -Direction is ValidateSet-constrained
                # to the exact SyncRuleDirection member names. The API rejects numeric
                # ordinals (JsonStringEnumConverter allowIntegerValues:false, PR #1060).
                direction = $Direction
                enabled = $Enabled
            }

            if ($PSBoundParameters.ContainsKey('Description')) {
                $body.description = $Description
            }

            if ($ProjectToMetaverse) {
                $body.projectToMetaverse = $true
            }

            if ($ProvisionToConnectedSystem) {
                $body.provisionToConnectedSystem = $true
            }

            if ($PSBoundParameters.ContainsKey('OutboundDeprovisionAction')) {
                $body.outboundDeprovisionAction = $OutboundDeprovisionAction
            }

            if ($PSBoundParameters.ContainsKey('ChangeReason')) {
                $body.changeReason = $ChangeReason
            }

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules" -Method 'POST' -Body $body

                Write-Verbose "Created Synchronisation Rule: $($result.id) ($($result.name))"

                # The new rule projects into a Metaverse Object Type deleted When Authoritative Source Disconnected, from a
                # system that is not one of its authoritative sources (#1256): reported, never blocking.
                if ($result.deletionSourceWarning) {
                    Write-Warning $result.deletionSourceWarning
                }

                # The Object Types the save made the next synchronisation process in full (#2615): said, never blocking.
                Write-JIMAwaitingSynchronisationInformation -AwaitingSynchronisation $result.AwaitingSynchronisation

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to create Synchronisation Rule: $_"
            }
        }
    }
}
