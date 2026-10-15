# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

# The product vocabulary, read by scripts/Test-DocsConformance.ps1 (TERM-1, LANG-3, LANG-4; see
# engineering/DOCUMENTATION_STANDARD.md, #1454).
#
# The glossary (docs/reference/glossary.md) is the one place each product noun is named and
# defined: its entry gives the canonical form and the anchor. This file holds only what the
# glossary cannot: for each entry, keyed by its anchor,
#
#   Home       the page that explains the term (HOME-1), relative to docs/
#   TitleCase  whether the term is held to Title Case in running text (LANG-3). Off for the
#              single words that are also ordinary English, where a lower-case use is usually
#              right ("a schedule", "the import"), and for phrases that are not names.
#   Rejected   other names for the same thing, which a page must not use (LANG-3)
#
# TERM-1 checks this file against the glossary both ways: every glossary entry has an entry
# here, and every entry here has a glossary entry. The portal's term hints (TermDefinitions)
# are checked against the glossary by GlossaryTermConsistencyTests, so the three agree.
#
# This folder starts with a dot, so MkDocs never publishes it.
@{
    # LANG-4: abbreviations allowed only in tight table headers, each with its full term. A
    # page may introduce one with the full term ("Metaverse Object (MVO)").
    Abbreviations = @{
        'CSO'  = 'Connected System Object'
        'MVO'  = 'Metaverse Object'
        'RPEI' = 'Run Profile Execution Item'
    }

    Terms         = @(
        @{ Anchor = 'activity'; Home = 'configuration/activities.md'; TitleCase = $false }
        @{ Anchor = 'attribute-flow'; Home = 'configuration/attribute-mappings.md'; TitleCase = $true }
        @{ Anchor = 'attribute-priority'; Home = 'concepts/attribute-priority.md'; TitleCase = $true }
        @{ Anchor = 'causality-explorer'; Home = 'configuration/causality.md'; TitleCase = $true }
        @{ Anchor = 'configuration-change-history'; Home = 'configuration/activities.md'; TitleCase = $true }
        @{ Anchor = 'configuration-change-preview'; Home = 'configuration/configuration-changes.md'; TitleCase = $true }
        @{ Anchor = 'connected-system'; Home = 'configuration/connected-systems.md'; TitleCase = $true }
        @{ Anchor = 'connected-system-object'; Home = 'configuration/connector-space.md'; TitleCase = $true; Rejected = @('Connector Space Object') }
        @{ Anchor = 'connector'; Home = 'connectors/index.md'; TitleCase = $false }
        @{ Anchor = 'connector-space'; Home = 'configuration/connector-space.md'; TitleCase = $true }
        @{ Anchor = 'container-scope'; Home = 'configuration/partitions-and-containers.md'; TitleCase = $true }
        @{ Anchor = 'delta-synchronisation'; Home = 'concepts/synchronisation-pipeline.md'; TitleCase = $true }
        @{ Anchor = 'deprovisioning'; Home = 'concepts/jml-lifecycle.md'; TitleCase = $false }
        @{ Anchor = 'deprovisioning-action'; Home = 'configuration/deprovisioning.md'; TitleCase = $true }
        @{ Anchor = 'drift-correction'; Home = 'configuration/synchronisation-rules.md'; TitleCase = $true }
        @{ Anchor = 'export'; Home = 'concepts/synchronisation-pipeline.md'; TitleCase = $false }
        @{ Anchor = 'expression'; Home = 'concepts/expressions.md'; TitleCase = $false }
        @{ Anchor = 'full-synchronisation'; Home = 'concepts/synchronisation-pipeline.md'; TitleCase = $true }
        @{ Anchor = 'grace-period'; Home = 'concepts/jml-lifecycle.md'; TitleCase = $false }
        @{ Anchor = 'identity'; Home = 'concepts/architecture.md'; TitleCase = $false }
        @{ Anchor = 'import'; Home = 'concepts/synchronisation-pipeline.md'; TitleCase = $false }
        @{ Anchor = 'initial-export-only'; Home = 'configuration/attribute-mappings.md'; TitleCase = $true }
        @{ Anchor = 'join'; Home = 'configuration/synchronisation-rules.md'; TitleCase = $false }
        @{ Anchor = 'metaverse'; Home = 'configuration/metaverse.md'; TitleCase = $true }
        @{ Anchor = 'metaverse-object'; Home = 'configuration/metaverse.md'; TitleCase = $true }
        @{ Anchor = 'null-is-a-value'; Home = 'concepts/attribute-priority.md'; TitleCase = $false }
        @{ Anchor = 'object-matching-rule'; Home = 'configuration/object-matching-rules.md'; TitleCase = $true }
        @{ Anchor = 'obsoletion'; Home = 'configuration/connector-space.md'; TitleCase = $false }
        @{ Anchor = 'partition'; Home = 'configuration/partitions-and-containers.md'; TitleCase = $false }
        @{ Anchor = 'password-synchronisation'; Home = 'concepts/passwords.md'; TitleCase = $true }
        @{ Anchor = 'pending-export'; Home = 'configuration/connected-systems.md'; TitleCase = $true }
        @{ Anchor = 'projection'; Home = 'configuration/synchronisation-rules.md'; TitleCase = $false }
        @{ Anchor = 'provisioning'; Home = 'configuration/synchronisation-rules.md'; TitleCase = $false }
        @{ Anchor = 'run-profile'; Home = 'configuration/run-profiles.md'; TitleCase = $true }
        @{ Anchor = 'safeguards'; Home = 'configuration/run-profiles.md'; TitleCase = $false }
        @{ Anchor = 'schedule'; Home = 'configuration/schedules.md'; TitleCase = $false }
        @{ Anchor = 'scoping'; Home = 'configuration/scoping-criteria.md'; TitleCase = $false }
        @{ Anchor = 'service-health'; Home = 'configuration/operations.md'; TitleCase = $true }
        @{ Anchor = 'standard-mappings'; Home = 'configuration/metaverse.md'; TitleCase = $true }
        @{ Anchor = 'sync-preview'; Home = 'configuration/sync-preview.md'; TitleCase = $true }
        @{ Anchor = 'synchronisation-rule'; Home = 'configuration/synchronisation-rules.md'; TitleCase = $true; Rejected = @('Sync Rule') }
        @{ Anchor = 'temporal-scope-reconciliation'; Home = 'configuration/schedules.md'; TitleCase = $true }
    )
}
