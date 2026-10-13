# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Test-JIMScope {
    <#
    .SYNOPSIS
        Tests whether an object would be in scope of a Synchronisation Rule.

    .DESCRIPTION
        Evaluates a Synchronisation Rule's scoping criteria against an object holding the given values, by the same
        code a synchronisation runs, and explains the outcome criterion by criterion. The values can be samples, a
        real object's, or both: a Connected System Object for an import rule, a Metaverse Object for an export rule.

        Relative date criteria, such as "Leave Date is after 30 days ago", resolve against AsOf, or the current time
        when it is not given. NextChange says when the outcome would next change as time passes, the values staying
        as they are.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule whose scoping criteria to test.

    .PARAMETER Attributes
        A hashtable of sample values for the attributes the scoping criteria compare, keyed by attribute name. Each
        value is converted to its attribute's type; $null is no value, and an array gives a multi-valued attribute
        several values. With ConnectedSystemObjectId or MetaverseObjectId, a value here replaces the object's value
        for that attribute.

    .PARAMETER ConnectedSystemObjectId
        The ID of a Connected System Object whose values to test with, for an import rule.

    .PARAMETER MetaverseObjectId
        The ID of a Metaverse Object whose values to test with, for an export rule.

    .PARAMETER AsOf
        The instant to test as of, which relative date criteria resolve against. A time without a time zone is read
        as UTC.

    .OUTPUTS
        Whether the object would be in scope (Outcome), the scoping criteria as they evaluated (Explanation, the same
        tree the portal's Scope tab shows), the values tested for each compared attribute (Inputs) and when the
        outcome would next change (NextChange). When the values cannot be used, IsValid is $false and InputErrors
        says why.

    .EXAMPLE
        Test-JIMScope -SyncRuleId 12 -Attributes @{ department = 'Finance'; leaveDate = '2026-11-01' }

        Tests whether a Connected System Object in the Finance department with that leave date would be in scope of
        import Synchronisation Rule 12 now, and when that would change.

    .EXAMPLE
        Test-JIMScope -SyncRuleId 12 -ConnectedSystemObjectId '0b0e1c5a-6d3a-4c7e-9a43-6a7b5d2f1e01' -AsOf '2027-01-31'

        Tests a real Connected System Object's scope as it would be on 31 January 2027.

    .EXAMPLE
        (Test-JIMScope -SyncRuleId 15 -MetaverseObjectId '6f9619ff-8b86-d011-b42d-00c04fc964ff').Explanation.Criteria |
            Select-Object Description, Met, ActualDescription

        Lists each criterion of export Synchronisation Rule 15 with whether a real Metaverse Object meets it and what
        the object holds.

    .LINK
        Get-JIMScopingCriteria
        New-JIMScopingCriterion
    #>
    [CmdletBinding(DefaultParameterSetName = 'Samples')]
    [OutputType('JIM.TestScopeResponse')]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$SyncRuleId,

        [Parameter()]
        [hashtable]$Attributes,

        [Parameter(Mandatory, ParameterSetName = 'ConnectedSystemObject')]
        [guid]$ConnectedSystemObjectId,

        [Parameter(Mandatory, ParameterSetName = 'MetaverseObject')]
        [guid]$MetaverseObjectId,

        [Parameter()]
        [datetime]$AsOf
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $body = @{}

        if ($Attributes) {
            $body.attributes = $Attributes
        }

        if ($PSBoundParameters.ContainsKey('ConnectedSystemObjectId')) {
            $body.connectedSystemObjectId = $ConnectedSystemObjectId
        }

        if ($PSBoundParameters.ContainsKey('MetaverseObjectId')) {
            $body.metaverseObjectId = $MetaverseObjectId
        }

        if ($PSBoundParameters.ContainsKey('AsOf')) {
            # ISO 8601 with no offset unless one was given, so the API reads a bare date or time as UTC.
            $body.asOf = if ($AsOf.Kind -eq [DateTimeKind]::Unspecified) { $AsOf.ToString('yyyy-MM-ddTHH:mm:ss') } else { $AsOf.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') }
        }

        Write-Verbose "Testing the scope of Synchronisation Rule $SyncRuleId"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/sync-rules/$SyncRuleId/scoping-criteria/test" -Method 'POST' -Body $body

            if (-not $result.isValid) {
                Write-Verbose "The values could not be used: $($result.inputErrors -join ' ')"
            }

            # The API's own object, so every property it reports reaches the caller.
            $result
        }
        catch {
            Write-Error "Failed to test the scope of Synchronisation Rule ${SyncRuleId}: $_"
        }
    }
}
