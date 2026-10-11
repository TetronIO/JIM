// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Transactional;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class SyncPreviewMessageDtoTests
{
    [Test]
    public void FromModel_AmbiguousMatch_CarriesItsCandidates()
    {
        var candidateId = Guid.NewGuid();
        var model = new SyncPreviewMessage
        {
            Code = SyncPreviewMessageCode.AmbiguousMatch,
            Detail = "This object matches 2 Metaverse Objects.",
            MatchCandidates =
            [
                new ActivityRunProfileExecutionItemMatchCandidate
                {
                    CandidateKind = MatchCandidateKind.MetaverseObject,
                    CandidateId = candidateId,
                    DisplayNameSnapshot = "Ann Bloggs",
                    MatchedValue = "48213",
                    ObjectMatchingRuleId = 3,
                    CandidateTotal = 2
                }
            ]
        };

        var dto = SyncPreviewMessageDto.FromModel(model);

        var candidate = dto.MatchCandidates.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidate.CandidateId, Is.EqualTo(candidateId));
            Assert.That(candidate.DisplayNameSnapshot, Is.EqualTo("Ann Bloggs"));
            Assert.That(candidate.MatchedValue, Is.EqualTo("48213"));
            Assert.That(candidate.ObjectMatchingRuleId, Is.EqualTo(3));
            Assert.That(candidate.CandidateTotal, Is.EqualTo(2));
        }
    }
}
