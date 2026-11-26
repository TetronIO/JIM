// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using System.Text.Json;
using JIM.Application.Localisation;
using JIM.Models.Activities;
using JIM.Models.Localisation;
using JIM.Web;
using JIM.Web.Models;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Activities and Run Profile Execution Items carry each message's key and arguments beside its English (#3039), so an
/// API client can render a message in any language. The English <c>message</c> is unchanged.
/// </summary>
[TestFixture]
public class ActivityMessageKeyDtoTests
{
    private static readonly DateTime Moment = new(2026, 11, 24, 9, 30, 0, DateTimeKind.Utc);

    private static JimMessage RoleMemberAdded() =>
        JimMessage.Create(ApplicationMessageCatalogues.Activities, "RoleMemberAdded", "Ada Lovelace", "Administrators");

    [Test]
    public void Header_MessageWithArguments_CarriesItsKeyAndTypedArgumentsBesideTheEnglish()
    {
        var activity = new Activity { MessageDetail = RoleMemberAdded() };

        var header = ActivityHeader.FromEntity(activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.Message, Is.EqualTo("Added 'Ada Lovelace' to Role 'Administrators'"));
            Assert.That(header.MessageKey, Is.EqualTo("ActivityMessages.RoleMemberAdded"));
            Assert.That(header.MessageArguments!.Select(a => a.Text), Is.EqualTo(new[] { "Ada Lovelace", "Administrators" }));
            Assert.That(header.MessageCount, Is.Null);
        }
    }

    [Test]
    public void KeyOf_ConnectorMessage_KeepsTheConnectorsCatalogueName()
    {
        // A JIM catalogue's assembly is an implementation detail and is dropped; a Connector's catalogue name is the
        // only thing that says which Connector a message is from, so a client needs all of it (#3103).
        var message = JimMessage.Create(ConnectorCatalogueNames.For("jim.ldap"), "PermissionMissing", "User.ReadWrite");

        Assert.That(ApiMessage.KeyOf(message), Is.EqualTo("connector:jim.ldap/Messages.PermissionMissing"));
    }

    [Test]
    public void Header_ActivityRecordedBeforeMessagesWereStored_HasNoKeyOrArguments()
    {
        var header = ActivityHeader.FromEntity(new Activity { Message = "Sync complete" });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.Message, Is.EqualTo("Sync complete"));
            Assert.That(header.MessageKey, Is.Null);
            Assert.That(header.MessageArguments, Is.Null);
            Assert.That(header.MessageCount, Is.Null);
        }
    }

    [Test]
    public void Detail_WarningAndError_EachCarryTheirOwnKey()
    {
        var activity = new Activity
        {
            WarningMessageDetail = RoleMemberAdded(),
            ErrorMessageDetail = JimMessage.Create(ApplicationMessageCatalogues.Activities, "TargetFactoryReset")
        };

        var detail = ActivityDetailDto.FromEntity(activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail.WarningMessageKey, Is.EqualTo("ActivityMessages.RoleMemberAdded"));
            Assert.That(detail.WarningMessageArguments, Has.Count.EqualTo(2));
            Assert.That(detail.ErrorMessage, Is.EqualTo("Factory reset"));
            Assert.That(detail.ErrorMessageKey, Is.EqualTo("ActivityMessages.TargetFactoryReset"));
            Assert.That(detail.ErrorMessageArguments, Is.Empty);
            Assert.That(detail.MessageKey, Is.Null);
        }
    }

    [Test]
    public void Arguments_NumberDateAndNestedCountedMessage_KeepTheirTypes()
    {
        var nested = JimMessage.Counted(ApplicationMessageCatalogues.Activities, "PartitionCountOne", 3, 3);
        var message = JimMessage.Create(ApplicationMessageCatalogues.Activities, "FailureWithCause", nested, 42L);
        var argumentsWithDate = new MessageArgument[] { Moment };

        var arguments = ApiMessage.ArgumentsOf(message)!;
        var date = ApiMessage.ArgumentsOf(new JimMessage(ApplicationMessageCatalogues.Activities, "Verbatim", argumentsWithDate))!.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(arguments[0].Message!.Key, Is.EqualTo("ActivityMessages.PartitionCount"));
            Assert.That(arguments[0].Message!.Count, Is.EqualTo(3));
            Assert.That(arguments[0].Message!.Arguments.Single().Number, Is.EqualTo(3));
            Assert.That(arguments[1].Number, Is.EqualTo(42));
            Assert.That(date.Date, Is.EqualTo(Moment));
        }
    }

    [Test]
    public void Header_CountedMessage_CarriesTheStemKeyAndTheCountThatChoosesItsForm()
    {
        var activity = new Activity { MessageDetail = JimMessage.Counted(ApplicationMessageCatalogues.Activities, "PartitionCountOne", 1, 1) };

        var header = ActivityHeader.FromEntity(activity);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.MessageKey, Is.EqualTo("ActivityMessages.PartitionCount"));
            Assert.That(header.MessageCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void ExecutionItemDetail_Error_CarriesItsKeyAndArguments()
    {
        var item = new ActivityRunProfileExecutionItem { ErrorMessageDetail = RoleMemberAdded() };

        var dto = ActivityRunProfileExecutionItemDetailDto.FromEntity(item);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.ErrorMessage, Is.EqualTo("Added 'Ada Lovelace' to Role 'Administrators'"));
            Assert.That(dto.ErrorMessageKey, Is.EqualTo("ActivityMessages.RoleMemberAdded"));
            Assert.That(dto.ErrorMessageArguments, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void Progress_Narration_CarriesItsKeyBesideTheEnglish()
    {
        var progress = new ActivityProgress { ActivityId = Guid.NewGuid(), Status = ActivityStatus.InProgress, MessageDetail = RoleMemberAdded() };

        var dto = ActivityProgressDto.FromEntity(progress, new ActivityEtaEstimate(null, null), Moment);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Message, Is.EqualTo("Added 'Ada Lovelace' to Role 'Administrators'"));
            Assert.That(dto.MessageKey, Is.EqualTo("ActivityMessages.RoleMemberAdded"));
            Assert.That(dto.MessageArguments, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void Serialised_EachArgumentHasOnlyItsOwnValue()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ApiJsonConfiguration.Configure(options);
        var activity = new Activity { MessageDetail = JimMessage.Create(ApplicationMessageCatalogues.Activities, "FailureWithCause", "a", 7L) };

        var json = JsonSerializer.Serialize(ActivityHeader.FromEntity(activity), options);

        Assert.That(json, Does.Contain("\"messageArguments\":[{\"text\":\"a\"},{\"number\":7}]"));
    }
}
