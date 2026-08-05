# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemPasswordPolicy {
    <#
    .SYNOPSIS
        Gets the password policy JIM discovered on a Connected System.

    .DESCRIPTION
        Reports what the Connected System itself said it will accept, read during a previous connection.
        Nothing here opens a new connection or changes anything.

        Every value can be null, and a null means JIM could not read that rule rather than that no such rule
        exists: a directory withholds what a caller may not see by omitting it rather than refusing. Check
        HasAnyDiscoveredConstraint before treating the figures as a description of what the system will accept.

        Where a domain has password policies that apply to only some accounts, the figures are a floor rather
        than a guarantee; FineGrainedPolicySignal says which case this is.

    .PARAMETER Id
        The unique identifier of the Connected System.

    .PARAMETER InputObject
        Connected System object to read the policy from (from pipeline).

    .OUTPUTS
        An object with the following properties:

        - discovered                  [datetime?] When JIM last read this from the system
        - minimumLength               [int?]      The shortest password the system will accept
        - complexityRequired          [bool?]     Whether the system enforces a complexity rule
        - requiredCharacterClassCount [int?]      How many character categories a password must draw on
        - recognisedCharacterClasses  [string[]]  The categories this system counts towards that rule
        - passwordHistoryLength       [int?]      How many previous passwords it remembers and refuses
        - maximumPasswordAgeDays      [int?]      How long a password may live
        - minimumPasswordAgeDays      [int?]      How soon it may be changed again
        - fineGrainedPolicySignal     [string]    Absent, Present or CouldNotDetermine
        - hasAnyDiscoveredConstraint  [bool]      Whether JIM discovered anything at all

    .EXAMPLE
        Get-JIMConnectedSystemPasswordPolicy -Id 3

        Shows what the Connected System with ID 3 demands of a password.

    .EXAMPLE
        Get-JIMConnectedSystem -All | ForEach-Object {
            $p = Get-JIMConnectedSystemPasswordPolicy -Id $_.Id
            [PSCustomObject]@{ System = $_.Name; MinimumLength = $p.minimumLength; Known = $p.hasAnyDiscoveredConstraint }
        }

        Compares what every Connected System demands, and says where JIM could not find out.

    .LINK
        Set-JIMConnectedSystemObjectPassword
        Get-JIMSyncRuleInitialPassword
    #>
    [CmdletBinding(DefaultParameterSetName = 'ById')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'ByInputObject', ValueFromPipeline)]
        [PSCustomObject]$InputObject
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $connectedSystemId = if ($PSCmdlet.ParameterSetName -eq 'ByInputObject') { $InputObject.Id } else { $Id }

        try {
            Invoke-JIMApi -Endpoint "/api/v1/synchronisation/connected-systems/$connectedSystemId/password-policy" -Method 'GET'
        }
        catch {
            Write-Error "Failed to get the password policy for Connected System ${connectedSystemId}: $_"
        }
    }
}
