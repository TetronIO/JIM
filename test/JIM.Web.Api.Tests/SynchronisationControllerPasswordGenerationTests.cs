// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Reading what a Connected System will accept, and asking JIM for a password that satisfies it.
/// <para>
/// Both existed only inside the portal: <c>IPasswordGeneratorService</c> has always been on the Application
/// tier and the portal's Generate button called it directly, so automation had to invent its own compliant
/// password or read the policy and implement the rules by hand. That is precisely the work JIM took on so
/// administrators would not have to.
/// </para>
/// <para>
/// The generate endpoint returns a password in its response body, which no other endpoint does. That is
/// deliberate and is not the thing the rest of this feature avoids: what JIM never does is <i>store</i> a
/// password, or return one nobody asked for. Here the caller asked, and is the only party that can use it.
/// </para>
/// </summary>
[TestFixture]
public class SynchronisationControllerPasswordGenerationTests
{
    private const int ConnectedSystemId = 3;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);

        _application = new JimApplication(_mockRepository.Object);
        _controller = new SynchronisationController(
            new Mock<ILogger<SynchronisationController>>().Object,
            _application,
            new DynamicExpressoEvaluator(),
            new Mock<ICredentialProtectionService>().Object);

        // The generate endpoint writes cache-control headers, so it needs a real response to write them to.
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId))
            .ReturnsAsync(new ConnectedSystem { Id = ConnectedSystemId, Name = "Contoso AD" });
    }

    #region Discovered policy read

    [Test]
    public async Task GetConnectedSystemPasswordPolicy_WithADiscoveredPolicy_ReportsWhatTheTargetDemandsAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetPasswordPolicyAsync(ConnectedSystemId)).ReturnsAsync(new ConnectedSystemPasswordPolicy
        {
            ConnectedSystemId = ConnectedSystemId,
            MinimumLength = 14,
            ComplexityRequired = true,
            RequiredCharacterClassCount = 3,
            RecognisedCharacterClasses = PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase | PasswordCharacterClasses.Digit,
            PasswordHistoryLength = 24,
            MaximumPasswordAge = TimeSpan.FromDays(90),
            FineGrainedPolicySignal = FineGrainedPolicySignal.Absent,
            Discovered = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc)
        });

        var result = await _controller.GetConnectedSystemPasswordPolicyAsync(ConnectedSystemId);
        var response = (ConnectedSystemPasswordPolicyResponse)((OkObjectResult)result).Value!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.MinimumLength, Is.EqualTo(14));
            Assert.That(response.RequiredCharacterClassCount, Is.EqualTo(3));
            Assert.That(response.PasswordHistoryLength, Is.EqualTo(24));
            Assert.That(response.MaximumPasswordAgeDays, Is.EqualTo(90),
                "expressed in days rather than as a timespan, which JSON has no native form for");
            Assert.That(response.Discovered, Is.EqualTo(new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc)));
        }
    }

    /// <summary>
    /// A directory withholds what a caller may not see by omitting it, so "nothing discovered" is a real answer
    /// and not a failure. Reporting it as 404 would have a script treat an unreadable policy as a missing system.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemPasswordPolicy_WithNothingDiscovered_SaysSoRatherThanFailingAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetPasswordPolicyAsync(ConnectedSystemId)).ReturnsAsync((ConnectedSystemPasswordPolicy?)null);

        var result = await _controller.GetConnectedSystemPasswordPolicyAsync(ConnectedSystemId);
        var response = (ConnectedSystemPasswordPolicyResponse)((OkObjectResult)result).Value!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Discovered, Is.Null);
            Assert.That(response.HasAnyDiscoveredConstraint, Is.False);
            Assert.That(response.MinimumLength, Is.Null);
        }
    }

    [Test]
    public async Task GetConnectedSystemPasswordPolicy_ForASystemThatDoesNotExist_IsNotFoundAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(99)).ReturnsAsync((ConnectedSystem?)null);

        var result = await _controller.GetConnectedSystemPasswordPolicyAsync(99);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    #endregion

    #region Generating a password

    [Test]
    public async Task GenerateConnectedSystemPassword_FollowsTheDiscoveredPolicyAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetPasswordPolicyAsync(ConnectedSystemId)).ReturnsAsync(new ConnectedSystemPasswordPolicy
        {
            ConnectedSystemId = ConnectedSystemId,
            MinimumLength = 20,
            ComplexityRequired = true,
            RequiredCharacterClassCount = 3,
            RecognisedCharacterClasses = PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase |
                                         PasswordCharacterClasses.Digit | PasswordCharacterClasses.Symbol
        });

        var result = await _controller.GenerateConnectedSystemPasswordAsync(ConnectedSystemId);
        var response = (GeneratedPasswordResponse)((OkObjectResult)result).Value!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Password, Is.Not.Null.And.Not.Empty);
            Assert.That(response.Password!.Length, Is.GreaterThanOrEqualTo(20),
                "generated against what the target demands, which is the whole reason to ask JIM rather than invent one");
            Assert.That(response.GuaranteedCharacterClassCount, Is.GreaterThanOrEqualTo(3));
            Assert.That(response.SatisfiesDiscoveredPolicy, Is.True);
        }
    }

    /// <summary>
    /// Two calls must not produce the same password. Trivial to state and exactly the kind of thing a
    /// refactor of the generator's seeding could break without any other test noticing.
    /// </summary>
    [Test]
    public async Task GenerateConnectedSystemPassword_CalledTwice_DoesNotRepeatItselfAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetPasswordPolicyAsync(ConnectedSystemId)).ReturnsAsync((ConnectedSystemPasswordPolicy?)null);

        var first = (GeneratedPasswordResponse)((OkObjectResult)await _controller.GenerateConnectedSystemPasswordAsync(ConnectedSystemId)).Value!;
        var second = (GeneratedPasswordResponse)((OkObjectResult)await _controller.GenerateConnectedSystemPasswordAsync(ConnectedSystemId)).Value!;

        Assert.That(first.Password, Is.Not.EqualTo(second.Password));
    }

    [Test]
    public async Task GenerateConnectedSystemPassword_WithNoDiscoveredPolicy_StillProducesOneAndSaysItIsUnverifiedAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetPasswordPolicyAsync(ConnectedSystemId)).ReturnsAsync((ConnectedSystemPasswordPolicy?)null);

        var result = await _controller.GenerateConnectedSystemPasswordAsync(ConnectedSystemId);
        var response = (GeneratedPasswordResponse)((OkObjectResult)result).Value!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Password, Is.Not.Null.And.Not.Empty,
                "JIM's own defaults are better than whatever a script would invent");
            Assert.That(response.SatisfiesDiscoveredPolicy, Is.False,
                "there is no policy to satisfy, and claiming it complies would be a claim JIM cannot make");
        }
    }

    [Test]
    public async Task GenerateConnectedSystemPassword_ForASystemThatDoesNotExist_IsNotFoundAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(99)).ReturnsAsync((ConnectedSystem?)null);

        var result = await _controller.GenerateConnectedSystemPasswordAsync(99);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    /// <summary>
    /// The one endpoint in JIM whose body carries a password. It must never be cached, by a browser or by
    /// anything between JIM and the caller.
    /// </summary>
    [Test]
    public async Task GenerateConnectedSystemPassword_TellsEveryCacheNotToStoreTheResponseAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetPasswordPolicyAsync(ConnectedSystemId)).ReturnsAsync((ConnectedSystemPasswordPolicy?)null);

        await _controller.GenerateConnectedSystemPasswordAsync(ConnectedSystemId);

        Assert.That(_controller.Response.Headers.CacheControl.ToString(), Does.Contain("no-store"));
    }

    #endregion

    #region Generating across several systems

    /// <summary>
    /// Setting one password across a person's accounts is the case that most needs JIM to generate it: the
    /// administrator would otherwise have to guess a password acceptable to the strictest of several systems
    /// whose policies they cannot see. The generated password must satisfy all of them, not the first.
    /// </summary>
    [Test]
    public async Task GeneratePasswordForSystems_SatisfiesTheStrictestOfThemAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(7))
            .ReturnsAsync(new ConnectedSystem { Id = 7, Name = "Research LDAP" });
        _mockConnectedSystemRepo.Setup(r => r.GetPasswordPolicyAsync(ConnectedSystemId)).ReturnsAsync(new ConnectedSystemPasswordPolicy
        {
            ConnectedSystemId = ConnectedSystemId,
            MinimumLength = 8,
            RecognisedCharacterClasses = PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase |
                                         PasswordCharacterClasses.Digit | PasswordCharacterClasses.Symbol
        });
        _mockConnectedSystemRepo.Setup(r => r.GetPasswordPolicyAsync(7)).ReturnsAsync(new ConnectedSystemPasswordPolicy
        {
            ConnectedSystemId = 7,
            MinimumLength = 24,
            RecognisedCharacterClasses = PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase |
                                         PasswordCharacterClasses.Digit | PasswordCharacterClasses.Symbol
        });

        var result = await _controller.GeneratePasswordForSystemsAsync(
            new GeneratePasswordForSystemsRequest { ConnectedSystemIds = [ConnectedSystemId, 7] });
        var response = (GeneratedPasswordResponse)((OkObjectResult)result).Value!;

        Assert.That(response.Password!.Length, Is.GreaterThanOrEqualTo(24),
            "the longest minimum any of them demands, or the shorter system's password fails on the longer one");
    }

    /// <summary>
    /// Where no single password can satisfy every system, saying so beats handing back one that will be
    /// refused on the second account after the first has already been changed.
    /// </summary>
    [Test]
    public async Task GeneratePasswordForSystems_WhereThePoliciesCannotBeReconciled_SaysSoAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(7))
            .ReturnsAsync(new ConnectedSystem { Id = 7, Name = "Research LDAP" });
        _mockConnectedSystemRepo.Setup(r => r.GetPasswordPolicyAsync(ConnectedSystemId)).ReturnsAsync(new ConnectedSystemPasswordPolicy
        {
            ConnectedSystemId = ConnectedSystemId,
            MinimumLength = 8,
            RecognisedCharacterClasses = PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase
        });
        _mockConnectedSystemRepo.Setup(r => r.GetPasswordPolicyAsync(7)).ReturnsAsync(new ConnectedSystemPasswordPolicy
        {
            ConnectedSystemId = 7,
            MinimumLength = 2000,
            RequiredCharacterClassCount = 4,
            RecognisedCharacterClasses = PasswordCharacterClasses.Uppercase
        });

        var result = await _controller.GeneratePasswordForSystemsAsync(
            new GeneratePasswordForSystemsRequest { ConnectedSystemIds = [ConnectedSystemId, 7] });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>(),
            "a password that cannot work everywhere must not be handed back as though it will");
    }

    [Test]
    public async Task GeneratePasswordForSystems_WithNoSystemsNamed_IsRejectedAsync()
    {
        var result = await _controller.GeneratePasswordForSystemsAsync(
            new GeneratePasswordForSystemsRequest { ConnectedSystemIds = [] });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task GeneratePasswordForSystems_NamingASystemThatDoesNotExist_IsNotFoundAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(99)).ReturnsAsync((ConnectedSystem?)null);

        var result = await _controller.GeneratePasswordForSystemsAsync(
            new GeneratePasswordForSystemsRequest { ConnectedSystemIds = [ConnectedSystemId, 99] });

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    /// <summary>
    /// A system JIM could read nothing from is named rather than passed over, because the caller is about to
    /// set a password on it and JIM cannot promise it will be accepted.
    /// </summary>
    [Test]
    public async Task GeneratePasswordForSystems_WhereOneSystemDisclosedNothing_NamesItAsync()
    {
        _mockConnectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(7))
            .ReturnsAsync(new ConnectedSystem { Id = 7, Name = "Research LDAP" });
        _mockConnectedSystemRepo.Setup(r => r.GetPasswordPolicyAsync(ConnectedSystemId)).ReturnsAsync(new ConnectedSystemPasswordPolicy
        {
            ConnectedSystemId = ConnectedSystemId,
            MinimumLength = 14,
            RecognisedCharacterClasses = PasswordCharacterClasses.Uppercase | PasswordCharacterClasses.Lowercase |
                                         PasswordCharacterClasses.Digit
        });
        _mockConnectedSystemRepo.Setup(r => r.GetPasswordPolicyAsync(7)).ReturnsAsync((ConnectedSystemPasswordPolicy?)null);

        var result = await _controller.GeneratePasswordForSystemsAsync(
            new GeneratePasswordForSystemsRequest { ConnectedSystemIds = [ConnectedSystemId, 7] });
        var response = (GeneratedPasswordResponse)((OkObjectResult)result).Value!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Password, Is.Not.Null.And.Not.Empty);
            Assert.That(response.SystemsWithNoDiscoveredPolicy, Does.Contain("Research LDAP"));
        }
    }

    #endregion
}
