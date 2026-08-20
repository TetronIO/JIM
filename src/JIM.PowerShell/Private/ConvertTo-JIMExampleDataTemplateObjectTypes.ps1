# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function ConvertTo-JIMExampleDataTemplateObjectTypes {
    <#
    .SYNOPSIS
        Turns the -ObjectType hashtables supplied to New/Set-JIMExampleDataTemplate into the API's objectTypes array.

    .DESCRIPTION
        Each hashtable describes one Object Type the Data Generation Template creates objects for:
        MetaverseObjectType (a Metaverse Object Type name or id), ObjectsToCreate (defaults to 1) and an
        optional Attributes array of per-attribute generation hashtables. Names are resolved to ids via
        the API; unknown hashtable keys throw so typos are caught before anything is sent.

    .PARAMETER ObjectType
        The Object Type hashtables to convert.

    .OUTPUTS
        hashtable[] in the shape the templates endpoint's objectTypes request field expects.
    #>
    [CmdletBinding()]
    [OutputType([hashtable[]])]
    param(
        [Parameter(Mandatory)]
        [hashtable[]]$ObjectType
    )

    $allowedKeys = @('MetaverseObjectType', 'ObjectsToCreate', 'Attributes')

    $converted = foreach ($entry in $ObjectType) {
        foreach ($key in $entry.Keys) {
            if ($key -notin $allowedKeys) {
                throw "Unknown key '$key' in -ObjectType hashtable. Allowed keys: $($allowedKeys -join ', ')."
            }
        }

        if (-not $entry.ContainsKey('MetaverseObjectType')) {
            throw "Each -ObjectType hashtable must contain a MetaverseObjectType key holding a Metaverse Object Type name or id."
        }

        $objectTypeValue = $entry['MetaverseObjectType']
        $metaverseObjectTypeId = if ($objectTypeValue -is [string]) {
            [int](Resolve-JIMMetaverseObjectType -Name $objectTypeValue).id
        }
        else {
            [int]$objectTypeValue
        }

        $result = @{
            metaverseObjectTypeId = $metaverseObjectTypeId
            objectsToCreate       = if ($entry.ContainsKey('ObjectsToCreate')) { [int]$entry['ObjectsToCreate'] } else { 1 }
            attributes            = @()
        }

        if ($entry.ContainsKey('Attributes') -and $entry['Attributes']) {
            $result.attributes = @(foreach ($attribute in @($entry['Attributes'])) {
                ConvertTo-JIMExampleDataTemplateAttributeRequest -Attribute $attribute
            })
        }

        $result
    }

    # Emitted as individual hashtables; every caller re-collects with @(...), which keeps the
    # empty and single-element cases right without a comma-protected nested array.
    return [hashtable[]]@($converted)
}

function ConvertTo-JIMExampleDataTemplateAttributeRequest {
    <#
    .SYNOPSIS
        Turns one attribute-generation hashtable into the API's template attribute request shape.

    .DESCRIPTION
        Keys follow the API's request field names, except that MetaverseAttribute may be a Metaverse
        Attribute name or id, ExampleDataSets entries may be Example Data Set names, ids, or
        @{ ExampleDataSetId = ..; Order = .. } hashtables (order defaults to array position), and an
        AttributeDependency's MetaverseAttribute may likewise be a name or id. Unknown keys throw.

    .PARAMETER Attribute
        The attribute-generation hashtable to convert.

    .OUTPUTS
        hashtable in the shape the templates endpoint's attribute request objects expect.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Attribute
    )

    $allowedKeys = @(
        'MetaverseAttribute', 'MetaverseAttributeId', 'ConnectedSystemObjectTypeAttributeId',
        'PopulatedValuesPercentage', 'BoolTrueDistribution', 'BoolShouldBeRandom',
        'MinDate', 'MaxDate', 'MinNumber', 'MaxNumber', 'SequentialNumbers', 'RandomNumbers',
        'Pattern', 'Expression', 'ExampleDataSets', 'WeightedStringValues',
        'ManagerDepthPercentage', 'MvaRefMinAssignments', 'MvaRefMaxAssignments',
        'ReferenceMetaverseObjectTypeIds', 'AttributeDependency'
    )

    foreach ($key in $Attribute.Keys) {
        if ($key -notin $allowedKeys) {
            throw "Unknown key '$key' in attribute hashtable. Allowed keys: $($allowedKeys -join ', ')."
        }
    }

    $converted = @{}

    if ($Attribute.ContainsKey('MetaverseAttribute')) {
        $attributeValue = $Attribute['MetaverseAttribute']
        $converted.metaverseAttributeId = if ($attributeValue -is [string]) {
            [int](Resolve-JIMMetaverseAttribute -Name $attributeValue).id
        }
        else {
            [int]$attributeValue
        }
    }
    if ($Attribute.ContainsKey('MetaverseAttributeId')) {
        $converted.metaverseAttributeId = [int]$Attribute['MetaverseAttributeId']
    }
    if ($Attribute.ContainsKey('ConnectedSystemObjectTypeAttributeId')) {
        $converted.connectedSystemObjectTypeAttributeId = [int]$Attribute['ConnectedSystemObjectTypeAttributeId']
    }

    # Scalar generation settings pass through under their request field names.
    $scalarKeyMap = @{
        PopulatedValuesPercentage = 'populatedValuesPercentage'
        BoolTrueDistribution      = 'boolTrueDistribution'
        BoolShouldBeRandom        = 'boolShouldBeRandom'
        MinDate                   = 'minDate'
        MaxDate                   = 'maxDate'
        MinNumber                 = 'minNumber'
        MaxNumber                 = 'maxNumber'
        SequentialNumbers         = 'sequentialNumbers'
        RandomNumbers             = 'randomNumbers'
        Pattern                   = 'pattern'
        Expression                = 'expression'
        ManagerDepthPercentage    = 'managerDepthPercentage'
        MvaRefMinAssignments      = 'mvaRefMinAssignments'
        MvaRefMaxAssignments      = 'mvaRefMaxAssignments'
    }
    foreach ($scalarKey in $scalarKeyMap.Keys) {
        if ($Attribute.ContainsKey($scalarKey)) {
            $converted[$scalarKeyMap[$scalarKey]] = $Attribute[$scalarKey]
        }
    }

    if ($Attribute.ContainsKey('ExampleDataSets')) {
        $order = 0
        $converted.exampleDataSets = @(foreach ($dataSet in @($Attribute['ExampleDataSets'])) {
            ConvertTo-JIMExampleDataSetReference -DataSet $dataSet -DefaultOrder $order
            $order++
        })
    }

    if ($Attribute.ContainsKey('WeightedStringValues')) {
        $converted.weightedStringValues = @(foreach ($weighted in @($Attribute['WeightedStringValues'])) {
            @{
                value  = [string]$weighted.Value
                weight = [float]$weighted.Weight
            }
        })
    }

    if ($Attribute.ContainsKey('ReferenceMetaverseObjectTypeIds')) {
        $converted.referenceMetaverseObjectTypeIds = @(@($Attribute['ReferenceMetaverseObjectTypeIds']) | ForEach-Object { [int]$_ })
    }

    if ($Attribute.ContainsKey('AttributeDependency')) {
        $converted.attributeDependency = ConvertTo-JIMExampleDataAttributeDependency -Dependency $Attribute['AttributeDependency']
    }

    return $converted
}

