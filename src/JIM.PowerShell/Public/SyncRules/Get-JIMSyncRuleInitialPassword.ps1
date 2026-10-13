# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMSyncRuleInitialPassword {
    <#
    .SYNOPSIS
        Gets a Synchronisation Rule's initial password settings, and the Connected System Objects waiting on them.

    .DESCRIPTION
        Gets whether JIM sets an initial password on the Connected System Objects a Synchronisation Rule provisions,
        how it generates one, and which Connected System Objects are waiting on a person. A rule with nothing
        configured reports the setting switched off with JIM's defaults, which is how it behaves.

        Where a target refused the password these settings produced, JIM parks the Connected System Object rather
        than retrying, and correcting the settings releases it. ParkedReasons says what the target said, grouped by
        reason with the biggest group first. ExpiredAccountCount is separate and never added to it: those Connected
        System Objects were never given a password within its time to live, and correcting these settings does
        nothing for them.

        No password is ever returned. A generated password is produced at the moment it is set and stored nowhere.
        Where the rule sets one static password, that password is stored encrypted and is write-only, so all that
        comes back is that one is set and when it last changed.

    .PARAMETER Id
        The ID of the Synchronisation Rule.

    .PARAMETER InputObject
        A Synchronisation Rule from Get-JIMSyncRule, through the pipeline.

    .OUTPUTS
        The rule's initial password settings, and the Connected System Objects waiting on them.

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
        Get-JIMSyncRule | ForEach-Object {
            $p = Get-JIMSyncRuleInitialPassword -Id $_.id
            if ($p.parkedAccountCount -or $p.expiredAccountCount) {
                [PSCustomObject]@{ Rule = $_.name; Parked = $p.parkedAccountCount; Expired = $p.expiredAccountCount }
            }
        }

        Reports every Synchronisation Rule with initial password work waiting on somebody.

    .EXAMPLE
        Get-JIMSyncRule | ForEach-Object {
            $p = Get-JIMSyncRuleInitialPassword -Id $_.id
            if ($p.staticPasswordSet -and $p.staticPasswordSetAt -lt (Get-Date).AddDays(-90)) {
                [PSCustomObject]@{ Rule = $_.name; LastChanged = $p.staticPasswordSetAt }
            }
        }

        Finds the Synchronisation Rules whose shared initial password has not been changed for 90 days. The
        password itself is never returned, which is why the date is what a rotation check has to work from.

    .EXAMPLE
        Get-JIMSyncRule | Where-Object provisionToConnectedSystem |
            ForEach-Object { [PSCustomObject]@{ Rule = $_.name; Password = (Get-JIMSyncRuleInitialPassword -Id $_.id).enabled } }

        Reports which provisioning Synchronisation Rules set an initial password.

    .LINK
        Set-JIMSyncRuleInitialPassword
        Get-JIMSyncRule
    #>
    [CmdletBinding(DefaultParameterSetName = 'ById')]
    [OutputType('JIM.SyncRuleInitialPasswordResponse')]
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
