# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConfigurationTestCase {
    <#
    .SYNOPSIS
        Gets saved configuration test cases.

    .DESCRIPTION
        Gets saved configuration test cases: inputs, and what an Attribute Flow, Generated Value, Scope or Object
        Matching Rule should give for them. Lists every case, those covering one Synchronisation Rule, or those
        under one Connected System, or gets one by its ID. Each case comes back with its inputs.

        Inputs copied from a real object can hold personal data; handle the output as you would the object.

    .PARAMETER Id
        The ID of the test case to get.

    .PARAMETER SyncRuleId
        Lists the cases covering this Synchronisation Rule: its Scope, its Attribute Flows and its own Object Matching
        Rules.

    .PARAMETER ConnectedSystemId
        Lists the cases under this Connected System: each of its Synchronisation Rules', and its Simple mode Object
        Matching Rules'.

    .PARAMETER Name
        Lists only the cases whose name matches.

    .OUTPUTS
        Each test case, with its inputs (CsAttributes and MvAttributes for an Attribute Flow or Generated Value case,
        Attributes otherwise) and its Expectation.

    .EXAMPLE
        Get-JIMConfigurationTestCase -SyncRuleId 12

        Lists the test cases covering Synchronisation Rule 12.

    .EXAMPLE
        Get-JIMConfigurationTestCase -ConnectedSystemId 3 -Name 'Hire date*' | Select-Object Id, Name, Kind

        Lists the test cases under Connected System 3 whose names start with "Hire date".

    .EXAMPLE
        Get-JIMConfigurationTestCase -Id 4

        Gets test case 4.

    .LINK
        New-JIMConfigurationTestCase
        Set-JIMConfigurationTestCase
        Remove-JIMConfigurationTestCase
        Invoke-JIMConfigurationTest
    #>
    [CmdletBinding(DefaultParameterSetName = 'All')]
    [OutputType('JIM.ConfigurationTestCaseDto')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter(Mandatory, ParameterSetName = 'BySyncRule')]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ParameterSetName = 'ByConnectedSystem')]
        [int]$ConnectedSystemId,

        [Parameter(ParameterSetName = 'All')]
        [Parameter(ParameterSetName = 'BySyncRule')]
        [Parameter(ParameterSetName = 'ByConnectedSystem')]
        [SupportsWildcards()]
        [string]$Name
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        try {
            if ($PSCmdlet.ParameterSetName -eq 'ById') {
                Write-Verbose "Getting configuration test case $Id"
                Invoke-JIMApi -Endpoint "/api/v1/configuration-tests/$Id"
                return
            }

            $endpoint = switch ($PSCmdlet.ParameterSetName) {
                'BySyncRule' { "/api/v1/configuration-tests?syncRuleId=$SyncRuleId&include=inputs" }
                'ByConnectedSystem' { "/api/v1/configuration-tests?connectedSystemId=$ConnectedSystemId&include=inputs" }
                default { '/api/v1/configuration-tests?include=inputs' }
            }

            Write-Verbose 'Getting configuration test cases'
            $testCases = @(Invoke-JIMApi -Endpoint $endpoint)
            if ($Name) {
                $testCases = @($testCases | Where-Object { $_.name -like $Name })
            }
            $testCases
        }
        catch {
            Write-Error "Failed to get configuration test cases: $_"
        }
    }
}
