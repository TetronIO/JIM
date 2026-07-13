// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using System.Reflection;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using JIM.Worker.Tests.Models;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Scale guard for issue #988 finding 4: <c>UpdateConnectedSystemObjectFromImportObject</c> diffs
/// every attribute with nested <c>.Any()</c> scans in both directions (removals: CSO values vs
/// import values; additions: import values vs CSO values). Quadratic for large multi-valued
/// attributes - a 200,008-member group's next Full Import would do ~8x10^10 comparisons for that
/// one object.
/// <para>
/// This invokes <c>UpdateConnectedSystemObjectFromImportObject</c> directly via reflection (it is
/// private static) rather than driving the full <c>PerformImportAsync</c> pipeline. That keeps the
/// measurement isolated to the diff logic issue #988 actually changes: the full pipeline also runs
/// reference resolution and change-tracking stages with their own, separate scaling
/// characteristics that are out of scope for this fix and would otherwise confound the timing.
/// </para>
/// </summary>
[TestFixture]
public class ImportUpdateDiffScaleTests
{
    private static readonly MethodInfo UpdateMethod = typeof(SyncImportTaskProcessor).GetMethod(
        "UpdateConnectedSystemObjectFromImportObject",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("UpdateConnectedSystemObjectFromImportObject method not found via reflection - has it been renamed?");

    /// <summary>
    /// A user CSO with 50,000 existing values on a multi-valued Text attribute (QUALIFICATIONS),
    /// re-diffed against a completely disjoint set of 50,000 import values, forces both the
    /// removal-direction scan (50,000 CSO values x 50,000 import values) and the addition-direction
    /// scan (the reverse) - the worst case for finding 4's nested .Any() pattern.
    /// </summary>
    [Test]
    public void UpdateConnectedSystemObjectFromImportObject_LargeMvaFullyDisjointValueChange_CompletesWithinBound()
    {
        const int valueCount = 50_000;
        var userObjectType = TestUtilities.GetConnectedSystemObjectTypeData().Single(t => t.Name == "SOURCE_USER");
        var qualificationsAttribute = userObjectType.Attributes.Single(a => a.Name == MockSourceSystemAttributeNames.QUALIFICATIONS.ToString());
        var hrIdAttribute = userObjectType.Attributes.Single(a => a.IsExternalId);

        var hrId = Guid.NewGuid();
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            Type = userObjectType,
            ExternalIdAttributeId = hrIdAttribute.Id
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = hrIdAttribute.Id,
            GuidValue = hrId,
            Attribute = hrIdAttribute,
            ConnectedSystemObject = cso
        });
        for (var i = 0; i < valueCount; i++)
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = qualificationsAttribute.Id,
                StringValue = $"existing-qualification-{i}",
                Attribute = qualificationsAttribute,
                ConnectedSystemObject = cso
            });
        }

        var newQualificationValues = new List<string>(valueCount);
        for (var i = 0; i < valueCount; i++)
            newQualificationValues.Add($"new-qualification-{i}");

        var importObject = new ConnectedSystemImportObject
        {
            ChangeType = ObjectChangeType.NotSet,
            ObjectType = "SOURCE_USER",
            Attributes = new List<ConnectedSystemImportObjectAttribute>
            {
                new() { Name = MockSourceSystemAttributeNames.HR_ID.ToString(), GuidValues = new List<Guid> { hrId }, Type = AttributeDataType.Guid },
                new() { Name = MockSourceSystemAttributeNames.QUALIFICATIONS.ToString(), StringValues = newQualificationValues, Type = AttributeDataType.Text }
            }
        };

        var rpei = new ActivityRunProfileExecutionItem();

        var stopwatch = Stopwatch.StartNew();
        UpdateMethod.Invoke(null, new object?[] { importObject, cso, userObjectType, rpei, null });
        stopwatch.Stop();

        var newValueSet = new HashSet<string>(newQualificationValues, StringComparer.Ordinal);
        var additions = cso.PendingAttributeValueAdditions.Where(av => av.Attribute?.Name == MockSourceSystemAttributeNames.QUALIFICATIONS.ToString()).ToList();
        var removals = cso.PendingAttributeValueRemovals.Where(av => av.Attribute?.Name == MockSourceSystemAttributeNames.QUALIFICATIONS.ToString()).ToList();

        Assert.That(additions, Has.Count.EqualTo(valueCount), "Every new value should be queued as an addition.");
        Assert.That(additions.All(av => newValueSet.Contains(av.StringValue!)), Is.True, "Every addition should be one of the new import values.");
        Assert.That(removals, Has.Count.EqualTo(valueCount), "Every existing value should be queued as a removal (fully disjoint from the import).");

        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)),
            $"Diffing a {valueCount:N0}-value multi-valued Text attribute against a fully disjoint {valueCount:N0}-value import took " +
            $"{stopwatch.Elapsed.TotalSeconds:N1}s; expected well under 5s with the #988 fix applied.");
    }
}
