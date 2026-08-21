# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

function ConvertTo-JIMExampleDataTemplateRequest {
    <#
    .SYNOPSIS
        Converts a Data Generation Template response DTO back into the update request's objectTypes shape.

    .DESCRIPTION
        The GET response carries names alongside ids (metaverseObjectTypeName, exampleDataSetName, and so
        on) while the PUT request wants ids only. This strips the response back to the request shape so a
        read-modify-write caller (Add-JIMExampleDataTemplateAttribute) can round-trip the whole existing
        graph unchanged. Existing scalar generation values pass through verbatim: coercing unset values to
        defaults would silently rewrite generation settings the call was never asked to change.

    .PARAMETER Template
        The template object as returned by GET /api/v1/example-data/templates/{id}.

    .OUTPUTS
        hashtable[] in the shape the templates endpoint's objectTypes request field expects.
    #>
    [CmdletBinding()]
    [OutputType([hashtable[]])]
    param(
        [Parameter(Mandatory)]
        [object]$Template
    )

    $scalarFields = @(
        'populatedValuesPercentage', 'boolTrueDistribution', 'boolShouldBeRandom',
        'minDate', 'maxDate', 'minNumber', 'maxNumber', 'sequentialNumbers', 'randomNumbers',
        'pattern', 'expression', 'managerDepthPercentage', 'mvaRefMinAssignments', 'mvaRefMaxAssignments'
    )

    $objectTypes = foreach ($objectType in @($Template.objectTypes)) {
        $attributes = @(foreach ($attribute in @($objectType.templateAttributes)) {
            $converted = @{}

            if ($null -ne $attribute.metaverseAttributeId) {
                $converted.metaverseAttributeId = [int]$attribute.metaverseAttributeId
            }
            if ($null -ne $attribute.connectedSystemObjectTypeAttributeId) {
                $converted.connectedSystemObjectTypeAttributeId = [int]$attribute.connectedSystemObjectTypeAttributeId
            }

            foreach ($field in $scalarFields) {
                if ($null -ne $attribute.$field) {
                    $converted[$field] = $attribute.$field
                }
            }

            if ($attribute.exampleDataSetInstances) {
                $converted.exampleDataSets = @(foreach ($instance in @($attribute.exampleDataSetInstances)) {
                    @{
                        exampleDataSetId = [int]$instance.exampleDataSetId
                        order            = [int]$instance.order
                    }
                })
            }

            if ($attribute.weightedStringValues) {
                $converted.weightedStringValues = @(foreach ($weighted in @($attribute.weightedStringValues)) {
                    @{
                        value  = $weighted.value
                        weight = $weighted.weight
                    }
                })
            }

            if ($attribute.referenceMetaverseObjectTypes) {
                $converted.referenceMetaverseObjectTypeIds = @(@($attribute.referenceMetaverseObjectTypes) | ForEach-Object { [int]$_.id })
            }

            if ($attribute.attributeDependency) {
                $converted.attributeDependency = @{
                    metaverseAttributeId = [int]$attribute.attributeDependency.metaverseAttributeId
                    # Pass the enum value through verbatim: the API returns and accepts enum names (#1060).
                    comparisonType       = $attribute.attributeDependency.comparisonType
                    stringValue          = $attribute.attributeDependency.stringValue
                }
            }

            $converted
        })

        @{
            metaverseObjectTypeId = [int]$objectType.metaverseObjectTypeId
            objectsToCreate       = [int]$objectType.objectsToCreate
            attributes            = $attributes
        }
    }

    # Emitted as individual hashtables; every caller re-collects with @(...), which keeps the
    # empty and single-element cases right without a comma-protected nested array.
    return [hashtable[]]@($objectTypes)
}
