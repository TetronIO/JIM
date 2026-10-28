# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Get-JIMConnectedSystemCatalogue {
    <#
    .SYNOPSIS
        Lists what a Connected System's source contains: its tables and views, and their columns and keys.

    .DESCRIPTION
        Reads the source's own catalogue with the Connected System's saved settings. By default, lists every
        table and view the configured account can see. With -Name, describes one table or view: its columns,
        its primary key in key order, and the foreign keys it declares. With -Statement, describes the columns
        a SELECT statement would return, without reading a row.

        Use it to find the names to put in the SQL Connector's Object Types setting. Purely informational:
        nothing is written to JIM or to the source. Connectors that cannot describe their source return an
        error naming why.

    .PARAMETER ConnectedSystemId
        The ID of the Connected System.

    .PARAMETER Name
        The table or view to describe. Matched ignoring case.

    .PARAMETER Schema
        The schema the table or view is in. Leave it out to find the object by name alone, which is refused
        when the name is in more than one schema.

    .PARAMETER Statement
        A SELECT (or WITH) statement whose result columns to describe: a single statement, with no
        terminating semicolon.

    .OUTPUTS
        By default, one entry per table and view, tables first. With -Name or -Statement, one description.

    .EXAMPLE
        Get-JIMConnectedSystemCatalogue -ConnectedSystemId 3

        Lists the tables and views Connected System 3 can see.

    .EXAMPLE
        Get-JIMConnectedSystemCatalogue -ConnectedSystemId 3 | Where-Object { $_.Kind -eq 'View' }

        Lists only the views.

    .EXAMPLE
        Get-JIMConnectedSystemCatalogue -ConnectedSystemId 3 -Schema 'HR' -Name 'EMPLOYEES' | Select-Object -ExpandProperty Columns

        Lists the columns of HR.EMPLOYEES.

    .EXAMPLE
        (Get-JIMConnectedSystemCatalogue -ConnectedSystemId 3 -Schema 'HR' -Name 'EMPLOYEES').PrimaryKey

        Shows the primary key of HR.EMPLOYEES, in key order: the usual choice for an Object Type's anchorColumns.

    .EXAMPLE
        Get-JIMConnectedSystemCatalogue -ConnectedSystemId 3 -Statement 'SELECT EMPLOYEE_ID, GIVEN_NAME FROM HR.EMPLOYEES'

        Describes the columns the statement would return, without reading any rows.

    .LINK
        Get-JIMConnectedSystem
        Set-JIMConnectedSystem
    #>
    [CmdletBinding(DefaultParameterSetName = 'List')]
    [OutputType('JIM.ConnectedSystemCatalogueObjectDto', ParameterSetName = 'List')]
    [OutputType('JIM.ConnectedSystemCatalogueObjectDetailDto', ParameterSetName = 'Object')]
    [OutputType('JIM.ConnectedSystemCatalogueObjectDetailDto', ParameterSetName = 'Statement')]
    param(
        # The Id alias lets a piped Connected System bind. It is read-only, so -Id meaning "the Connected System"
        # rather than "a catalogue entry" can mislead, but never destroy anything.
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [Alias('Id')]
        [int]$ConnectedSystemId,

        [Parameter(Mandatory, ParameterSetName = 'Object')]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter(ParameterSetName = 'Object')]
        [string]$Schema,

        [Parameter(Mandatory, ParameterSetName = 'Statement')]
        [ValidateNotNullOrEmpty()]
        [string]$Statement
    )

    process {
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        $baseEndpoint = "/api/v1/synchronisation/connected-systems/$ConnectedSystemId/catalogue"

        try {
            switch ($PSCmdlet.ParameterSetName) {
                'Object' {
                    $query = @()
                    if ($Schema) {
                        $query += "schema=$([System.Uri]::EscapeDataString($Schema))"
                    }
                    $query += "name=$([System.Uri]::EscapeDataString($Name))"

                    Write-Verbose "Describing $Name in the catalogue of Connected System $ConnectedSystemId"
                    Invoke-JIMApi -Endpoint "$baseEndpoint/object?$($query -join '&')"
                }
                'Statement' {
                    # Sent in the body: a statement does not belong in a URL, where proxies and logs would keep it.
                    Write-Verbose "Describing a statement against Connected System $ConnectedSystemId"
                    Invoke-JIMApi -Endpoint "$baseEndpoint/statement" -Method 'POST' -Body @{ statement = $Statement }
                }
                default {
                    Write-Verbose "Listing the catalogue of Connected System $ConnectedSystemId"
                    $result = Invoke-JIMApi -Endpoint $baseEndpoint

                    # One object per table or view, for the pipeline.
                    foreach ($catalogueObject in $result) {
                        $catalogueObject
                    }
                }
            }
        }
        catch {
            Write-Error "Failed to read the catalogue: $_"
        }
    }
}
