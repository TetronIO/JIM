# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMConnectedSystemContainer {
    <#
    .SYNOPSIS
        Chooses whether, and how deep, JIM imports from a container.

    .DESCRIPTION
        Chooses whether JIM imports objects from a container of a Connected System, and how far beneath it. The
        partition holding the container must be selected too.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .PARAMETER ContainerId
        The ID of the container to change.

    .PARAMETER Selected
        Whether JIM imports objects from the container.

    .PARAMETER Excluded
        Whether the container is carved out of a selection a container above it made, so its objects are
        deliberately not imported. A container cannot be both selected and excluded: to replace a selection with an
        exclusion, give -Selected $false -Excluded $true together. Omit it to leave the exclusion as it is.

    .PARAMETER Scope
        How far beneath the container objects are imported from, when it is selected: Subtree imports from the
        container and every container beneath it; OneLevel only from the container itself, leaving the containers
        beneath it to be selected in their own right. Omit it to leave the scope as it is.

    .PARAMETER PassThru
        Returns the container as changed.

    .OUTPUTS
        With -PassThru, the container as changed; otherwise nothing.

    .EXAMPLE
        Set-JIMConnectedSystemContainer -ConnectedSystemId 1 -ContainerId 10 -Selected $true

        Selects the container for import operations.

    .EXAMPLE
        Get-JIMConnectedSystemPartition -ConnectedSystemId 1 |
            ForEach-Object { $_.containers } |
            Where-Object { $_.name -eq "Users" } |
            ForEach-Object { Set-JIMConnectedSystemContainer -ConnectedSystemId 1 -ContainerId $_.id -Selected $true }

        Selects the "Users" container from all partitions.

    .EXAMPLE
        Set-JIMConnectedSystemContainer -ConnectedSystemId 1 -ContainerId 10 -Selected $true -Scope OneLevel

        Selects the container and imports only the objects held directly in it, ignoring the containers beneath it.

    .EXAMPLE
        Set-JIMConnectedSystemContainer -ConnectedSystemId 1 -ContainerId 10 -Scope Subtree

        Widens an already selected container back to importing its whole subtree, leaving its selection unchanged.

    .EXAMPLE
        Set-JIMConnectedSystemContainer -ConnectedSystemId 1 -ContainerId 12 -Excluded $true

        Carves the container out of the selection an ancestor made. Objects within it, and within every
        container beneath it, are left unimported. A container beneath it can be selected in its own
        right to bring that branch back into scope.

    .EXAMPLE
        Set-JIMConnectedSystemContainer -ConnectedSystemId 1 -ContainerId 12 -Excluded $false

        Hands the container back to whatever its ancestors say, undoing the exclusion. This does not
        select the container.

    .LINK
        Get-JIMConnectedSystemPartition
        Set-JIMConnectedSystemPartition
        Get-JIMConnectedSystem
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType('JIM.ConnectedSystemContainerDto')]
    param(
        [Parameter(Mandatory)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ContainerId,

        [Parameter()]
        [bool]$Selected,

        [Parameter()]
        [bool]$Excluded,

        [Parameter()]
        [ValidateSet('Subtree', 'OneLevel')]
        [string]$Scope,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Build update body
        $body = @{}

        if ($PSBoundParameters.ContainsKey('Selected')) {
            $body.selected = $Selected
        }

        # Omitted rather than defaulted, so a caller changing scope cannot silently hand an excluded branch back
        # into scope, exactly as an omitted -Scope leaves a OneLevel container alone.
        if ($PSBoundParameters.ContainsKey('Excluded')) {
            $body.excluded = $Excluded
        }

        if ($PSBoundParameters.ContainsKey('Scope')) {
            $body.scope = $Scope
        }

        if ($body.Count -eq 0) {
            Write-Warning "No updates specified."
            return
        }

        if ($PSCmdlet.ShouldProcess("Container $ContainerId in Connected System $ConnectedSystemId", "Update")) {
            Write-Verbose "Updating Container: $ContainerId in Connected System: $ConnectedSystemId"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/containers/$ContainerId" -Method 'PUT' -Body $body

                Write-Verbose "Updated Container: $ContainerId"

                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update Container: $_"
            }
        }
    }
}
