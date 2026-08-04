# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMSyncRuleInitialPassword {
    <#
    .SYNOPSIS
        Gets a Synchronisation Rule's initial password configuration.

    .DESCRIPTION
        Returns whether JIM sets an initial password on the accounts a Synchronisation Rule provisions, and
        how it generates one.

        A rule with nothing configured reports the setting switched off with JIM's defaults, which is how it
        behaves; there is no need to special-case rules nobody has touched.

        Also reports the accounts waiting on a person. Where a target refused the password these settings
        produced, JIM parks those accounts rather than retrying, and correcting the settings releases them;
        parkedReasons says what the target said, grouped by reason with the biggest group first, so a script
        can report the fault rather than only the count. expiredAccountCount is kept separate and is never
        added to it: those accounts were provisioned, never given a password within its time to live, and
        correcting these settings does nothing for them.

        No password value is ever returned. Passwords are generated at the moment they are set and are not
        stored by JIM.

    .PARAMETER Id
        The unique identifier of the Synchronisation Rule.

    .PARAMETER InputObject
        Synchronisation Rule object to read the configuration from (from pipeline).

    .OUTPUTS
        An object with the following properties:

        - enabled              [bool]    Whether JIM sets an initial password on accounts this rule provisions
        - source               [string]  Discovered or Custom
        - customPolicy         [object]  The generator settings used when source is Custom
        - expiryBehaviour      [string]  What happens to the password once it is set
        - enableAccount        [bool]    Whether the account is enabled once the password is set
        - parkedAccountCount   [int]     Accounts waiting on a change to these settings
        - expiredAccountCount  [int]     Accounts never given an initial password within its time to live
        - parkedReasons        [array]   One entry per distinct refusal, biggest group first, each with
                                         targetMessage, failureReason, accountCount and firstSeenAt

    .EXAMPLE
        Get-JIMSyncRuleInitialPassword -Id 5

        Gets the initial password configuration of the Synchronisation Rule with ID 5.

    .EXAMPLE
        Get-JIMSyncRule -Id 5 | Get-JIMSyncRuleInitialPassword

        Gets the same configuration by piping the Synchronisation Rule.

    .EXAMPLE
        (Get-JIMSyncRuleInitialPassword -Id 5).parkedReasons |
            Format-Table accountCount, targetMessage -AutoSize

        Shows what the target said about the accounts waiting on this rule, worst first. Correcting the
        settings named by the reason and saving releases them.

    .EXAMPLE
        Get-JIMSyncRule -All | ForEach-Object {
            $p = Get-JIMSyncRuleInitialPassword -Id $_.id
            if ($p.parkedAccountCount -or $p.expiredAccountCount) {
                [PSCustomObject]@{ Rule = $_.name; Parked = $p.parkedAccountCount; Expired = $p.expiredAccountCount }
            }
        }

        Reports every Synchronisation Rule with initial password work waiting on somebody.

    .EXAMPLE
        Get-JIMSyncRule -All | Where-Object provisionToConnectedSystem |
            ForEach-Object { [PSCustomObject]@{ Rule = $_.name; Password = (Get-JIMSyncRuleInitialPassword -Id $_.id).enabled } }

        Reports which provisioning Synchronisation Rules set an initial password.

    .LINK
        Set-JIMSyncRuleInitialPassword
        Get-JIMSyncRule
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

        $ruleId = if ($InputObject) { $InputObject.id } else { $Id }

        Write-Verbose "Getting the initial password configuration of Synchronisation Rule: $ruleId"

        try {
            Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$ruleId/initial-password" -Method 'GET'
        }
        catch {
            Write-Error "Failed to get the initial password configuration: $_"
        }
    }
}
