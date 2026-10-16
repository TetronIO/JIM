# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Test-JIMExpression {
    <#
    .SYNOPSIS
        Tests an expression with sample attribute data.

    .DESCRIPTION
        Tests a JIM expression with sample Metaverse and Connected System attribute values
        to verify syntax and see the expected output before deploying to a Synchronisation Rule mapping.

        Expressions use DynamicExpresso syntax with mv["AttributeName"] and cs["AttributeName"]
        for attribute access.

        With -SyncRuleId and -MappingId, the expression is tested as that Attribute Flow, by the same code
        a synchronisation runs: each sample value is converted to its attribute's type, then the Attribute
        Flow's Missing Input Behaviour, the expression, value processing and conversion to the target
        attribute's type apply in turn. Outcome then says whether the target would be given values, no value,
        or an error, and ValueLines gives the values it would hold.

        An Attribute Flow that generates its value (a Generated Value) is tested as far as its base expression
        and stops before generation, as a synchronisation does: generation needs the database to know which
        values are taken. GeneratedValue is then $true, and BaseOutcome and BaseValue say what generation would
        be given in place of Outcome and ValueLines.

    .PARAMETER Expression
        The expression to test.
        Uses DynamicExpresso syntax with mv["AttributeName"] and cs["AttributeName"] for attribute access.
        It can be left out only for a Generated Value with no base expression, which generates its value from a sequence or random token alone.

    .PARAMETER MvAttributes
        A hashtable of sample Metaverse attribute values.
        Keys are attribute names, values are the attribute values.
        $null is no value and an array gives the attribute several values; with an Attribute Flow, empty text is a value distinct from $null.

    .PARAMETER CsAttributes
        A hashtable of sample Connected System attribute values.
        Keys are attribute names, values are the attribute values.
        $null is no value and an array gives the attribute several values; with an Attribute Flow, empty text is a value distinct from $null.

    .PARAMETER SyncRuleId
        The ID of the Synchronisation Rule whose Attribute Flow to test the expression as. Give it with MappingId.

    .PARAMETER MappingId
        The ID of the Attribute Flow to test the expression as. The expression tested is Expression, so an edit can be tried before it is saved.

    .PARAMETER ConnectedSystemObjectId
        The ID of a Connected System Object whose values to test with, for an import Attribute Flow. Its values for the attributes the expression reads are the inputs, and a value in CsAttributes replaces the object's value for that attribute. An expression that also reads mv["..."] takes those inputs from the Metaverse Object it is joined to.

    .PARAMETER MetaverseObjectId
        The ID of a Metaverse Object whose values to test with, for an export Attribute Flow. A value in MvAttributes replaces the object's value for that attribute.

    .PARAMETER AsOf
        The instant to test as of: what Now() and Today() read. A time without a time zone is read as UTC.

    .OUTPUTS
        The result of evaluating the expression against the sample values. ResultLines gives it as the portal's tester
        shows it, one value per line. With an Attribute Flow, Outcome, OutcomeDescription and ValueLines say what
        a synchronisation would give the target attribute, Inputs lists the values each input was given, and
        IfInputHasNoValue says what each input's absence would do. For a Generated Value, BaseOutcome and BaseValue
        take the place of Outcome and ValueLines.

    .EXAMPLE
        Test-JIMExpression -Expression '"Hello, " + mv["Display Name"] + "!"' -MvAttributes @{ "Display Name" = "John Doe" }

        Tests a simple string concatenation expression with a sample display name.

    .EXAMPLE
        Test-JIMExpression -Expression '"CN=" + EscapeDN(mv["Display Name"]) + ",OU=Users,DC=domain,DC=local"' -MvAttributes @{ "Display Name" = "John, Doe" }

        Tests a DN construction expression with proper escaping.

    .EXAMPLE
        Test-JIMExpression -Expression 'Lower(cs["FirstName"]) + "." + Lower(cs["LastName"]) + "@company.com"' -CsAttributes @{ "FirstName" = "John"; "LastName" = "DOE" }

        Tests an email address construction expression.

    .EXAMPLE
        Test-JIMExpression -Expression 'IIF(mv["Department"] == "IT", "tech-" + mv["AccountName"], mv["AccountName"])' -MvAttributes @{ "Department" = "IT"; "AccountName" = "jdoe" }

        Tests a conditional expression that prefixes account names for IT users.

    .EXAMPLE
        Test-JIMExpression -Expression 'FormatDate((DateTime)cs["hireDate"], "yyyy-MM-dd")' -SyncRuleId 12 -MappingId 40 -CsAttributes @{ "hireDate" = "2026-03-02" }

        Tests an expression as Attribute Flow 40 on Synchronisation Rule 12: the sample is converted to a date, as the
        Connected System Object would hold it, and the result is converted for the Attribute Flow's target attribute.

    .EXAMPLE
        Test-JIMExpression -Expression 'Lower(cs["givenName"]) + "." + Lower(cs["sn"])' -SyncRuleId 12 -MappingId 41 -ConnectedSystemObjectId '0b0e1c5a-6d3a-4c7e-9a43-6a7b5d2f1e01'

        Tests an expression as Attribute Flow 41 with a real Connected System Object's values, then shows what each
        input's absence would do through IfInputHasNoValue.

    .EXAMPLE
        Test-JIMExpression -Expression 'IIF((DateTime)cs["leaveDate"] < Today(), "Leaver", "Active")' -SyncRuleId 12 -MappingId 42 -CsAttributes @{ "leaveDate" = "2026-12-31" } -AsOf '2027-01-15'

        Tests what Attribute Flow 42 would give an object on 15 January 2027, when its leave date has passed.

    .EXAMPLE
        Test-JIMExpression -Expression 'Left(cs["givenName"], 1) + cs["sn"]' -SyncRuleId 12 -MappingId 43 -CsAttributes @{ "givenName" = "John"; "sn" = "Smith" }

        Tests the base expression of Attribute Flow 43, a Generated Value: BaseValue is the base generation would add its
        uniqueness token to, after the Attribute Flow's value processing. Which value is free is decided when a
        synchronisation runs.

    .EXAMPLE
        Test-JIMExpression -Expression 'RandomPassword(16, true)'

        Tests random password generation (result will vary on each run).

    .EXAMPLE
        Test-JIMExpression -Expression 'RandomPassphrase(3, "-")'

        Tests random passphrase generation (e.g., "purple-boat-ladder").

    .LINK
        New-JIMSyncRuleMapping
        Get-JIMSyncRuleMapping
    #>
    [CmdletBinding(DefaultParameterSetName = 'Expression')]
    [OutputType('JIM.TestExpressionResponse')]
    param(
        [Parameter(Mandatory, Position = 0, ParameterSetName = 'Expression')]
        [Parameter(Position = 0, ParameterSetName = 'AttributeFlow')]
        [string]$Expression,

        [Parameter()]
        [hashtable]$MvAttributes,

        [Parameter()]
        [hashtable]$CsAttributes,

        [Parameter(Mandatory, ParameterSetName = 'AttributeFlow')]
        [int]$SyncRuleId,

        [Parameter(Mandatory, ParameterSetName = 'AttributeFlow')]
        [int]$MappingId,

        [Parameter(ParameterSetName = 'AttributeFlow')]
        [guid]$ConnectedSystemObjectId,

        [Parameter(ParameterSetName = 'AttributeFlow')]
        [guid]$MetaverseObjectId,

        [Parameter()]
        [datetime]$AsOf
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        # Build request body. A Generated Value with no base expression is tested without one.
        $body = @{}
        if ($PSBoundParameters.ContainsKey('Expression')) {
            $body.expression = $Expression
        }

        if ($MvAttributes) {
            $body.mvAttributes = $MvAttributes
        }

        if ($CsAttributes) {
            $body.csAttributes = $CsAttributes
        }

        if ($PSCmdlet.ParameterSetName -eq 'AttributeFlow') {
            $body.syncRuleId = $SyncRuleId
            $body.mappingId = $MappingId

            if ($PSBoundParameters.ContainsKey('ConnectedSystemObjectId')) {
                $body.connectedSystemObjectId = $ConnectedSystemObjectId
            }

            if ($PSBoundParameters.ContainsKey('MetaverseObjectId')) {
                $body.metaverseObjectId = $MetaverseObjectId
            }
        }

        if ($PSBoundParameters.ContainsKey('AsOf')) {
            # ISO 8601 with no offset unless one was given, so the API reads a bare date or time as UTC.
            $body.asOf = if ($AsOf.Kind -eq [DateTimeKind]::Unspecified) { $AsOf.ToString('yyyy-MM-ddTHH:mm:ss') } else { $AsOf.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') }
        }

        Write-Verbose "Testing expression: $Expression"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/synchronisation/test-expression" -Method 'POST' -Body $body

            if ($result.isValid) {
                Write-Verbose "Expression is valid. Result type: $($result.resultType)"
            }
            else {
                Write-Verbose "Expression is invalid: $($result.errorMessage)"
            }

            # The API's own object, so every property it reports reaches the caller.
            $result
        }
        catch {
            Write-Error "Failed to test expression: $_"
        }
    }
}
