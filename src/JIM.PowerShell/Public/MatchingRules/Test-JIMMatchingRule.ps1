# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Test-JIMMatchingRule {
    <#
    .SYNOPSIS
        Tests what an Object Matching Rule would match on, and optionally what it would join.

    .DESCRIPTION
        Works out what a saved Object Matching Rule would compare for a Connected System Object holding the given
        values, by the same code a synchronisation runs: each value is read as its attribute's type, and the rule's
        source attribute or Expression gives the values to match on, in the Metaverse attribute's type.

        With -LookUp, the values are also looked up in the Metaverse as an import join would look them up, and Lookup
        says what a synchronisation would do: join a Metaverse Object, find none, or find several. Nothing is written.

        The values can be samples, a real Connected System Object's, or both.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System the rule belongs to, for a rule on a Connected System Object Type.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule the rule belongs to, for a rule on a Synchronisation Rule.

    .PARAMETER Id
        The ID of the Object Matching Rule to test.

    .PARAMETER Attributes
        A hashtable of sample values for the attributes the rule reads, keyed by attribute name. Each value is converted
        to its attribute's type; $null is no value, and an array gives a multi-valued attribute several values. With
        ConnectedSystemObjectId, a value here replaces the object's value for that attribute.

    .PARAMETER ConnectedSystemObjectId
        The ID of a Connected System Object of the rule's Object Type whose values to test with.

    .PARAMETER LookUp
        Also looks the values up in the Metaverse and reports what a synchronisation would do. Nothing is written.

    .OUTPUTS
        What the rule would match on (Outcome, ValueLines and ValueType, or FailureMessage when it fails), the values
        tested for each attribute it reads (Inputs) and, with -LookUp, what the values find in the Metaverse (Lookup).
        When the values cannot be used, IsValid is $false and InputErrors says why.

    .EXAMPLE
        Test-JIMMatchingRule -ConnectedSystemId 3 -Id 40 -Attributes @{ employeeNumber = 'EMP-48213' }

        Shows the value Object Matching Rule 40 would match on for an object with that employee number.

    .EXAMPLE
        Test-JIMMatchingRule -ConnectedSystemId 3 -Id 40 -ConnectedSystemObjectId '0b0e1c5a-6d3a-4c7e-9a43-6a7b5d2f1e01' -LookUp

        Tests a real Connected System Object and reports which Metaverse Object a synchronisation would join it to.

    .EXAMPLE
        (Test-JIMMatchingRule -SyncRuleId 12 -Id 7 -Attributes @{ mail = 'jo@corp.example', 'j.bloggs@corp.example' } -LookUp).Lookup

        Tests a rule on Synchronisation Rule 12 with two email addresses, and shows whether they identify one Metaverse
        Object or several.

    .LINK
        Get-JIMMatchingRule
        Get-JIMSyncRuleMatchingRule
    #>
    [CmdletBinding(DefaultParameterSetName = 'ConnectedSystem')]
    [OutputType('JIM.TestMatchingRuleResponse')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ConnectedSystem', ValueFromPipelineByPropertyName)]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'SyncRule', ValueFromPipelineByPropertyName)]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [int]$Id,

        [Parameter()]
        [hashtable]$Attributes,

        [Parameter()]
        [guid]$ConnectedSystemObjectId,

        [Parameter()]
        [switch]$LookUp
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $body = @{ lookUp = [bool]$LookUp }

        if ($Attributes) {
            $body.attributes = $Attributes
        }

        if ($PSBoundParameters.ContainsKey('ConnectedSystemObjectId')) {
            $body.connectedSystemObjectId = $ConnectedSystemObjectId
        }

        $endpoint = if ($PSCmdlet.ParameterSetName -eq 'SyncRule') {
            "/api/v1/synchronisation/sync-rules/$SyncRuleId/matching-rules/$Id/test"
        }
        else {
            "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/matching-rules/$Id/test"
        }

        Write-Verbose "Testing Object Matching Rule $Id"

        try {
            $result = Invoke-JIMApi -Endpoint $endpoint -Method 'POST' -Body $body

            if (-not $result.isValid) {
                Write-Verbose "The values could not be used: $($result.inputErrors -join ' ')"
            }

            # The API's own object, so every property it reports reaches the caller.
            $result
        }
        catch {
            Write-Error "Failed to test Object Matching Rule ${Id}: $_"
        }
    }
}
