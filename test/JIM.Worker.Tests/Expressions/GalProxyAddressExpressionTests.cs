// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// GAL synchronisation (#2021) gives each mail contact the source recipient's proxy addresses plus an <c>X500:</c> address
/// built from its Legacy Exchange DN, so mail addressed to the recipient's old Exchange identity still resolves. No new
/// function is needed: Split, Join and IIF build it. These tests prove the expression the GAL documentation gives, through
/// the same pipeline an export Attribute Flow evaluates.
/// </summary>
[TestFixture]
public class GalProxyAddressExpressionTests
{
    private const string GalProxyAddresses =
        "Split(Join(mv[\"Proxy Addresses\"], \"|\") + IIF(HasValue(mv[\"Legacy Exchange DN\"]), \"|X500:\" + mv[\"Legacy Exchange DN\"], \"\"), \"|\")";

    private const string LegacyExchangeDn = "/o=PANOPLY/ou=Exchange Administrative Group (FYDIBOHF23SPDLT)/cn=Recipients/cn=jane.smith";

    private DynamicExpressoEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp() => _evaluator = new DynamicExpressoEvaluator();

    [Test]
    public void EvaluateExport_ProxiesAndALegacyExchangeDn_GivesEveryProxyAndTheX500Address()
    {
        var evaluation = Evaluate(("Proxy Addresses", new List<string> { "SMTP:jane.smith@panoply.local", "smtp:jane@panoply.local" }),
            ("Legacy Exchange DN", LegacyExchangeDn));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(evaluation.Outcome, Is.EqualTo(AttributeFlowOutcome.Values));
            Assert.That(evaluation.Values, Is.EqualTo(new object[]
            {
                "SMTP:jane.smith@panoply.local", "smtp:jane@panoply.local", "X500:" + LegacyExchangeDn
            }));
        }
    }

    [Test]
    public void EvaluateExport_ProxiesAndNoLegacyExchangeDn_GivesTheProxiesAndNoBareX500()
    {
        var evaluation = Evaluate(("Proxy Addresses", new List<string> { "SMTP:jane.smith@panoply.local" }));

        Assert.That(evaluation.Values, Is.EqualTo(new object[] { "SMTP:jane.smith@panoply.local" }));
    }

    [Test]
    public void EvaluateExport_ALegacyExchangeDnAndNoProxies_GivesOnlyTheX500Address()
    {
        var evaluation = Evaluate(("Legacy Exchange DN", LegacyExchangeDn));

        Assert.That(evaluation.Values, Is.EqualTo(new object[] { "X500:" + LegacyExchangeDn }));
    }

    [Test]
    public void EvaluateExport_NeitherProxiesNorALegacyExchangeDn_GivesNoValue()
    {
        var evaluation = Evaluate();

        Assert.That(evaluation.Outcome, Is.EqualTo(AttributeFlowOutcome.NoValue));
    }

    [Test]
    public void EvaluateExport_TheX500AddressIsAlreadyAProxy_GivesItOnce()
    {
        // A recipient migrated between organisations often carries its own old X500 address already.
        var evaluation = Evaluate(("Proxy Addresses", new List<string> { "SMTP:jane.smith@panoply.local", "X500:" + LegacyExchangeDn }),
            ("Legacy Exchange DN", LegacyExchangeDn));

        Assert.That(evaluation.Values.Cast<object>().Count(v => Equals(v, "X500:" + LegacyExchangeDn)), Is.EqualTo(1),
            "a contact's proxy addresses must not repeat a value, or the directory refuses the write");
    }

    private AttributeFlowEvaluation Evaluate(params (string Name, object? Value)[] values)
    {
        var mapping = new SyncRuleMapping
        {
            TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute
            {
                Id = 7, Name = "proxyAddresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued
            }
        };
        var source = new SyncRuleMappingSource { Expression = GalProxyAddresses };
        mapping.Sources.Add(source);
        var metaverseAttributes = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

        return AttributeFlowPipeline.EvaluateExport(metaverseAttributes, mapping, source, _evaluator);
    }
}
