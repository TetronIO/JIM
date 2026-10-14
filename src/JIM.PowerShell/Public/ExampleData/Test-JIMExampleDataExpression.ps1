# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Test-JIMExampleDataExpression {
    <#
    .SYNOPSIS
        Tests an Example Data Template attribute's expression with sample attribute values.

    .DESCRIPTION
        Evaluates the expression an Example Data Template attribute generates its value with, or an expression given in
        its place, against sample values for the attributes it reads, by the same evaluation generating objects runs.
        Nothing is generated.

        Each sample is read as the type of the template attribute that generates it on the same object, so a number
        reaches the expression as a number. Outcome then says whether the attribute would be generated as a value
        (Value), without one (NoValue), or whether generating objects from the template would fail (Error).

    .PARAMETER TemplateId
        The ID of the Example Data Template the attribute belongs to.

    .PARAMETER TemplateAttributeId
        The ID of the template attribute whose expression to test. Get-JIMExampleDataTemplate lists each object type's template attributes with their IDs.

    .PARAMETER Expression
        An expression to test in place of the template attribute's saved one, so an edit can be tried before it is saved. When left out, the attribute's own expression is tested.

    .PARAMETER Attributes
        A hashtable of sample values for the attributes the expression reads, keyed by Metaverse attribute name. $null is no value and an array gives the attribute several values.

    .OUTPUTS
        What generating objects would give the attribute from the sample values. Value is the generated value in JIM's
        text form, OutcomeDescription says what would happen in one sentence, Inputs lists the attributes the expression
        reads with their types and the values they were given, and InputsNotGenerated names any it reads that the
        template does not generate, which never have a value.

    .EXAMPLE
        Test-JIMExampleDataExpression -TemplateId 4 -TemplateAttributeId 13 -Attributes @{ 'First Name' = 'Ada'; 'Last Name' = 'Lovelace' }

        Tests template attribute 13's expression with a sample first and last name, and shows the value it would be
        generated as.

    .EXAMPLE
        Test-JIMExampleDataExpression -TemplateId 4 -TemplateAttributeId 13 -Expression 'Lower(Left(mv["First Name"], 1) + mv["Last Name"])' -Attributes @{ 'First Name' = 'Ada'; 'Last Name' = 'Lovelace' }

        Tries an edited expression for template attribute 13 before saving it with Set-JIMExampleDataTemplate.

    .LINK
        Get-JIMExampleDataTemplate
        Add-JIMExampleDataTemplateAttribute
        Invoke-JIMExampleDataTemplate
    #>
    [CmdletBinding()]
    [OutputType('JIM.TestExampleDataExpressionResponse')]
    param(
        [Parameter(Mandatory, Position = 0)]
        [int]$TemplateId,

        [Parameter(Mandatory, Position = 1)]
        [int]$TemplateAttributeId,

        [Parameter()]
        [string]$Expression,

        [Parameter()]
        [hashtable]$Attributes
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $body = @{ templateAttributeId = $TemplateAttributeId }

        if ($PSBoundParameters.ContainsKey('Expression') -and -not [string]::IsNullOrWhiteSpace($Expression)) {
            $body.expression = $Expression
        }

        if ($Attributes) {
            $body.attributes = $Attributes
        }

        Write-Verbose "Testing the expression of template attribute $TemplateAttributeId on Example Data Template $TemplateId"

        try {
            $result = Invoke-JIMApi -Endpoint "/api/v1/example-data/templates/$TemplateId/test-expression" -Method 'POST' -Body $body

            if (-not $result.isValid) {
                Write-Verbose "The expression would not generate a value: $($result.errorMessage)"
            }

            # The API's own object, so every property it reports reaches the caller.
            $result
        }
        catch {
            Write-Error "Failed to test the expression of template attribute ${TemplateAttributeId}: $_"
        }
    }
}
