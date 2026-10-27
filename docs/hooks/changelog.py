# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

"""Publish only released versions of CHANGELOG.md on the docs site.

CHANGELOG.md opens with contributor-facing preamble (the Keep a Changelog and
Semantic Versioning notes) and an [Unreleased] section of work not yet shipped.
Neither belongs in customer documentation, so docs/reference/changelog.md
carries its own introduction and this hook replaces the placeholder there with
the changelog from the first released version onwards. The link definitions at
the foot of the file come along with it.

Each changelog entry leads with an emoji naming its kind (a convention for the
changelog itself, see engineering/CLAUDE.md, which the GitHub release notes
keep). The docs site uses no emoji (STY-1 in engineering/DOCUMENTATION_STANDARD.md),
so the hook strips each entry's leading emoji; the section headings (Added,
Fixed and so on) already say what kind each entry is.
"""

import re
from pathlib import Path

PLACEHOLDER = "<!-- released-changelog -->"
CHANGELOG = Path(__file__).resolve().parents[2] / "CHANGELOG.md"

# A released version heading, such as "## [0.16.0] - 2026-09-29".
FIRST_RELEASE = re.compile(r"^## \[\d", re.MULTILINE)

# An entry's leading emoji: a list marker, then one emoji (with its variation
# selector, if any) and the space after it. The marker and indent are kept. The
# emoji pattern is STY-1's, from scripts/Test-DocsConformance.ps1 (described
# there) and scripts/Test-DocsSite.ps1; keep the three in step.
LEADING_EMOJI = re.compile(
    r"^([ \t]*[-*] )"
    r"(?:[\u2600-\u27BF\u2B05-\u2B07\u2B1B\u2B1C\u2B50\u2B55\u231A\u231B\u23E9-\u23FA"
    r"\u2139\u24C2\u25AA\u25AB\u25B6\u25C0\u25FB-\u25FE\u21A9\u21AA\u2934\u2935"
    r"\u3030\u303D\u3297\u3299\u203C\u2049\U0001F000-\U0001FAFF]\uFE0F?|\S\uFE0F)[ \t]*",
    re.MULTILINE,
)


def on_page_markdown(markdown, page, **kwargs):
    if page.file.src_uri != "reference/changelog.md":
        return markdown
    if PLACEHOLDER not in markdown:
        raise ValueError(f"reference/changelog.md has no {PLACEHOLDER} placeholder")
    text = CHANGELOG.read_text(encoding="utf-8")
    match = FIRST_RELEASE.search(text)
    if match is None:
        raise ValueError("CHANGELOG.md has no released version heading")
    released = LEADING_EMOJI.sub(r"\1", text[match.start():])
    return markdown.replace(PLACEHOLDER, released)
