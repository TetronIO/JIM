# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMConfigurationTestCase {
    <#
    .SYNOPSIS
        Saves a configuration test case.

    .DESCRIPTION
        Saves inputs, and what an Attribute Flow, Generated Value, Scope or Object Matching Rule should give for them,
        so Invoke-JIMConfigurationTest can check that a later change has not broken it. The case covers exactly one
        thing, is deleted with it, and is recorded in its Synchronisation Rule's Configuration History (the Connected
        System's for a Simple mode Object Matching Rule).

        Inputs take the same shape Test-JIMExpression, Test-JIMScope and Test-JIMMatchingRule take, so a test tried
        with one of them can be saved with the same values. Give every attribute the configuration reads, with $null
        for one that should have no value; a case missing one needs review rather than running.

        Inputs are stored unencrypted, as other configuration is. Prefer made-up values to a real person's.

    .PARAMETER Name
        What the case checks, in your own words.

    .PARAMETER Description
        A longer description.

    .PARAMETER SyncRuleMappingId
        The Attribute Flow the case covers. The case tests its Expression, or for a Generated Value its base.

    .PARAMETER Kind
        For an Attribute Flow, AttributeFlow (the default) or GeneratedValue, which tests a Generated Value's base: the
        value before the uniqueness token is added.

    .PARAMETER SyncRuleId
        The Synchronisation Rule whose Scope the case covers.

    .PARAMETER ObjectMatchingRuleId
        The Object Matching Rule the case covers. The case compares the rule's match values and never looks them up.

    .PARAMETER CsAttributes
        For an Attribute Flow or Generated Value case, the Connected System Object's values, keyed by attribute name.
        $null is no value, and an array gives several values. A [datetime] is sent as ISO 8601.

    .PARAMETER MvAttributes
        For an Attribute Flow or Generated Value case, the Metaverse Object's values, keyed by attribute name, as for
        CsAttributes.

    .PARAMETER Attributes
        For a Scope or Object Matching Rule case, the object's values, keyed by attribute name, as for CsAttributes.

    .PARAMETER RunAsOf
        The instant to run the case as of: what Now() and Today() read, and what relative date criteria resolve
        against. Omit it to run as of the time the case runs. A time without a time zone is read as UTC.

    .PARAMETER Expect
        The outcome expected. Values, NoValue, Error or AnyValue for an Attribute Flow, Generated Value or Object
        Matching Rule case; InScope or OutOfScope for a Scope case.

    .PARAMETER ExpectedValues
        With -Expect Values, the values expected. Each is compared with what the configuration gives as a value of its
        type, so a date can be written 2026-03-02.

    .PARAMETER InOrder
        With -Expect Values, requires the values to come in the order given. By default they compare in any order,
        each repeat counted.

    .OUTPUTS
        The saved test case.

    .EXAMPLE
        New-JIMConfigurationTestCase -Name 'Hire date as ISO' -SyncRuleMappingId 9 -CsAttributes @{ hireDate = '2026-03-02' } -Expect Values -ExpectedValues '2026-03-02'

        Saves a case checking that Attribute Flow 9 turns a hire date of 2 March 2026 into the text 2026-03-02.

    .EXAMPLE
        New-JIMConfigurationTestCase -Name 'Leavers drop out of scope' -SyncRuleId 12 -Attributes @{ leaveDate = '2026-01-15' } -RunAsOf '2026-03-01' -Expect OutOfScope

        Saves a case checking that someone who left on 15 January is out of scope of Synchronisation Rule 12 on 1
        March, whatever the date the case runs.

    .EXAMPLE
        New-JIMConfigurationTestCase -Name 'Account name base' -SyncRuleMappingId 14 -Kind GeneratedValue -MvAttributes @{ 'First Name' = 'Joe'; 'Last Name' = 'Bloggs' } -Expect Values -ExpectedValues 'jbloggs'

        Saves a case checking the base a Generated Value builds before its uniqueness token.

    .LINK
        Get-JIMConfigurationTestCase
        Set-JIMConfigurationTestCase
        Invoke-JIMConfigurationTest
        Test-JIMExpression
    #>
    [CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'AttributeFlow')]
    [OutputType('JIM.ConfigurationTestCaseDto')]
    param(
        [Parameter(Mandatory)]
        [ValidateLength(1, 200)]
        [string]$Name,

        [Parameter()]
        [string]$Description,

        [Parameter(Mandatory, ParameterSetName = 'AttributeFlow')]
        [int]$SyncRuleMappingId,

        [Parameter(ParameterSetName = 'AttributeFlow')]
        [ValidateSet('AttributeFlow', 'GeneratedValue')]
        [string]$Kind = 'AttributeFlow',

        [Parameter(Mandatory, ParameterSetName = 'Scope')]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ParameterSetName = 'ObjectMatchingRule')]
        [int]$ObjectMatchingRuleId,

        [Parameter(ParameterSetName = 'AttributeFlow')]
        [hashtable]$CsAttributes,

        [Parameter(ParameterSetName = 'AttributeFlow')]
        [hashtable]$MvAttributes,

        [Parameter(ParameterSetName = 'Scope')]
        [Parameter(ParameterSetName = 'ObjectMatchingRule')]
        [hashtable]$Attributes,

        [Parameter()]
        [datetime]$RunAsOf,

        [Parameter(Mandatory)]
        [ValidateSet('Values', 'NoValue', 'Error', 'AnyValue', 'InScope', 'OutOfScope')]
        [string]$Expect,

        [Parameter()]
        [object[]]$ExpectedValues,

        [Parameter()]
        [switch]$InOrder
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $body = @{
            name = $Name
            expectation = @{
                kind = $Expect
                values = @(if ($ExpectedValues) { $ExpectedValues | ForEach-Object { ConvertTo-JIMTestCaseValue $_ } })
                inOrder = [bool]$InOrder
            }
        }

        switch ($PSCmdlet.ParameterSetName) {
            'AttributeFlow' {
                $body.kind = $Kind
                $body.syncRuleMappingId = $SyncRuleMappingId
                if ($CsAttributes) { $body.csAttributes = ConvertTo-JIMTestCaseAttributes $CsAttributes }
                if ($MvAttributes) { $body.mvAttributes = ConvertTo-JIMTestCaseAttributes $MvAttributes }
            }
            'Scope' {
                $body.kind = 'Scope'
                $body.syncRuleId = $SyncRuleId
                if ($Attributes) { $body.attributes = ConvertTo-JIMTestCaseAttributes $Attributes }
            }
            'ObjectMatchingRule' {
                $body.kind = 'ObjectMatchingRule'
                $body.objectMatchingRuleId = $ObjectMatchingRuleId
                if ($Attributes) { $body.attributes = ConvertTo-JIMTestCaseAttributes $Attributes }
            }
        }

        if ($PSBoundParameters.ContainsKey('Description')) {
            $body.description = $Description
        }

        if ($PSBoundParameters.ContainsKey('RunAsOf')) {
            $body.runAsOf = ConvertTo-JIMTestCaseValue $RunAsOf
        }

        if ($PSCmdlet.ShouldProcess($Name, 'Create configuration test case')) {
            Write-Verbose "Creating configuration test case '$Name'"

            try {
                Invoke-JIMApi -Endpoint '/api/v1/configuration-tests' -Method 'POST' -Body $body
            }
            catch {
                Write-Error "Failed to create configuration test case: $_"
            }
        }
    }
}
