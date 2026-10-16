# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Invoke-JIMConfigurationTest {
    <#
    .SYNOPSIS
        Runs saved configuration test cases.

    .DESCRIPTION
        Runs the configuration test cases covering a Synchronisation Rule, those under a Connected System, or those
        piped in, against the configuration as saved, by the same code synchronisation runs. Each result says Passed;
        Failed, with what was expected and what came; NeedsReview, when the configuration now reads an input the case
        gives no value for or an input left the schema; or Error. Results are computed for this run and never stored.

        With -FailOnUnsuccessful, every case that did not pass is also written as an error, so a script or pipeline
        can stop on it.

    .PARAMETER SyncRuleId
        Runs the cases covering this Synchronisation Rule: its Scope, its Attribute Flows and its own Object Matching
        Rules.

    .PARAMETER ConnectedSystemId
        Runs the cases under this Connected System: each of its Synchronisation Rules', and its Simple mode Object
        Matching Rules'.

    .PARAMETER Id
        The IDs of the test cases to run. Cases piped in are run together, in one request.

    .PARAMETER FailOnUnsuccessful
        Writes an error for each case that did not pass, after returning every result.

    .OUTPUTS
        Each case's result.

    .EXAMPLE
        Invoke-JIMConfigurationTest -SyncRuleId 12

        Runs every test case covering Synchronisation Rule 12.

    .EXAMPLE
        Invoke-JIMConfigurationTest -ConnectedSystemId 3 | Where-Object Status -ne 'Passed' | Select-Object TestCaseName, Status, Summary

        Runs every test case under Connected System 3 and lists those that did not pass, with why.

    .EXAMPLE
        Invoke-JIMConfigurationTest -ConnectedSystemId 3 -FailOnUnsuccessful -ErrorAction Stop

        Runs every test case under Connected System 3 and stops the script if any did not pass, as a deployment
        pipeline would.

    .EXAMPLE
        Get-JIMConfigurationTestCase -SyncRuleId 12 -Name 'Hire date*' | Invoke-JIMConfigurationTest

        Runs only the cases covering Synchronisation Rule 12 whose names start with "Hire date".

    .LINK
        Get-JIMConfigurationTestCase
        New-JIMConfigurationTestCase
    #>
    [CmdletBinding(DefaultParameterSetName = 'ById')]
    [OutputType('JIM.ConfigurationTestRunResultDto')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'BySyncRule')]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ParameterSetName = 'ByConnectedSystem')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int[]]$Id,

        [Parameter()]
        [switch]$FailOnUnsuccessful
    )

    begin {
        $ids = [System.Collections.Generic.List[int]]::new()
    }

    process {
        if ($PSCmdlet.ParameterSetName -eq 'ById') {
            $ids.AddRange($Id)
        }
    }

    end {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $body = switch ($PSCmdlet.ParameterSetName) {
            'BySyncRule' { @{ syncRuleId = $SyncRuleId } }
            'ByConnectedSystem' { @{ connectedSystemId = $ConnectedSystemId } }
            default { @{ ids = @($ids) } }
        }

        Write-Verbose 'Running configuration test cases'

        try {
            $results = @(Invoke-JIMApi -Endpoint '/api/v1/configuration-tests/run' -Method 'POST' -Body $body)
        }
        catch {
            Write-Error "Failed to run configuration test cases: $_"
            return
        }

        $results

        if ($FailOnUnsuccessful) {
            foreach ($result in $results | Where-Object { $_.status -ne 'Passed' }) {
                Write-Error "Configuration test case '$($result.testCaseName)' ($($result.testCaseId)) $($result.status): $($result.summary)"
            }
        }
    }
}
