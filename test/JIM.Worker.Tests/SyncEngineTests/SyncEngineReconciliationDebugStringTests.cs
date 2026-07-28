// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Pins the reconciliation diagnostic value-renderers so every attribute data type produces a
/// readable value string. A missing arm renders "(no matching type values)", obscuring the actual
/// imported state when an export fails reconciliation.
/// </summary>
public class SyncEngineReconciliationDebugStringTests
{
    private static PendingExportAttributeValueChange BuildChange(AttributeDataType type, Action<PendingExportAttributeValueChange>? configure = null)
    {
        var change = new PendingExportAttributeValueChange
        {
            AttributeId = 1,
            Attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "attr", Type = type }
        };
        configure?.Invoke(change);
        return change;
    }

    [Test]
    public void GetImportedValueAsString_BinaryAttribute_RendersByteLengthSummary()
    {
        // Arrange
        var change = BuildChange(AttributeDataType.Binary);
        var csoValues = new Dictionary<int, List<ConnectedSystemObjectAttributeValue>>
        {
            [1] = new() { new ConnectedSystemObjectAttributeValue { ByteValue = new byte[] { 1, 2, 3 } } }
        };

        // Act
        var result = SyncEngine.GetImportedValueAsString(csoValues, change);

        // Assert
        Assert.That(result, Is.EqualTo("(binary, 3 bytes)"));
    }

    [Test]
    public void GetImportedValueAsString_LongNumberAttribute_RendersValue()
    {
        // Arrange
        var change = BuildChange(AttributeDataType.LongNumber);
        var csoValues = new Dictionary<int, List<ConnectedSystemObjectAttributeValue>>
        {
            [1] = new() { new ConnectedSystemObjectAttributeValue { LongValue = 9999999999L } }
        };

        // Act
        var result = SyncEngine.GetImportedValueAsString(csoValues, change);

        // Assert
        Assert.That(result, Is.EqualTo("9999999999"));
    }
}
