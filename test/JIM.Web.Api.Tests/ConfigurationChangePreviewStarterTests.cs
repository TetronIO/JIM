// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Servers.Preview;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Web.Services;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The one place that decides whether an administrator is asked how much of a preview to keep (#827, PRD scenario
/// 5). The decision is worth isolating because both ways of getting it wrong are quiet:
///
/// Prompting for a small preview trains administrators to dismiss the question, so that the once-a-year preview
/// that genuinely costs something is dismissed too.
///
/// Not prompting for a large one caps it silently, and a drill-down that is a sample without saying so is how a
/// change gets approved as safe on the strength of the rows that happened to fit.
/// </summary>
[TestFixture]
public class ConfigurationChangePreviewStarterTests
{
    private Mock<IRepository> _repository = null!;
    private Mock<IConfigurationChangePreviewRepository> _previewRepository = null!;
    private Mock<IServiceSettingsRepository> _serviceSettingsRepository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private FakePrompt _prompt = null!;
    private FakeAdapter _adapter = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IRepository>();
        _previewRepository = new Mock<IConfigurationChangePreviewRepository>();
        _serviceSettingsRepository = new Mock<IServiceSettingsRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        _repository.Setup(r => r.ConfigurationChangePreviews).Returns(_previewRepository.Object);
        _repository.Setup(r => r.ServiceSettings).Returns(_serviceSettingsRepository.Object);
        _repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        _repository.Setup(r => r.Tasking).Returns(new Mock<ITaskingRepository>().Object);

