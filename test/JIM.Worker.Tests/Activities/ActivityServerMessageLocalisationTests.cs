// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application;
using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Localisation;
using Moq;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// A stored Activity message keeps the British English the REST API and logs read, and carries the message behind it so
/// the portal can show it in the viewer's language (#3039).
/// </summary>
[TestFixture]
public class ActivityServerMessageLocalisationTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    private JimApplication _application = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        var activities = new Mock<IActivityRepository>();
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.Activity).Returns(activities.Object);
        _application = new JimApplication(repository.Object);
    }

    [TearDown]
    public void TearDown() => _application.Dispose();

    private static Activity NewActivity() => new()
    {
        Id = Guid.NewGuid(),
        TargetType = ActivityTargetType.ConnectedSystemRunProfile,
        Status = ActivityStatus.InProgress,
        Executed = DateTime.UtcNow.AddSeconds(-5)
    };

    private static JimMessage Refusal(string subject) =>
        JimMessage.Create(ApplicationMessageCatalogues.Activities, nameof(ActivityMessages.FailureWithCause), subject, "because");

    [Test]
    public async Task FailActivityWithErrorAsync_Message_StoresTheEnglishAndTheMessageAsync()
    {
        var activity = NewActivity();

        await _application.Activities.FailActivityWithErrorAsync(activity, Refusal("Run stopped"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(activity.ErrorMessage, Is.EqualTo("Run stopped --> because"));
            Assert.That(activity.ErrorMessageDetail, Is.Not.Null);
            Assert.That(MessageRenderer.Render(activity.ErrorMessageDetail!, French, French), Is.EqualTo("Run stopped --> because"));
        }
    }

    [Test]
    public async Task FailActivityWithErrorAsync_String_StoresTheEnglishAloneAndClearsAnyEarlierMessageAsync()
    {
        var activity = NewActivity();
        activity.ErrorMessageDetail = Refusal("old");

        await _application.Activities.FailActivityWithErrorAsync(activity, "A plain sentence.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.ErrorMessage, Is.EqualTo("A plain sentence."));
            Assert.That(activity.ErrorMessageDetailJson, Is.Null);
        }
    }

    [Test]
    public async Task FailActivityWithErrorAsync_ExceptionCarryingAMessage_StoresItBesideTheUnchangedEnglishAsync()
    {
        var activity = NewActivity();
        var exception = new JimMessageException(Refusal("Refused"));

        await _application.Activities.FailActivityWithErrorAsync(activity, exception);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.ErrorMessage, Is.EqualTo(exception.Message));
            Assert.That(activity.ErrorMessageDetail?.ToEnglish(), Is.EqualTo(exception.Message));
        }
    }

    [Test]
    public async Task FailActivityWithErrorAsync_ExceptionWithEnglishOnly_StoresNoMessageAsync()
    {
        var activity = NewActivity();

        await _application.Activities.FailActivityWithErrorAsync(activity, new InvalidOperationException("Framework text"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.ErrorMessage, Is.EqualTo("Framework text"));
            Assert.That(activity.ErrorMessageDetailJson, Is.Null);
        }
    }

    [Test]
    public async Task FailActivityWithErrorAsync_ExceptionWithACauseThatCarriesAMessage_KeepsTheChainEnglishAndTranslatesTheLinkAsync()
    {
        var activity = NewActivity();
        var exception = new InvalidOperationException("Outer text", new JimMessageException(Refusal("Inner")));

        await _application.Activities.FailActivityWithErrorAsync(activity, exception);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.ErrorMessage, Is.EqualTo("Outer text --> Inner --> because"));
            Assert.That(activity.ErrorMessageDetail?.ToEnglish(), Is.EqualTo("Outer text --> Inner --> because"));
        }
    }

    [Test]
    public async Task FailActivityWithErrorAsync_ExceptionWhoseMessageHasAFrameworkSuffix_LeavesTheEnglishToStandAsync()
    {
        var activity = NewActivity();
        var exception = LocalisedExceptions.Argument(Refusal("Bad value"), "attributeId");

        await _application.Activities.FailActivityWithErrorAsync(activity, exception);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.ErrorMessage, Does.EndWith("(Parameter 'attributeId')"));
            Assert.That(activity.ErrorMessageDetailJson, Is.Null, "The English is not the message's, so the portal shows the English");
        }
    }

    [Test]
    public async Task CompleteActivityWithErrorAsync_Message_StoresTheEnglishAndTheMessageAsync()
    {
        var activity = NewActivity();

        await _application.Activities.CompleteActivityWithErrorAsync(activity, Refusal("Finished"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.CompleteWithError));
            Assert.That(activity.ErrorMessage, Is.EqualTo("Finished --> because"));
            Assert.That(activity.ErrorMessageDetail, Is.Not.Null);
        }
    }

    [Test]
    public async Task UpdateActivityMessageAsync_String_ClearsTheMessageBesideItAsync()
    {
        var activity = NewActivity();
        activity.MessageDetail = Refusal("old");

        await _application.Activities.UpdateActivityMessageAsync(activity, "Saving changes");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.Message, Is.EqualTo("Saving changes"));
            Assert.That(activity.MessageDetailJson, Is.Null);
        }
    }
}
