# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function New-JIMExampleDataTemplate {
    <#
    .SYNOPSIS
        Creates a new Data Generation Template in JIM.

    .DESCRIPTION
        Creates a new Data Generation Template: a definition of which Metaverse Object Types to generate
        test objects for, how many of each, and how each attribute's values are generated (patterns,
        expressions, Example Data Sets, weighted values, number/date ranges and reference assignment).

    .PARAMETER Name
        The name for the Data Generation Template.

    .PARAMETER ObjectType
        One hashtable per Object Type the template generates objects for. Each hashtable supports:
        - MetaverseObjectType: a Metaverse Object Type name or id (required)
        - ObjectsToCreate: how many objects to generate (defaults to 1)
        - Attributes: an optional array of attribute-generation hashtables using the API's request field
          names (Pattern, Expression, PopulatedValuesPercentage, MinNumber, MaxNumber, MinDate, MaxDate,
          BoolTrueDistribution, BoolShouldBeRandom, SequentialNumbers, RandomNumbers, WeightedStringValues,
          ManagerDepthPercentage, MvaRefMinAssignments, MvaRefMaxAssignments, ReferenceMetaverseObjectTypeIds,
          AttributeDependency, ConnectedSystemObjectTypeAttributeId), except that MetaverseAttribute may be
          a Metaverse Attribute name or id, and ExampleDataSets entries may be Example Data Set names or ids
          (order taken from array position).
        Unknown keys throw, so typos are caught before anything is sent.

    .PARAMETER ChangeReason
        Optional reason for the change, recorded on the audit Activity and shown in the Data Generation
        Template's configuration change history.

    .PARAMETER PassThru
        If specified, returns the created Data Generation Template object.

    .OUTPUTS
        If -PassThru is specified, returns the created Data Generation Template object.

    .EXAMPLE
        New-JIMExampleDataTemplate -Name "Demo Users" -ObjectType @{
            MetaverseObjectType = "User"
            ObjectsToCreate     = 500
            Attributes          = @(
                @{ MetaverseAttribute = "Display Name"; Pattern = "{0} {1}"; ExampleDataSets = @("Firstnames Female", "Lastnames") }
            )
        }

        Creates a template generating 500 Users with pattern-based Display Names.

    .EXAMPLE
        New-JIMExampleDataTemplate -Name "Demo Users" -ObjectType @{ MetaverseObjectType = 1; ObjectsToCreate = 100 } -ChangeReason "Seeding demo data (CHG0200)" -PassThru

        Creates the template using a Metaverse Object Type id and records the reason on its configuration change history.

    .LINK
        Get-JIMExampleDataTemplate
        Set-JIMExampleDataTemplate
        Remove-JIMExampleDataTemplate
        Add-JIMExampleDataTemplateAttribute
        Invoke-JIMExampleDataTemplate
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory, Position = 0)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [Parameter(Mandatory)]
        [hashtable[]]$ObjectType,

        [ValidateNotNullOrEmpty()]
        [string]$ChangeReason,

        [switch]$PassThru
    )

    process {
        # Check connection first
        if (-not $script:JIMConnection) {
            Write-Error "You are not connected to JIM. Run Connect-JIM -Url <your JIM URL> to authenticate, then try again."
            return
        }

        if ($PSCmdlet.ShouldProcess($Name, "Create Data Generation Template")) {
            Write-Verbose "Creating Data Generation Template: $Name"

            try {
                $body = @{
                    name        = $Name
                    objectTypes = @(ConvertTo-JIMExampleDataTemplateObjectTypes -ObjectType $ObjectType)
                }
                if ($ChangeReason) {
                    $body.changeReason = $ChangeReason
                }

                $response = Invoke-JIMApi -Endpoint "/api/v1/example-data/templates" -Method 'POST' -Body $body
                Write-Verbose "Created Data Generation Template: $($response.id)"

                if ($PassThru) {
                    $response
                }
            }
            catch {
                Write-Error "Failed to create Data Generation Template: $_"
            }
        }
    }
}
