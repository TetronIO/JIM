# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMExpressionFunction {
    <#
    .SYNOPSIS
        Gets the built-in functions an expression can call.

    .DESCRIPTION
        Lists the built-in expression functions, with each one's signature, parameters, what it returns, a
        description and an example: the same list the expression editor's Insert function menu offers in the portal.
        Functions come back grouped by category, in the order the Expressions reference lists them.

        Use -Name to find a function by name and -Category to list one group.

    .PARAMETER Name
        Only functions with this name, ignoring case.

    .PARAMETER Category
        Only functions in this category.

    .OUTPUTS
        One object per function.

    .EXAMPLE
        Get-JIMExpressionFunction

        Lists every expression function.

    .EXAMPLE
        Get-JIMExpressionFunction -Name 'RegexReplace'

        Gets the RegexReplace function, to read its signature and what each parameter takes.

    .EXAMPLE
        Get-JIMExpressionFunction -Name '*Date*' | Select-Object Signature, Description

        Lists the functions whose names contain "Date", with how each is called.

    .EXAMPLE
        Get-JIMExpressionFunction -Category MultiValued | Format-Table Name, Returns, Example

        Lists the functions that work on every value of a Multi-Valued attribute at once.

    .EXAMPLE
        Get-JIMExpressionFunction | Where-Object { -not $_.IsRepeatable } | Select-Object Name

        Lists the functions that return a different value each time they are evaluated, such as Now and RandomPassword.

    .LINK
        Test-JIMExpression
        New-JIMSyncRuleMapping
    #>
    [CmdletBinding()]
    [OutputType('JIM.ExpressionFunctionDto')]
    param(
        [Parameter(Position = 0)]
        [SupportsWildcards()]
        [string]$Name,

        [Parameter()]
        [ValidateSet('Text', 'Pattern', 'Conditional', 'Conversion', 'Date', 'DistinguishedName', 'Password', 'MultiValued', 'AccountControl')]
        [string]$Category
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $endpoint = "/api/v1/synchronisation/expression-functions"
        if ($Category) {
            $endpoint += "?category=$Category"
        }

        Write-Verbose "Getting expression functions"

        try {
            $functions = Invoke-JIMApi -Endpoint $endpoint
        }
        catch {
            Write-Error "Failed to get expression functions: $_"
            return
        }

        # Filtered here rather than by the API's name filter, so -Name takes a wildcard pattern like every Get- cmdlet.
        if ($Name) {
            Write-Verbose "Filtering by name pattern: $Name"
            $functions = $functions | Where-Object { $_.name -like $Name }
        }

        foreach ($function in $functions) {
            $function
        }
    }
}
