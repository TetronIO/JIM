function Test-JIMExpression {
    <#
    .SYNOPSIS
        Tests an expression with sample attribute data.

    .DESCRIPTION
        Tests a JIM expression with sample Metaverse and Connected System attribute values
        to verify syntax and see the expected output before deploying to a sync rule mapping.

        Expressions use DynamicExpresso syntax with mv["AttributeName"] and cs["AttributeName"]
        for attribute access.

    .PARAMETER Expression
        The expression to test.
        Uses DynamicExpresso syntax with mv["AttributeName"] and cs["AttributeName"] for attribute access.

    .PARAMETER MvAttributes
        A hashtable of sample Metaverse attribute values.
        Keys are attribute names, values are the attribute values.

    .PARAMETER CsAttributes
        A hashtable of sample Connected System attribute values.
        Keys are attribute names, values are the attribute values.

    .OUTPUTS
        PSCustomObject with properties:
        - IsValid: Whether the expression is valid and evaluated successfully
        - Result: The result of evaluating the expression
        - ResultType: The type of the result (e.g., "String", "Int32", "Boolean")
        - ErrorMessage: Error message if the expression is invalid or evaluation failed
        - ErrorPosition: Position in the expression where an error occurred (if applicable)

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
        Test-JIMExpression -Expression 'RandomPassword(16, true)'

        Tests random password generation (result will vary on each run).

    .EXAMPLE
        Test-JIMExpression -Expression 'RandomPassphrase(3, "-")'

        Tests random passphrase generation (e.g., "purple-boat-ladder").

    .LINK
        New-JIMSyncRuleMapping
        Get-JIMSyncRuleMapping
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, Position = 0)]
        [string]$Expression,

        [Parameter()]
        [hashtable]$MvAttributes,

        [Parameter()]
        [hashtable]$CsAttributes
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "Not connected to JIM. Use Connect-JIM first."
            return
        }

        # Build request body
        $body = @{
            expression = $Expression
        }

        if ($MvAttributes) {
            $body.mvAttributes = $MvAttributes
        }

        if ($CsAttributes) {
            $body.csAttributes = $CsAttributes
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

            # Return a formatted object
            [PSCustomObject]@{
                IsValid = $result.isValid
                Result = $result.result
                ResultType = $result.resultType
                ErrorMessage = $result.errorMessage
                ErrorPosition = $result.errorPosition
            }
        }
        catch {
            Write-Error "Failed to test expression: $_"
        }
    }
}
