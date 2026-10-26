# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

"""Publish only released versions of CHANGELOG.md on the docs site.

CHANGELOG.md opens with contributor-facing preamble (the Keep a Changelog and
Semantic Versioning notes) and an [Unreleased] section of work not yet shipped.
Neither belongs in customer documentation, so docs/reference/changelog.md
carries its own introduction and this hook replaces the placeholder there with
the changelog from the first released version onwards. The link definitions at
the foot of the file come along with it.
"""

import re
from pathlib import Path

PLACEHOLDER = "<!-- released-changelog -->"
CHANGELOG = Path(__file__).resolve().parents[2] / "CHANGELOG.md"

# A released version heading, such as "## [0.16.0] - 2026-09-29".
FIRST_RELEASE = re.compile(r"^## \[\d", re.MULTILINE)


def on_page_markdown(markdown, page, **kwargs):
    if page.file.src_uri != "reference/changelog.md":
        return markdown
    if PLACEHOLDER not in markdown:
        raise ValueError(f"reference/changelog.md has no {PLACEHOLDER} placeholder")
    text = CHANGELOG.read_text(encoding="utf-8")
    match = FIRST_RELEASE.search(text)
    if match is None:
        raise ValueError("CHANGELOG.md has no released version heading")
    return markdown.replace(PLACEHOLDER, text[match.start():])