function ConvertTo-JIMExampleDataSetReference {
    <#
    .SYNOPSIS
        Turns one ExampleDataSets entry (name, id, or hashtable) into the API's { exampleDataSetId, order } shape.

    .PARAMETER DataSet
        An Example Data Set name, id, or @{ ExampleDataSetId = ..; Order = .. } hashtable.

    .PARAMETER DefaultOrder
        The order to assign when the entry does not carry one; callers pass the entry's array position.

    .OUTPUTS
        hashtable with exampleDataSetId and order keys.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [object]$DataSet,

        [Parameter(Mandatory)]
        [int]$DefaultOrder
    )

    if ($DataSet -is [System.Collections.IDictionary]) {
        if (-not $DataSet.Contains('ExampleDataSetId')) {
            throw "An ExampleDataSets hashtable entry must contain an ExampleDataSetId key."
        }
        return @{
            exampleDataSetId = [int]$DataSet['ExampleDataSetId']
            order            = if ($DataSet.Contains('Order')) { [int]$DataSet['Order'] } else { $DefaultOrder }
        }
    }

    $dataSetId = if ($DataSet -is [string]) {
        [int](Resolve-JIMExampleDataSet -Name $DataSet).id
    }
    else {
        [int]$DataSet
    }

    return @{
        exampleDataSetId = $dataSetId
        order            = $DefaultOrder
    }
}

function ConvertTo-JIMExampleDataAttributeDependency {
    <#
    .SYNOPSIS
        Turns an attribute dependency hashtable into the API's { metaverseAttributeId, comparisonType, stringValue } shape.

    .PARAMETER Dependency
        A hashtable with MetaverseAttribute (name or id), ComparisonType and StringValue keys.

    .OUTPUTS
        hashtable in the attributeDependency request shape.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Dependency
    )

    $allowedKeys = @('MetaverseAttribute', 'MetaverseAttributeId', 'ComparisonType', 'StringValue')
    foreach ($key in $Dependency.Keys) {
        if ($key -notin $allowedKeys) {
            throw "Unknown key '$key' in AttributeDependency hashtable. Allowed keys: $($allowedKeys -join ', ')."
        }
    }

    $metaverseAttributeId = if ($Dependency.ContainsKey('MetaverseAttributeId')) {
        [int]$Dependency['MetaverseAttributeId']
    }
    elseif ($Dependency.ContainsKey('MetaverseAttribute')) {
        $attributeValue = $Dependency['MetaverseAttribute']
        if ($attributeValue -is [string]) {
            [int](Resolve-JIMMetaverseAttribute -Name $attributeValue).id
        }
        else {
            [int]$attributeValue
        }
    }
    else {
        throw "An AttributeDependency hashtable must contain a MetaverseAttribute key (a Metaverse Attribute name or id)."
    }

    # Enum values must be sent as names, not numbers: the API rejects numeric enum values
    # on request DTOs (#1060). Canonicalise the casing so the exact enum name is sent.
    $validComparisonTypes = @('Equals', 'NotEquals', 'LessThan', 'GreaterThan', 'GreaterThanOrEqual', 'LessThanOrEqual', 'Like')
    $comparisonType = [string]$Dependency['ComparisonType']
    $canonicalComparisonType = $validComparisonTypes | Where-Object { $_ -eq $comparisonType } | Select-Object -First 1
    if (-not $canonicalComparisonType) {
        throw "Invalid AttributeDependency ComparisonType '$comparisonType'. Valid values: $($validComparisonTypes -join ', ')."
    }

    return @{
        metaverseAttributeId = $metaverseAttributeId
        comparisonType       = $canonicalComparisonType
        stringValue          = [string]$Dependency['StringValue']
    }
}
