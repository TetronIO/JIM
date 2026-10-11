# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# Configuration for scripts/Test-DocsConformance.ps1: the checked rules of the documentation
# standard (engineering/DOCUMENTATION_STANDARD.md, #1454). The standard defines each rule; this
# file says how strictly each is enforced today, and which pages have reached which bar.
#
# This folder starts with a dot, so MkDocs never publishes it.
@{
    # Pages the checks skip, as globs relative to docs/. Contributor pages leave the public site in
    # Phase 5 of the plan, and the changelog page is a one-line include of CHANGELOG.md.
    Exclude                 = @('CLAUDE.md', 'developer/**', 'reference/changelog.md')

    # Block: every finding fails. Ratchet: findings beyond the baseline fail, and the baseline only
    # shrinks. Warn: reported, never fails. Promote a Warn rule to Ratchet or Block once it has run
    # a release without a false positive; see the standard's "How the standard is enforced".
    # MNT-3 warns until the pages already over budget are split (plan Phases 3 and 4), then blocks:
    # blocking now would fail most feature pull requests, which document into those pages.
    Rules                   = @{
        'STR-1'  = 'Block'
        'STR-2'  = 'Ratchet'
        'STR-3'  = 'Block'
        'STR-4'  = 'Block'
        'STR-5'  = 'Block'
        'STR-6'  = 'Ratchet'
        'STR-7'  = 'Warn'
        'STR-8'  = 'Warn'
        'MNT-3'  = 'Warn'
        'A11Y-1' = 'Block'
        'A11Y-2' = 'Block'
        'TYP-1'  = 'Block'
    }

    RequiredFrontMatter     = @('title', 'description', 'type', 'audience')
    AllowedFrontMatter      = @('title', 'description', 'type', 'audience', 'template', 'hide', 'scenario')

    # One primary audience per page; see the standard's "Audiences".
    Audiences               = @('evaluator', 'implementer', 'administrator', 'operator', 'automation-author', 'security-assessor')

    # The shape each page type must have; see the standard's "Page types".
    PageTypes               = @{
        'tutorial'        = @{
            RequiredHeadings      = @('Before you begin', 'What you achieved')
            RequiredFrontMatter   = @('scenario')
            RequiresNumberedSteps = $true
        }
        'how-to'          = @{
            RequiredHeadings      = @('Check it worked')
            RequiresNumberedSteps = $true
        }
        'concept'         = @{
            ForbiddenTitles = @('Overview', 'Introduction', 'How it works')
        }
        'reference'       = @{}
        'troubleshooting' = @{
            RequiredHeadings = @('Still stuck?')
        }
        'landing'         = @{}
        'object'          = @{
            TrailingHeadings = @('Settings', 'Troubleshooting', 'Related')
        }
    }

    DescriptionLength       = @{ Min = 50; Max = 160 }

    SizeBudgetWords         = 5000
    PrioritySizeBudgetWords = 3000

    StubPatterns            = @('Documentation In Progress', 'Content will be added', 'This page is under active development')

    # The pages that decide whether JIM is adopted, installed and trusted (PRD requirement 36).
    # They migrate first; once listed under ConformantPages too, warnings fail on them and the
    # priority size budget applies.
    PriorityPages           = @(
        'index.md'
        'concepts/index.md'
        'concepts/synchronisation-pipeline.md'
        'getting-started/prerequisites.md'
        'administration/deployment.md'
        'getting-started/first-synchronisation.md'
        'configuration/connected-systems.md'
        'configuration/synchronisation-rules.md'
        'connectors/jim-ldap-connector.md'
        'concepts/jml-lifecycle.md'
        'configuration/run-profiles.md'
        'configuration/activities.md'
        'administration/troubleshooting.md'
        'administration/upgrading.md'
        'administration/backup-recovery.md'
    )

    # Pages and folders migrated to the standard: no baseline, and Ratchet rules fail outright.
    # A folder or page is added once every page in it declares its type and has no baseline
    # entries, and is never taken off again.
    ConformantFolders       = @()
    ConformantPages         = @()

    # Where a tutorial's integration scenario lives, from its 'scenario' front matter value.
    ScenarioPathFormat      = 'test/integration/scenarios/Invoke-Scenario-{0}.ps1'
}
