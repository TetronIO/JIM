# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# Configuration for scripts/Test-DocsConformance.ps1: the checked rules of the documentation
# standard (engineering/DOCUMENTATION_STANDARD.md, #1454). The standard defines each rule; this
# file says how strictly each is enforced today, and which pages have reached which bar.
#
# This folder starts with a dot, so MkDocs never publishes it.
@{
    # Pages the checks skip, as globs relative to docs/. Contributor pages leave the public site in
    # Phase 5 of the plan.
    Exclude                 = @('CLAUDE.md')

    # Block: every finding fails. Ratchet: findings beyond the baseline fail, and the baseline only
    # shrinks. Warn: reported, never fails. Promote a Warn rule to Ratchet or Block once it has run
    # a release without a false positive; see the standard's "How the standard is enforced".
    # MNT-3 blocks now that no page is over budget, so none can grow past it unnoticed.
    Rules                   = @{
        'STR-1'  = 'Block'
        'STR-2'  = 'Ratchet'
        'STR-3'  = 'Block'
        'STR-4'  = 'Block'
        'STR-5'  = 'Block'
        'STR-6'  = 'Ratchet'
        'STR-7'  = 'Warn'
        'STR-8'  = 'Warn'
        'MNT-3'  = 'Block'
        'A11Y-1' = 'Block'
        'A11Y-2' = 'Block'
        'TYP-1'  = 'Block'
        # A tutorial's Portal tab names only labels the portal shows (Phase 3).
        'TUT-2'  = 'Block'
        # A conformant priority page records the release it was last checked against, which
        # /release reads to list the pages due for re-validation (Phase 3).
        'PRI-1'  = 'Block'
        # The language rules (Phase 2a). Small debt was fixed before they landed, so they block;
        # lower-case product nouns, prose abbreviations and minimisers ratchet down from the baseline.
        'LANG-1' = 'Block'
        'LANG-2' = 'Block'
        'LANG-3' = 'Ratchet'
        'LANG-4' = 'Ratchet'
        'LANG-5' = 'Block'
        'LANG-6' = 'Block'
        'LANG-7' = 'Ratchet'
        'LANG-8' = 'Block'
        'TERM-1' = 'Block'
        # No emoji: icons come from Material Design Icons, which the theme ships (STY-1, STY-3).
        'STY-1'  = 'Block'
        # Heading icons on a section's index page, every H2, and nowhere else (STY-3).
        'STY-3'  = 'Block'
    }

    RequiredFrontMatter     = @('title', 'description', 'type', 'audience')
    AllowedFrontMatter      = @('title', 'description', 'type', 'audience', 'template', 'hide', 'scenario', 'validated')

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
        'administration/tls-and-reverse-proxy.md'
        'getting-started/first-synchronisation.md'
        'configuration/connected-systems.md'
        'configuration/synchronisation-rules.md'
        'connectors/jim-ldap-connector.md'
        'concepts/jml-lifecycle.md'
        'configuration/run-profiles.md'
        'configuration/activities.md'
        'administration/troubleshooting.md'
        'administration/troubleshooting-synchronisation.md'
        'administration/troubleshooting-podman.md'
        'administration/upgrading.md'
        'administration/upgrading-podman.md'
        'administration/backup-recovery.md'
    )

    # Pages and folders migrated to the standard: no baseline, and Ratchet rules fail outright.
    # A folder or page is added once every page in it declares its type and has no baseline
    # entries, and is never taken off again.
    ConformantFolders       = @('getting-started', 'api', 'reference', 'administration', 'configuration', 'concepts', 'connectors', 'powershell')
    ConformantPages         = @(
        'index.md'
        'concepts/index.md'
        'getting-started/prerequisites.md'
        'getting-started/first-synchronisation.md'
        'configuration/run-profiles.md'
        'administration/troubleshooting-synchronisation.md'
        'administration/backup-recovery.md'
        'concepts/synchronisation-pipeline.md'
        'concepts/jml-lifecycle.md'
        'administration/troubleshooting.md'
        'administration/troubleshooting-podman.md'
        'administration/upgrading.md'
        'administration/upgrading-podman.md'
        'configuration/activities.md'
        'administration/deployment.md'
        'administration/tls-and-reverse-proxy.md'
        'configuration/connected-systems.md'
        'configuration/synchronisation-rules.md'
        'connectors/jim-ldap-connector.md'
    )

    # Where a tutorial's integration scenario lives, from its 'scenario' front matter value.
    ScenarioPathFormat      = 'test/integration/scenarios/Invoke-Scenario-{0}.ps1'

    # TUT-2: where the portal's labels live. Its markup and code, the Connectors' setting names and
    # options, the names JIM seeds (the built-in Metaverse Object Types) and the comparison wording. Never the tests.
    PortalLabelSources      = @('src/JIM.Web', 'src/JIM.Connectors', 'src/JIM.Application', 'src/JIM.Utilities')

    # The glossary: each product noun's canonical form and anchor (LANG-3, TERM-1). What is
    # enforced for each noun is in vocabulary.psd1, beside this file.
    GlossaryPage            = 'reference/glossary.md'

    # LANG-2. Whole words, matched in any case, each with its British form. Words ending -ize,
    # -izes, -ized, -izing, -ization and so on are found by pattern instead, less the English
    # words that only look like them (IzeExceptions). Some American spellings are left out
    # because British usage shares them in this field: dialog (a software dialog), program
    # (a computer program), toward, license (the verb).
    AmericanSpellings       = @{
        'analyze'    = 'analyse'
        'analyzed'   = 'analysed'
        'analyzes'   = 'analyses'
        'analyzing'  = 'analysing'
        'behavior'   = 'behaviour'
        'behaviors'  = 'behaviours'
        'behavioral' = 'behavioural'
        'canceled'   = 'cancelled'
        'canceling'  = 'cancelling'
        'catalog'    = 'catalogue'
        'catalogs'   = 'catalogues'
        'center'     = 'centre'
        'centers'    = 'centres'
        'centered'   = 'centred'
        'color'      = 'colour'
        'colors'     = 'colours'
        'defense'    = 'defence'
        'enroll'     = 'enrol'
        'favor'      = 'favour'
        'favorite'   = 'favourite'
        'fulfill'    = 'fulfil'
        'gray'       = 'grey'
        'honor'      = 'honour'
        'labeled'    = 'labelled'
        'labeling'   = 'labelling'
        'modeled'    = 'modelled'
        'modeling'   = 'modelling'
        'neighbor'   = 'neighbour'
        'offense'    = 'offence'
        'traveled'   = 'travelled'
        'traveling'  = 'travelling'
    }
    IzeExceptions           = @('size', 'sizes', 'sized', 'sizing', 'resize', 'resizes', 'resized', 'resizing', 'downsize', 'downsized',
        'upsize', 'oversize', 'oversized', 'undersized', 'seize', 'seizes', 'seized', 'seizing', 'prize', 'prizes', 'capsize', 'maize', 'baize')
    # Third-party names and protocol terms, which keep their own spelling.
    AllowedSpellings        = @('authorization code', 'Authorization header', 'Global Catalog')

    # LANG-5, LANG-6 and LANG-7 (regular expressions, any case). Only the wording that is
    # wrong wherever it appears: "now", "currently", "no longer", "just" and "easy" are
    # usually right, so the advisory review judges them instead.
    TimeRelativePatterns    = @('\buntil now\b', '\b(?:from |since |as of |prior to |before )?this release\b', '\bin this version\b', '\(previously\b',
        '\bpreviously (?:called|named|known as)\b', '\bwhere it previously\b')
    FuturePatterns          = @('\bcoming soon\b', '\b(?:is|are) planned\b', '(?<!(?:is|are) )\bplanned for\b', '\bfuture (?:releases?|versions?|updates?)\b',
        '\bwill be (?:added|introduced|supported|available|provided|extended)\b')
    MinimiserPatterns       = @('\bsimply\b', '\bstraightforward\b', '\bobviously\b', '\bof course\b')
}
