// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The size limits on a configuration test case (#2055 Phase 4), enforced by the application layer every surface
/// calls: a case over any of them is refused before anything is stored or recorded, on create and on update. Every
/// case is read and hashed into each snapshot of what owns it, so an unbounded one would cost every later save.
/// </summary>
[TestFixture]
public class ConfigurationTestServerLimitTests
{
    private Mock<IRepository> _repository = null!;
    private Mock<IConfigurationTestRepository> _testRepository = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        _repository = new Mock<IRepository>();
        _testRepository = new Mock<IConfigurationTestRepository>();
        _repository.Setup(r => r.ConfigurationTests).Returns(_testRepository.Object);
        _jim = new JimApplication(_repository.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    private static IEnumerable<TestCaseData> OverALimit()
    {
        yield return new TestCaseData((Action<ConfigurationTestCase>)(c => c.Description = new string('x', ConfigurationTestCase.MaxDescriptionLength + 1)), "description")
            .SetName("Description longer than the limit");
        yield return new TestCaseData((Action<ConfigurationTestCase>)(c => c.Inputs = Enumerable.Range(0, ConfigurationTestCase.MaxInputs + 1)
                .Select(i => new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, $"a{i}", ["x"])).ToList()), "inputs")
            .SetName("More inputs than the limit");
        yield return new TestCaseData((Action<ConfigurationTestCase>)(c => c.Inputs = [new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "mail",
                Enumerable.Range(0, ConfigurationTestCase.MaxValues + 1).Select(i => $"v{i}").ToList())]), "mail")
            .SetName("An input with more values than the limit");
        yield return new TestCaseData((Action<ConfigurationTestCase>)(c => c.Inputs = [new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "sn",
                [new string('x', ConfigurationTestCase.MaxValueLength + 1)])]), "sn")
            .SetName("An input value longer than the limit");
        yield return new TestCaseData((Action<ConfigurationTestCase>)(c => c.Inputs = [new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem,
                new string('a', ConfigurationTestCase.MaxAttributeNameLength + 1), ["x"])]), "attribute name")
            .SetName("An input attribute name longer than the limit");
        yield return new TestCaseData((Action<ConfigurationTestCase>)(c => c.Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values,
                Enumerable.Range(0, ConfigurationTestCase.MaxValues + 1).Select(i => $"v{i}").ToList())), "expect")
            .SetName("More expected values than the limit");
        yield return new TestCaseData((Action<ConfigurationTestCase>)(c => c.Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values,
                [new string('x', ConfigurationTestCase.MaxValueLength + 1)])), "expected value")
            .SetName("An expected value longer than the limit");
    }

    [TestCaseSource(nameof(OverALimit))]
    public void CreateTestCaseAsync_OverALimit_IsRefusedAndStoresNothing(Action<ConfigurationTestCase> exceed, string named)
    {
        var testCase = Case();
        exceed(testCase);

        Assert.That(async () => await _jim.ConfigurationTests.CreateTestCaseAsync(testCase, (MetaverseObject?)null),
            Throws.TypeOf<ArgumentException>().With.Message.Contains(named));
        _testRepository.Verify(r => r.CreateTestCaseAsync(It.IsAny<ConfigurationTestCase>()), Times.Never);
    }

    [TestCaseSource(nameof(OverALimit))]
    public void UpdateTestCaseAsync_OverALimit_IsRefusedAndStoresNothing(Action<ConfigurationTestCase> exceed, string named)
    {
        _testRepository.Setup(r => r.GetTestCaseAsync(4)).ReturnsAsync(Case());
        var testCase = Case();
        exceed(testCase);

        Assert.That(async () => await _jim.ConfigurationTests.UpdateTestCaseAsync(testCase, (MetaverseObject?)null),
            Throws.TypeOf<ArgumentException>().With.Message.Contains(named));
        _testRepository.Verify(r => r.UpdateTestCaseAsync(It.IsAny<ConfigurationTestCase>()), Times.Never);
    }

    [TestCase(ConfigurationTestCase.MaxTestCasesPerOwner, true)]
    [TestCase(ConfigurationTestCase.MaxTestCasesPerOwner - 1, false)]
    public async Task CreateTestCaseAsync_OwnerAtTheMostCasesItCanHold_IsRefusedAndStoresNothing(int held, bool refused)
    {
        // The save gate runs every case covering a rule twice on each save of it, so a rule's cases are bounded.
        var connectedSystems = new Mock<IConnectedSystemRepository>();
        connectedSystems.Setup(r => r.GetSyncRuleAsync(7)).ReturnsAsync(new SyncRule { Id = 7, Name = "Import People" });
        _repository.Setup(r => r.ConnectedSystems).Returns(connectedSystems.Object);
        _testRepository.Setup(r => r.GetTestCasesForSyncRuleAsync(7))
            .ReturnsAsync(Enumerable.Range(1, held).Select(i => new ConfigurationTestCase { Id = i, Name = $"case {i}" }).ToList());
        var testCase = new ConfigurationTestCase
        {
            Name = "Everyone is in scope", Kind = ConfigurationTestCaseKind.Scope, SyncRuleId = 7,
            Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.InScope)
        };

        if (refused)
        {
            Assert.That(async () => await _jim.ConfigurationTests.CreateTestCaseAsync(testCase, (MetaverseObject?)null),
                Throws.TypeOf<ArgumentException>().With.Message.Contains($"({ConfigurationTestCase.MaxTestCasesPerOwner})"));
            _testRepository.Verify(r => r.CreateTestCaseAsync(It.IsAny<ConfigurationTestCase>()), Times.Never);
        }
        else
        {
            // Past the cap check: the next step creates the Activity, which this fixture does not set up.
            try { await _jim.ConfigurationTests.CreateTestCaseAsync(testCase, (MetaverseObject?)null); }
            catch (Exception ex) when (ex is not ArgumentException) { }
            _testRepository.Verify(r => r.GetTestCasesForSyncRuleAsync(7), Times.Once);
        }
    }

    private static ConfigurationTestCase Case() => new()
    {
        Id = 4,
        Name = "Surname flows",
        Kind = ConfigurationTestCaseKind.AttributeFlow,
        SyncRuleMappingId = 3,
        Inputs = [new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "sn", ["Bloggs"])],
        Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["BLOGGS"])
    };
}
