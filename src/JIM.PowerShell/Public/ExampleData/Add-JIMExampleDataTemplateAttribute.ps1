# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function Add-JIMExampleDataTemplateAttribute {
    <#
    .SYNOPSIS
        Adds an attribute-generation configuration to a Data Generation Template in JIM.

    .DESCRIPTION
        Adds one attribute's generation configuration to an Object Type within an existing Data
        Generation Template, without restating the rest of the template: the cmdlet reads the template,
        appends the new attribute to the named Object Type, and writes the whole configuration back.
        -MetaverseAttribute names the attribute values are generated for.

    .PARAMETER TemplateId
        The unique identifier of the Data Generation Template to add the attribute to.

    .PARAMETER TemplateName
        The name of the Data Generation Template to add the attribute to.

    .PARAMETER ObjectType
        The Metaverse Object Type (name or id) identifying which of the template's Object Types
        receives the attribute.

    .PARAMETER MetaverseAttribute
        The Metaverse Attribute (name or id) values are generated for. Required.

    .PARAMETER Pattern
        A variable-replacement pattern for constructing string values, for example "{0}.{1}@contoso.com".

    .PARAMETER Expression
        An expression that constructs the value from other already-generated attributes via mv["Attribute Name"].

    .PARAMETER ExampleDataSet
        The Example Data Sets (names or ids) values are drawn from. Order follows array order, which is
        what index-based patterns like "{0} {1}" refer to.

    .PARAMETER WeightedValue
        Specific string values to choose from, weighted to control their distribution:
        one @{ Value = ..; Weight = .. } hashtable per value.

    .PARAMETER PopulatedValuesPercentage
        What percentage of generated objects receive a value for this attribute.

    .PARAMETER BoolTrueDistribution
        For boolean attributes, what percentage of values are true.

    .PARAMETER BoolShouldBeRandom
        For boolean attributes, whether values are generated randomly.

    .PARAMETER MinDate
        For date attributes, the earliest value to generate.

    .PARAMETER MaxDate
        For date attributes, the latest value to generate.

    .PARAMETER MinNumber
        For number attributes, the smallest value to generate.

    .PARAMETER MaxNumber
        For number attributes, the largest value to generate.

    .PARAMETER SequentialNumbers
        For number attributes, whether values are generated sequentially.

    .PARAMETER RandomNumbers
        For number attributes, whether values are generated randomly.

    .PARAMETER ManagerDepthPercentage
        For Manager attributes, how far into the organisational hierarchy managers are present.

    .PARAMETER MvaRefMinAssignments
        For multi-valued reference attributes, the minimum number of values to assign.

    .PARAMETER MvaRefMaxAssignments
        For multi-valued reference attributes, the maximum number of values to assign.

    .PARAMETER ReferenceMetaverseObjectType
        For reference attributes, the Metaverse Object Types (names or ids) generated references may point at.

    .PARAMETER AttributeDependency
        A condition on another attribute that must hold for this attribute to be generated:
        @{ MetaverseAttribute = <name or id>; ComparisonType = <operator>; StringValue = <value> }. The operators are
        the ones Synchronisation Rule scoping offers for the depended-on attribute's data type, such as Equals,
        GreaterThanOrEquals, StartsWith, MatchesPattern, HasValue and HasNoValue (which take no StringValue). Write
        StringValue as the attribute's type reads it: numbers with a full stop, dates as 2026-07-01, Booleans as
        true or false. Text is compared case-sensitively.

    .PARAMETER ChangeReason
        A reason for the change, recorded on its Activity and shown in the Data Generation Template's Configuration
        Change History.

    .PARAMETER PassThru
        Returns the updated Data Generation Template.

    .OUTPUTS
        With -PassThru, the updated Data Generation Template; otherwise nothing.

    .EXAMPLE
        Add-JIMExampleDataTemplateAttribute -TemplateName "Demo Users" -ObjectType "User" -MetaverseAttribute "Display Name" -Pattern "{0} {1}" -ExampleDataSet "Firstnames Female", "Lastnames"

        Adds pattern-based Display Name generation drawing from two Example Data Sets.

    .EXAMPLE
        Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType "User" -MetaverseAttribute "Status" -WeightedValue @{ Value = "active"; Weight = 0.9 }, @{ Value = "suspended"; Weight = 0.1 }

        Adds weighted-value generation for a Status attribute.

    .EXAMPLE
        Add-JIMExampleDataTemplateAttribute -TemplateId 7 -ObjectType "User" -MetaverseAttribute "Employee End Date" -MinDate (Get-Date) -MaxDate (Get-Date).AddYears(2) -PopulatedValuesPercentage 10 -AttributeDependency @{ MetaverseAttribute = "Employee Type"; ComparisonType = "Equals"; StringValue = "Contractor" } -PassThru

        Adds a date attribute generated for 10% of objects, only where the object's Employee Type is "Contractor".

    .LINK
        Get-JIMExampleDataTemplate
        New-JIMExampleDataTemplate
        Set-JIMExampleDataTemplate
        Remove-JIMExampleDataTemplate
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'ById')]
    [OutputType('JIM.ExampleDataTemplateDto')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ById', ValueFromPipelineByPropertyName)]
        [int]$TemplateId,

        [Parameter(Mandatory, ParameterSetName = 'ByName')]
        [ValidateNotNullOrEmpty()]
        [string]$TemplateName,

        [Parameter(Mandatory)]
        [object]$ObjectType,

        [Parameter()]
        [object]$MetaverseAttribute,

        [Parameter()]
        [string]$Pattern,

        [Parameter()]
        [string]$Expression,

        [Parameter()]
        [object[]]$ExampleDataSet,

        [Parameter()]
        [hashtable[]]$WeightedValue,

        [Parameter()]
        [ValidateRange(0, 100)]
        [int]$PopulatedValuesPercentage,

        [Parameter()]
        [ValidateRange(0, 100)]
        [int]$BoolTrueDistribution,

        [Parameter()]
        [bool]$BoolShouldBeRandom,

        [Parameter()]
        [datetime]$MinDate,

        [Parameter()]
        [datetime]$MaxDate,

        [Parameter()]
        [int]$MinNumber,

        [Parameter()]
        [int]$MaxNumber,

        [Parameter()]
        [bool]$SequentialNumbers,

        [Parameter()]
        [bool]$RandomNumbers,

        [Parameter()]
        [ValidateRange(0, 100)]
        [int]$ManagerDepthPercentage,

        [Parameter()]
        [int]$MvaRefMinAssignments,

        [Parameter()]
        [int]$MvaRefMaxAssignments,

        [Parameter()]
        [object[]]$ReferenceMetaverseObjectType,

        [Parameter()]
        [hashtable]$AttributeDependency,

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

        # The target attribute must be named. -MetaverseAttribute is not declared Mandatory so that this check,
        # rather than an interactive prompt, answers a call that leaves it out.
        if (-not $PSBoundParameters.ContainsKey('MetaverseAttribute')) {
            Write-Error "Specify -MetaverseAttribute to say which attribute values are generated for."
            return
        }

        # Resolve name to ID if using ByName parameter set
        $displayName = if ($PSCmdlet.ParameterSetName -eq 'ByName') { $TemplateName } else { $TemplateId }
        if ($PSCmdlet.ParameterSetName -eq 'ByName') {
            try {
                $resolvedTemplate = Resolve-JIMExampleDataTemplate -Name $TemplateName
                $TemplateId = $resolvedTemplate.id
            }
            catch {
                Write-Error $_
                return
            }
        }

        if ($PSCmdlet.ShouldProcess($displayName, "Add attribute to Data Generation Template")) {
            Write-Verbose "Adding attribute to Data Generation Template: $TemplateId"

            try {
                # Get the existing template with its full Object Type graph
                $template = Invoke-JIMApi -Endpoint "/api/v1/example-data/templates/$TemplateId"

                if (-not $template) {
                    Write-Error "Data Generation Template not found: $TemplateId"
                    return
                }

                # Locate the Object Type receiving the attribute
                $existingObjectTypes = @($template.objectTypes)
                $targetIndex = -1
                for ($i = 0; $i -lt $existingObjectTypes.Count; $i++) {
                    $isMatch = if ($ObjectType -is [string]) {
                        [string]$existingObjectTypes[$i].metaverseObjectTypeName -eq $ObjectType
                    }
                    else {
                        [int]$existingObjectTypes[$i].metaverseObjectTypeId -eq [int]$ObjectType
                    }
                    if ($isMatch) {
                        $targetIndex = $i
                        break
                    }
                }

                if ($targetIndex -lt 0) {
                    $availableTypes = ($existingObjectTypes | ForEach-Object { "$($_.metaverseObjectTypeName) (id $($_.metaverseObjectTypeId))" }) -join ', '
                    Write-Error "Object Type '$ObjectType' was not found in Data Generation Template '$($template.name)'. The template's Object Types are: $availableTypes."
                    return
                }

                # Convert the whole existing graph from response shape back into request shape (ids only),
                # passing every existing generation value through verbatim.
                $objectTypes = @(ConvertTo-JIMExampleDataTemplateRequest -Template $template)

                # Assemble the new attribute from the flat parameters and reuse the shared converter,
                # so names resolve to ids identically to New/Set-JIMExampleDataTemplate's hashtable path.
                $attributeSpec = @{ MetaverseAttribute = $MetaverseAttribute }

                foreach ($scalarParameter in 'Pattern', 'Expression', 'PopulatedValuesPercentage', 'BoolTrueDistribution', 'BoolShouldBeRandom',
                    'MinDate', 'MaxDate', 'MinNumber', 'MaxNumber', 'SequentialNumbers', 'RandomNumbers',
                    'ManagerDepthPercentage', 'MvaRefMinAssignments', 'MvaRefMaxAssignments') {
                    if ($PSBoundParameters.ContainsKey($scalarParameter)) {
                        $attributeSpec[$scalarParameter] = $PSBoundParameters[$scalarParameter]
                    }
                }

                if ($PSBoundParameters.ContainsKey('ExampleDataSet')) {
                    $attributeSpec.ExampleDataSets = $ExampleDataSet
                }
                if ($PSBoundParameters.ContainsKey('WeightedValue')) {
                    $attributeSpec.WeightedStringValues = $WeightedValue
                }
                if ($PSBoundParameters.ContainsKey('ReferenceMetaverseObjectType')) {
                    $attributeSpec.ReferenceMetaverseObjectTypeIds = @(foreach ($referenceType in $ReferenceMetaverseObjectType) {
                        if ($referenceType -is [string]) {
                            [int](Resolve-JIMMetaverseObjectType -Name $referenceType).id
                        }
                        else {
                            [int]$referenceType
                        }
                    })
                }
                if ($PSBoundParameters.ContainsKey('AttributeDependency')) {
                    $attributeSpec.AttributeDependency = $AttributeDependency
                }

                $newAttribute = ConvertTo-JIMExampleDataTemplateAttributeRequest -Attribute $attributeSpec

                # Append the new attribute to the target Object Type and write the whole graph back.
                $objectTypes[$targetIndex].attributes = @($objectTypes[$targetIndex].attributes) + $newAttribute

                $body = @{
                    objectTypes = $objectTypes
                }
                if ($ChangeReason) {
                    $body.changeReason = $ChangeReason
                }

                $response = Invoke-JIMApi -Endpoint "/api/v1/example-data/templates/$TemplateId" -Method 'PUT' -Body $body
                Write-Verbose "Added attribute to Data Generation Template: $TemplateId"

                # What the save said about the template's expressions without refusing it (#2055 Phase 3): each
                # written as the server phrased it.
                foreach ($saveWarning in @($response.Warnings | Where-Object { $_ })) {
                    Write-Warning $saveWarning
                }

                if ($PassThru) {
                    $response
                }
            }
            catch {
                Write-Error "Failed to add attribute to Data Generation Template: $_"
            }
        }
    }
}