        _adapter = new FakeAdapter();
        _prompt = new FakePrompt();
        _application = NewApplication();
    }

    [TearDown]
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task StartAsync_EstimateBelowTheThreshold_StartsCappedWithoutAskingAsync()
    {
        _adapter.Estimate = new PreviewCostEstimate(500);

        var activityId = await NewStarter().StartAsync(NewRequest());

        Assert.Multiple(() =>
        {
            Assert.That(_prompt.TimesAsked, Is.Zero, "a question with one sensible answer is noise");
            Assert.That(activityId, Is.Not.Null);
            Assert.That(StartedPreview!.RequestedDeltaPersistence, Is.EqualTo(ConfigurationChangePreviewDeltaPersistence.Capped));
        });
    }

    [Test]
    public async Task StartAsync_EstimateAboveTheThreshold_AsksAndHonoursTheAnswerAsync()
    {
        _adapter.Estimate = new PreviewCostEstimate(200_000);
        _prompt.Answer = ConfigurationChangePreviewDeltaPersistence.Full;

        await NewStarter().StartAsync(NewRequest());

        Assert.Multiple(() =>
        {
            Assert.That(_prompt.TimesAsked, Is.EqualTo(1));
            Assert.That(_prompt.RowsStated, Is.EqualTo(200_000L), "the choice is only informed if it states the size");
            Assert.That(StartedPreview!.RequestedDeltaPersistence, Is.EqualTo(ConfigurationChangePreviewDeltaPersistence.Full));
        });
    }

    [Test]
    public async Task StartAsync_AdministratorBacksOutOfThePrompt_StartsNothingAsync()
    {
        // Backing out of the question is not the same as accepting the recommendation: they were shown a cost and
        // decided against paying it, and starting the preview anyway would spend it for them.
        _adapter.Estimate = new PreviewCostEstimate(200_000);
        _prompt.Answer = null;

        var activityId = await NewStarter().StartAsync(NewRequest());

        Assert.Multiple(() =>
        {
            Assert.That(activityId, Is.Null);
            Assert.That(StartedPreview, Is.Null);
        });
    }

    [Test]
    public async Task StartAsync_ThresholdRaised_StopsAskingAboutPreviewsBelowItAsync()
    {
        GivenPromptThreshold(500_000);
        _adapter.Estimate = new PreviewCostEstimate(200_000);

        await NewStarter().StartAsync(NewRequest());

        Assert.That(_prompt.TimesAsked, Is.Zero);
    }

    [Test]
    public async Task StartAsync_MultiAttributeAdapter_MeasuresTheThresholdInRowsNotObjectsAsync()
    {
        // The threshold is about stored rows, and an Attribute Flow preview emits several per object. Measuring it
        // in objects would leave the largest previews in the product below the line that exists for them.
        _adapter.Estimate = new PreviewCostEstimate(40_000, 5);

        await NewStarter().StartAsync(NewRequest());

        Assert.Multiple(() =>
        {
            Assert.That(_prompt.TimesAsked, Is.EqualTo(1));
            Assert.That(_prompt.RowsStated, Is.EqualTo(200_000L));
        });
    }

    #region Helpers

    private ConfigurationChangePreview? StartedPreview { get; set; }

    private JimApplication NewApplication() =>
        new(_repository.Object, previewAdapters: new ConfigurationChangePreviewAdapterRegistry([_adapter]));

    private ConfigurationChangePreviewStarter NewStarter()
    {
        _previewRepository.Setup(r => r.CreatePreviewAsync(It.IsAny<ConfigurationChangePreview>()))
            .Callback<ConfigurationChangePreview>(p => StartedPreview = p)
            .Returns(Task.CompletedTask);
        _activityRepository.Setup(r => r.GetActivityAsync(It.IsAny<Guid>())).ReturnsAsync(new Activity());

        return new ConfigurationChangePreviewStarter(new FakeApplicationFactory(NewApplication), _prompt);
    }

    private void GivenPromptThreshold(int threshold) =>
        _serviceSettingsRepository
            .Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangePreviewFullDataSetPromptThreshold))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangePreviewFullDataSetPromptThreshold,
                ValueType = ServiceSettingValueType.Integer,
                DefaultValue = threshold.ToString()
            });

    private static ConfigurationChangePreviewRequest NewRequest() => new()
    {
        Surface = ConfigurationChangePreviewSurface.MetaverseObjectType,
        TargetId = 11,
        TargetName = "User",
        ProposedConfiguration = new object(),
        InitiatedByType = ActivityInitiatorType.User,
        InitiatedById = Guid.CreateVersion7(),
        InitiatedByName = "Ada Lovelace"
    };

    /// <summary>
    /// Builds a fresh application per call over the fixture's shared mocks, exactly as the real factory does, so
    /// the starter's own `using` disposes its own instance rather than the fixture's.
    /// </summary>
    private sealed class FakeApplicationFactory(Func<JimApplication> build) : IJimApplicationFactory
    {
        public JimApplication Create() => build();
    }

    private sealed class FakePrompt : IPreviewDataSetSizePrompt
    {
        public int TimesAsked { get; private set; }

        public long RowsStated { get; private set; }

        public ConfigurationChangePreviewDeltaPersistence? Answer { get; set; } =
            ConfigurationChangePreviewDeltaPersistence.Capped;

        public Task<ConfigurationChangePreviewDeltaPersistence?> AskAsync(long estimatedDeltaRows)
        {
            TimesAsked++;
            RowsStated = estimatedDeltaRows;
            return Task.FromResult(Answer);
        }
    }

    private sealed class FakeAdapter : IConfigurationChangePreviewAdapter
    {
        public ConfigurationChangePreviewSurface Surface => ConfigurationChangePreviewSurface.MetaverseObjectType;

        public bool ProducesDeltas => true;

        public Type ProposalType => typeof(object);

        public PreviewCostEstimate Estimate { get; set; } = new(1);

        public Task<List<PreviewValidationFinding>> ValidateAsync(PreviewContext context) => Task.FromResult(new List<PreviewValidationFinding>());

        public Task<PreviewCostEstimate> EstimateCostAsync(PreviewContext context) => Task.FromResult(Estimate);

        public Task<List<PreviewImpactCount>> CountImpactAsync(PreviewContext context) => Task.FromResult(new List<PreviewImpactCount>());

        public IAsyncEnumerable<PreviewDelta> EvaluateDeltasAsync(PreviewContext context, CancellationToken cancellationToken) =>
            System.Linq.AsyncEnumerable.Empty<PreviewDelta>();
    }

    #endregion
}
