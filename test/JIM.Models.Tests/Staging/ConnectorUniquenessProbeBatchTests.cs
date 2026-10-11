// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using NUnit.Framework;
using Serilog;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// Probing several attributes of one Connected System at once (Generated Value Forms, #2023, Phase 2). A Connector that
/// cannot combine them in one search inherits the default: one <see cref="IConnectorUniquenessProbe.ProbeAsync"/> per
/// attribute, in the order asked, each answer in its own place.
/// </summary>
[TestFixture]
public class ConnectorUniquenessProbeBatchTests
{
    private static UniquenessProbeRequest Request(string attributeName, params string[] candidates) => new()
    {
        ObjectTypeName = "user",
        AttributeName = attributeName,
        Candidates = candidates
    };

    [Test]
    public async Task ProbeAttributesAsync_ConnectorThatSearchesOneAttributeAtATime_ProbesEachInTurnAndAnswersInOrderAsync()
    {
        var connector = new OneAttributeAtATimeProbe(taken: "joe.bloggs@corp.local");
        IConnectorUniquenessProbe probe = connector;

        var results = await probe.ProbeAttributesAsync(
            [Request("sAMAccountName", "joe.bloggs"), Request("mail", "joe.bloggs@corp.local", "joe.bloggs1@corp.local")],
            new LoggerConfiguration().CreateLogger(), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connector.Searched, Is.EqualTo(new[] { "sAMAccountName", "mail" }));
            Assert.That(results.Select(r => r.Outcomes), Is.EqualTo(new[]
            {
                new[] { UniquenessProbeOutcome.NotFound },
                new[] { UniquenessProbeOutcome.Found, UniquenessProbeOutcome.NotFound }
            }));
        }
    }

    /// <summary>
    /// A Connector implementing only the single-attribute search, as the SCIM and SQL Connectors do.
    /// </summary>
    private sealed class OneAttributeAtATimeProbe(params string[] taken) : IConnectorUniquenessProbe
    {
        public List<string> Searched { get; } = [];

        public void OpenUniquenessProbeConnection(ConnectedSystem connectedSystem, ILogger logger)
        {
        }

        public bool CanProbeAttribute(string attributeName) => true;

        public Task<UniquenessProbeResult> ProbeAsync(UniquenessProbeRequest request, ILogger logger, CancellationToken cancellationToken)
        {
            Searched.Add(request.AttributeName);
            return Task.FromResult(UniquenessProbeResult.FromValuesFound(request, request.Candidates.Where(taken.Contains)));
        }

        public void CloseUniquenessProbeConnection()
        {
        }
    }
}
