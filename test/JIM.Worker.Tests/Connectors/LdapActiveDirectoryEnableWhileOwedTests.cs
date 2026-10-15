// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.DirectoryServices.Protocols;
using System.Globalization;
using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// The export channel and the password channel against one account, under Active Directory's rule that an enabled
/// account must have a password (#2158). An Attribute Flow enables an account whose Initial Password is still queued;
/// Active Directory refuses that, so the Connector sends the enable disabled and reports it awaiting the password; the
/// delivery then sets the password and completes the enable through the compare-and-swap of the account's flags
/// (#2161). The closest stand-in for real Active Directory these tests can reach: both channels share one stateful
/// directory, so each sees what the other wrote.
/// </summary>
[TestFixture]
public class LdapActiveDirectoryEnableWhileOwedTests
{
    private const string UserDn = "CN=Joe Bloggs,OU=Users,DC=corp,DC=local";

    [Test]
    public async Task EnablingUpdateThenPasswordDelivery_EndsEnabledWithThePasswordAsync()
    {
        var directory = new PasswordRuleDirectory(userAccountControl: 514);

        var exported = await ExportAsync(directory, EnablingUpdate(initialPasswordFollows: true));
        var flagsAfterExport = directory.UserAccountControl;
        var delivered = await PasswordChannel(directory).SetPasswordAsync(UserDn, "Correct-Horse-9",
            new PasswordSetOptions { ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy, EnableAccount = true },
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exported.Success, Is.True, exported.ErrorMessage);
            Assert.That(exported.AttributesAwaitingInitialPassword, Is.EqualTo(new[] { "userAccountControl" }));
            Assert.That(flagsAfterExport, Is.EqualTo(514), "held disabled until the password exists");
            Assert.That(delivered.Success, Is.True, delivered.ErrorMessage);
            Assert.That(directory.HasPassword, Is.True);
            Assert.That(directory.UserAccountControl, Is.EqualTo(512), "the delivery completes the enable the flow asked for");
        }
    }

    [Test]
    public async Task EnablingUpdateAfterThePasswordLanded_IsAppliedAtOnceAsync()
    {
        var directory = new PasswordRuleDirectory(userAccountControl: 514);
        var delivered = await PasswordChannel(directory).SetPasswordAsync(UserDn, "Correct-Horse-9",
            new PasswordSetOptions { ExpiryBehaviour = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy },
            CancellationToken.None);

        var exported = await ExportAsync(directory, EnablingUpdate(initialPasswordFollows: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delivered.Success, Is.True, delivered.ErrorMessage);
            Assert.That(exported.Success, Is.True, exported.ErrorMessage);
            Assert.That(exported.AttributesAwaitingInitialPassword, Is.Null, "nothing was withheld");
            Assert.That(directory.UserAccountControl, Is.EqualTo(512));
            Assert.That(directory.RefusedEnables, Is.Zero);
        }
    }

    private static async Task<ConnectedSystemExportResult> ExportAsync(PasswordRuleDirectory directory, PendingExport pendingExport)
    {
        var export = new LdapConnectorExport(directory,
            [new ConnectedSystemSettingValue { Setting = new ConnectorDefinitionSetting { Name = "Delete Behaviour" }, StringValue = "Delete" }],
            Log.Logger,
            exportConcurrency: 1,
            LdapConnectorConstants.DEFAULT_MODIFY_BATCH_SIZE,
            LdapDirectoryType.ActiveDirectory,
            placeholderMemberDn: null);
        return (await export.ExecuteAsync([pendingExport], CancellationToken.None)).Single();
    }

    private static LdapConnectorPassword PasswordChannel(PasswordRuleDirectory directory) =>
        new(directory, Log.Logger, LdapDirectoryType.ActiveDirectory, supportsPasswordModifyExtension: false, isConnectionEncrypted: true);

    private static PendingExport EnablingUpdate(bool initialPasswordFollows)
    {
        var type = new ConnectedSystemObjectType { Name = "user" };
        var dnAttribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "distinguishedName", ConnectedSystemObjectType = type };
        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            InitialPasswordFollows = initialPasswordFollows,
            ConnectedSystemObject = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = type,
                SecondaryExternalIdAttributeId = dnAttribute.Id,
                AttributeValues = [new ConnectedSystemObjectAttributeValue { Attribute = dnAttribute, AttributeId = dnAttribute.Id, StringValue = UserDn }]
            },
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Attribute = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "userAccountControl", ConnectedSystemObjectType = type },
                    ChangeType = PendingExportAttributeChangeType.Update,
                    IntValue = 512
                }
            ]
        };
    }

    /// <summary>
    /// One Active Directory user account, holding its <c>userAccountControl</c> and whether it has a password. Like
    /// Active Directory it refuses, whole, a modify that would leave the account enabled with no password
    /// (<c>0000052D</c>), applies a delete-and-add of <c>userAccountControl</c> only while the value deleted is the one it
    /// holds (refusing with noSuchAttribute otherwise), and throws for every refusal, as LdapConnection does.
    /// </summary>
    private sealed class PasswordRuleDirectory(int userAccountControl) : ILdapOperationExecutor
    {
        public int UserAccountControl { get; private set; } = userAccountControl;
        public bool HasPassword { get; private set; }
        public int RefusedEnables { get; private set; }

        public DirectoryResponse SendRequest(DirectoryRequest request) => request switch
        {
            ModifyRequest modify => Modify(modify),
            SearchRequest search => LdapTestResponses.SearchResponseWith(search.DistinguishedName,
                ("userAccountControl", UserAccountControl.ToString(CultureInfo.InvariantCulture))),
            _ => throw new NotSupportedException($"The fake directory does not answer {request.GetType().Name}")
        };

        public DirectoryResponse SendRequest(DirectoryRequest request, TimeSpan timeout) => SendRequest(request);

        public Task<DirectoryResponse> SendRequestAsync(DirectoryRequest request) => Task.FromResult(SendRequest(request));

        private ModifyResponse Modify(ModifyRequest request)
        {
            var flags = UserAccountControl;
            var hasPassword = HasPassword;
            foreach (var modification in request.Modifications.Cast<DirectoryAttributeModification>())
            {
                if (modification.Name.Equals("unicodePwd", StringComparison.OrdinalIgnoreCase))
                {
                    hasPassword = true;
                    continue;
                }

                if (!modification.Name.Equals("userAccountControl", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = int.Parse((string)modification[0], CultureInfo.InvariantCulture);
                switch (modification.Operation)
                {
                    case DirectoryAttributeOperation.Delete when value != flags:
                        throw Refusal(ResultCode.NoSuchAttribute, "00002080: AtrErr: DSID-03151F8D, #1: 0: 00002080: DSID-03151F8D, problem 1001 (NO_ATTRIBUTE_OR_VAL), data 0, Att 90008 (userAccountControl)");
                    case DirectoryAttributeOperation.Delete:
                        break;
                    default:
                        flags = value;
                        break;
                }
            }

            if ((flags & LdapConnectorConstants.UAC_ACCOUNTDISABLE) == 0 && !hasPassword)
            {
                RefusedEnables++;
                throw Refusal(ResultCode.UnwillingToPerform, "0000052D: SvcErr: DSID-031A12C5, problem 5003 (WILL_NOT_PERFORM), data 0\n");
            }

            UserAccountControl = flags;
            HasPassword = hasPassword;
            return LdapTestResponses.Create<ModifyResponse>(ResultCode.Success);
        }

        private static DirectoryOperationException Refusal(ResultCode resultCode, string message) =>
            new(LdapTestResponses.Create<ModifyResponse>(resultCode, message));
    }
}
