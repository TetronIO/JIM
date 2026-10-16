# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Set-JIMConfigurationTestCase {
    <#
    .SYNOPSIS
        Changes a saved configuration test case.

    .DESCRIPTION
        Changes a test case's name, description, inputs, run-as-of time or expectation. Only what is given changes.
        Each input hashtable given replaces the case's inputs on that side; the other side stays as it is. What the
        case covers and what it tests cannot change: save another case to test something else.

    .PARAMETER Id
        The ID of the test case to change.

    .PARAMETER Name
        A new name.

    .PARAMETER Description
        A new description. $null (or '') clears it.

    .PARAMETER CsAttributes
        For an Attribute Flow or Generated Value case, the Connected System Object's values, replacing those the case
        holds for that side. $null is no value, and an array gives several values.

    .PARAMETER MvAttributes
        For an Attribute Flow or Generated Value case, the Metaverse Object's values, replacing those the case holds
        for that side.

    .PARAMETER Attributes
        For a Scope or Object Matching Rule case, the object's values, replacing those the case holds.

    .PARAMETER RunAsOf
        A new instant to run the case as of. A time without a time zone is read as UTC.

    .PARAMETER ClearRunAsOf
        Runs the case as of the time it runs rather than a fixed instant.

    .PARAMETER Expect
        A new expected outcome, replacing the case's expectation: Values, NoValue, Error or AnyValue for an Attribute
        Flow, Generated Value or Object Matching Rule case; InScope or OutOfScope for a Scope case.

    .PARAMETER ExpectedValues
        With -Expect Values, the values expected.

    .PARAMETER InOrder
        With -Expect Values, requires the values to come in the order given.

    .PARAMETER PassThru
        Returns the changed test case.

    .OUTPUTS
        With -PassThru, the changed test case; otherwise nothing.

    .EXAMPLE
        Set-JIMConfigurationTestCase -Id 4 -Expect Values -ExpectedValues 'BLOGGS'

        Changes what test case 4 expects.

    .EXAMPLE
        Set-JIMConfigurationTestCase -Id 4 -CsAttributes @{ sn = 'Smith'; givenName = $null }

        Replaces test case 4's Connected System Object values, giving givenName no value. Its Metaverse Object values
        stay as they are.

    .EXAMPLE
        Get-JIMConfigurationTestCase -SyncRuleId 12 -Name 'Leavers*' | Set-JIMConfigurationTestCase -ClearRunAsOf

        Runs every case covering Synchronisation Rule 12 whose name starts with "Leavers" as of the time it runs.

    .LINK
        Get-JIMConfigurationTestCase
        New-JIMConfigurationTestCase
        Remove-JIMConfigurationTestCase
    #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType('JIM.ConfigurationTestCaseDto')]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter()]
        [ValidateLength(1, 200)]
        [string]$Name,

        [Parameter()]
        [string]$Description,

        [Parameter()]
        [hashtable]$CsAttributes,

        [Parameter()]
        [hashtable]$MvAttributes,

        [Parameter()]
        [hashtable]$Attributes,

        [Parameter()]
        [datetime]$RunAsOf,

        [Parameter()]
        [switch]$ClearRunAsOf,

        [Parameter()]
        [ValidateSet('Values', 'NoValue', 'Error', 'AnyValue', 'InScope', 'OutOfScope')]
        [string]$Expect,

        [Parameter()]
        [object[]]$ExpectedValues,

        [Parameter()]
        [switch]$InOrder,

        [Parameter()]
        [switch]$PassThru
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($ClearRunAsOf -and $PSBoundParameters.ContainsKey('RunAsOf')) {
            Write-Error 'Give -RunAsOf or -ClearRunAsOf, not both.'
            return
        }

        if (($PSBoundParameters.ContainsKey('ExpectedValues') -or $InOrder) -and -not $PSBoundParameters.ContainsKey('Expect')) {
            Write-Error 'Give -Expect with -ExpectedValues or -InOrder: they replace the whole expectation.'
            return
        }

        $body = @{}
        if ($PSBoundParameters.ContainsKey('Name')) { $body.name = $Name }
        # $null and '' both clear the description; the binder makes them the same empty string.
        if ($PSBoundParameters.ContainsKey('Description')) { $body.description = "$Description" }
        if ($PSBoundParameters.ContainsKey('CsAttributes')) { $body.csAttributes = ConvertTo-JIMTestCaseAttributes $CsAttributes }
        if ($PSBoundParameters.ContainsKey('MvAttributes')) { $body.mvAttributes = ConvertTo-JIMTestCaseAttributes $MvAttributes }
        if ($PSBoundParameters.ContainsKey('Attributes')) { $body.attributes = ConvertTo-JIMTestCaseAttributes $Attributes }
        if ($PSBoundParameters.ContainsKey('RunAsOf')) { $body.runAsOf = ConvertTo-JIMTestCaseValue $RunAsOf }
        if ($ClearRunAsOf) { $body.clearRunAsOf = $true }
        if ($PSBoundParameters.ContainsKey('Expect')) {
            $body.expectation = @{
                kind = $Expect
                values = @(if ($ExpectedValues) { $ExpectedValues | ForEach-Object { ConvertTo-JIMTestCaseValue $_ } })
                inOrder = [bool]$InOrder
            }
        }

        if ($PSCmdlet.ShouldProcess("Configuration test case $Id", 'Update')) {
            Write-Verbose "Updating configuration test case $Id"

            try {
                $result = Invoke-JIMApi -Endpoint "/api/v1/configuration-tests/$Id" -Method 'PUT' -Body $body
                if ($PassThru) {
                    $result
                }
            }
            catch {
                Write-Error "Failed to update configuration test case ${Id}: $_"
            }
        }
    }
}
