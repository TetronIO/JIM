# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

"""Unit tests for docs/hooks/diagrams.py, the jim-diagram renderer.

Run from the repository root:

    python3 -m unittest discover -s docs/hooks/tests

Standard library only, so the tests need nothing installed beyond Python. The one
test that exercises the Markdown hook's YAML parsing is skipped when PyYAML is
missing; it is installed with the docs toolchain (docs/requirements.txt), which is
where the build uses it.
"""

import copy
import re
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import diagrams  # noqa: E402

try:
    import yaml  # noqa: F401
    HAVE_YAML = True
except ImportError:
    HAVE_YAML = False


FLOW = {
    "archetype": "flow",
    "title": "What a Synchronisation Rule does to an object on its way in",
    "desc": "An inbound Synchronisation Rule scopes, matches, projects and maps a Connected System Object.",
    "in": {"label": "Connected System Object", "note": "Connector Space"},
    "out": {"label": "Metaverse Object", "note": "the Metaverse"},
    "container": {"label": "Synchronisation Rule", "tag": "Direction: inbound", "badge": 1},
    "steps": [
        {"id": "scope", "label": "Scoping criteria", "badge": 2, "note": "out of scope: skipped"},
        {"id": "match", "label": "Object Matching Rules", "badge": 3, "next": "match"},
        {"id": "map", "label": "Attribute mappings", "badge": 5},
    ],
    "detours": [
        {"from": "match", "to": "map", "label": "no match",
         "step": {"id": "project", "label": "Projection", "sub": "creates one", "badge": 4}},
    ],
}


def render(spec, uid="t"):
    return diagrams.render(copy.deepcopy(spec), uid)


def svgs(html):
    return re.findall(r"<svg\b.*?</svg>", html, re.S)


def note_extents(svg, contains=""):
    """(left, right, baseline) of each middle-anchored note line, measured with the real metrics."""
    metrics = diagrams.Metrics.load()
    out = []
    for x, y, body in re.findall(r'<text class="jimdg-dg-note" x="([\d.]+)" y="([\d.]+)">(.*?)</text>', svg):
        text = re.sub(r"<[^>]+>", "", body)
        if contains in text:
            w = metrics.measure(text, "note")
            out.append((float(x) - w / 2, float(x) + w / 2, float(y)))
    return out


class MetricsTests(unittest.TestCase):
    def test_width_sums_glyph_advances_scaled_to_the_font_size(self):
        m = diagrams.Metrics({"sans": {"400": {"default": 1000, "chars": {"a": 500, "b": 250}}}})
        self.assertAlmostEqual(m.width("ab", 10, 400), 7.5)

    def test_an_unknown_character_measures_as_the_default(self):
        m = diagrams.Metrics({"sans": {"400": {"default": 1000, "chars": {"a": 500}}}})
        self.assertAlmostEqual(m.width("a中", 10, 400), 15)

    def test_the_committed_metrics_load_and_measure_plex(self):
        m = diagrams.Metrics.load()
        self.assertGreater(m.width("Synchronisation Rule", 13, 600), 100)
        self.assertAlmostEqual(m.width("abc", 10, 400, mono=True), 18, places=1)


class WrapTests(unittest.TestCase):
    def setUp(self):
        self.m = diagrams.Metrics({"sans": {"600": {"default": 500, "chars": {}}}})

    def test_short_text_stays_on_one_line(self):
        self.assertEqual(diagrams.wrap("Scoping criteria", "label", 1000, self.m), ["Scoping criteria"])

    def test_text_wider_than_the_box_wraps_onto_the_two_most_even_lines(self):
        lines = diagrams.wrap("Object Matching Rules", "label", 110, self.m)
        self.assertEqual(lines, ["Object", "Matching Rules"])

    def test_wrapping_does_not_leave_a_short_word_alone(self):
        lines = diagrams.wrap("reads it and changes it in either state", "label", 160, self.m)
        self.assertEqual(lines, ["reads it and changes", "it in either state"])

    def test_text_that_cannot_fit_on_two_lines_is_an_error(self):
        with self.assertRaisesRegex(diagrams.DiagramError, "does not fit"):
            diagrams.wrap("Supercalifragilisticexpialidocious", "label", 60, self.m)


class ValidationTests(unittest.TestCase):
    def test_title_is_required(self):
        spec = dict(FLOW)
        del spec["title"]
        with self.assertRaisesRegex(diagrams.DiagramError, "title"):
            render(spec)

    def test_desc_is_required(self):
        spec = dict(FLOW)
        del spec["desc"]
        with self.assertRaisesRegex(diagrams.DiagramError, "desc"):
            render(spec)

    def test_an_unknown_archetype_is_an_error(self):
        spec = dict(FLOW, archetype="spiral")
        with self.assertRaisesRegex(diagrams.DiagramError, "spiral"):
            render(spec)

    def test_an_unknown_key_is_an_error_so_typos_cannot_pass_silently(self):
        spec = dict(FLOW, stpes=[])
        with self.assertRaisesRegex(diagrams.DiagramError, "stpes"):
            render(spec)

    def test_an_unknown_step_key_is_an_error(self):
        spec = copy.deepcopy(FLOW)
        spec["steps"][0]["lable"] = "x"
        with self.assertRaisesRegex(diagrams.DiagramError, "lable"):
            render(spec)

    def test_a_flow_needs_at_least_two_steps(self):
        spec = copy.deepcopy(FLOW)
        spec["steps"] = spec["steps"][:1]
        spec["detours"] = []
        with self.assertRaisesRegex(diagrams.DiagramError, "two"):
            render(spec)

    def test_step_ids_are_unique(self):
        spec = copy.deepcopy(FLOW)
        spec["steps"][1]["id"] = "scope"
        with self.assertRaisesRegex(diagrams.DiagramError, "scope"):
            render(spec)

    def test_a_detour_names_existing_steps_in_order(self):
        spec = copy.deepcopy(FLOW)
        spec["detours"][0]["from"] = "map"
        spec["detours"][0]["to"] = "match"
        with self.assertRaisesRegex(diagrams.DiagramError, "detour"):
            render(spec)

    def test_a_detour_step_cannot_carry_a_note_its_edge_would_cross(self):
        spec = copy.deepcopy(FLOW)
        spec["steps"][1]["note"] = "a note under the detour's source"
        with self.assertRaisesRegex(diagrams.DiagramError, "note"):
            render(spec)

    def test_a_flow_too_wide_for_the_wide_layout_is_an_error(self):
        spec = copy.deepcopy(FLOW)
        spec["steps"] = [{"id": f"s{i}", "label": "A rather long step label"} for i in range(7)]
        spec["detours"] = []
        with self.assertRaisesRegex(diagrams.DiagramError, "too wide"):
            render(spec)


class OutputTests(unittest.TestCase):
    def setUp(self):
        self.html = render(FLOW, "page-1")

    def test_the_output_is_a_container_holding_a_wide_and_a_narrow_svg(self):
        self.assertTrue(self.html.startswith('<div class="jim-dg jim-dg-flow">'))
        found = svgs(self.html)
        self.assertEqual(len(found), 2)
        self.assertIn("jim-dg-wide", found[0])
        self.assertIn("jim-dg-narrow", found[1])

    def test_both_svgs_are_concept_diagrams_the_lightbox_and_theming_pick_up(self):
        for svg in svgs(self.html):
            self.assertRegex(svg, r'class="jim-diagram jim-dg-(wide|narrow)"')
            self.assertIn('role="img"', svg)

    def test_the_wide_layout_is_800_units_and_the_narrow_360(self):
        wide, narrow = svgs(self.html)
        self.assertRegex(wide, r'viewBox="0 0 800 \d+(\.\d+)?"')
        self.assertRegex(narrow, r'viewBox="0 0 360 \d+(\.\d+)?"')

    def test_each_svg_carries_the_title_and_desc_with_unique_ids(self):
        wide, narrow = svgs(self.html)
        for svg, suffix in ((wide, "w"), (narrow, "n")):
            self.assertIn(f'aria-labelledby="jimdg-page-1-{suffix}-t jimdg-page-1-{suffix}-d"', svg)
            self.assertIn(f'<title id="jimdg-page-1-{suffix}-t">{FLOW["title"]}</title>', svg)
            self.assertIn(f'<desc id="jimdg-page-1-{suffix}-d">', svg)

    def test_text_is_escaped(self):
        spec = copy.deepcopy(FLOW)
        spec["steps"][0]["label"] = "A < B & C"
        self.assertIn("A &lt; B &amp; C", render(spec))

    def test_every_label_appears(self):
        for label in ("Scoping criteria", "Attribute mappings", "Projection", "creates one", "no match", "match",
                      "Direction: inbound", "Connector Space"):
            self.assertIn(label, self.html)

    def test_badges_are_drawn(self):
        self.assertEqual(len(re.findall(r'class="jimdg-dg-badge"', svgs(self.html)[0])), 5)

    def test_there_are_no_colour_literals_only_theme_classes(self):
        self.assertNotRegex(self.html, r"#[0-9a-fA-F]{3,8}\b|rgba?\(|fill=\"(?!none)|stroke=\"")

    def test_there_are_no_blank_lines_which_would_end_the_raw_html_block(self):
        self.assertNotRegex(self.html, r"\n\s*\n")

    def test_every_node_groups_its_box_with_its_text(self):
        wide = svgs(self.html)[0]
        nodes = re.findall(r'<g class="jimdg-dg-node">(.*?)</g>', wide, re.S)
        self.assertEqual(len(nodes), 6)  # in, three steps, the detour step, out
        for node in nodes:
            self.assertEqual(node.count("<rect"), 1)
            self.assertGreaterEqual(node.count("<text"), 1)

    def test_a_note_wider_than_its_room_wraps_instead_of_running_into_its_neighbour(self):
        spec = {"archetype": "flow", "title": "t", "desc": "d", "steps": [
            {"id": "a", "label": "The first step", "note": "a note too long for one line here"},
            {"id": "b", "label": "The second step", "note": "and another under its neighbour"}]}
        wide = svgs(render(spec))[0]
        notes = note_extents(wide)
        self.assertGreater(len(notes), 2, "each note should wrap onto two lines")
        for i, (l1, r1, y1) in enumerate(notes):
            for l2, r2, y2 in notes[i + 1:]:
                if y1 == y2:
                    self.assertTrue(r1 <= l2 or r2 <= l1, f"notes overlap: {notes}")

    def test_a_note_too_long_for_two_lines_is_an_error(self):
        spec = {"archetype": "flow", "title": "t", "desc": "d", "steps": [
            {"id": "a", "label": "First", "note": "a note so long that even two lines under a small box "
                                                  "cannot possibly hold all of it without running away"},
            {"id": "b", "label": "Second"}]}
        with self.assertRaisesRegex(diagrams.DiagramError, "note"):
            render(spec)

    def test_an_end_note_stays_outside_the_container(self):
        spec = copy.deepcopy(FLOW)
        spec["out"]["note"] = "every target sees one value"
        wide = svgs(render(spec))[0]
        hub = re.search(r'class="jimdg-hub" x="([\d.]+)" y="[\d.]+" width="([\d.]+)"', wide)
        right = float(hub.group(1)) + float(hub.group(2))
        for left, _, _ in note_extents(wide, "every target"):
            self.assertGreaterEqual(left, right)

    def test_a_flow_without_a_container_or_ends_still_renders(self):
        spec = {"archetype": "flow", "title": "t", "desc": "d",
                "steps": [{"id": "a", "label": "First"}, {"id": "b", "label": "Second"}]}
        self.assertEqual(len(svgs(render(spec))), 2)


LIFECYCLE = {
    "archetype": "lifecycle",
    "title": "The Connected System lifecycle, one cmdlet per step",
    "desc": "New-JIMConnectedSystem creates a Connected System; imports fill it; Clear-JIMConnectedSystem empties it; Remove-JIMConnectedSystem deletes it.",
    "start": {"label": "`New-JIMConnectedSystem`"},
    "container": {"label": "Connected System",
                  "note": "`Get-JIMConnectedSystem` reads it and `Set-JIMConnectedSystem` changes it, in either state"},
    "states": [
        {"id": "configured", "label": "Configured", "sub": "no objects yet"},
        {"id": "holding", "label": "Holding objects", "sub": "Connector Space filled"},
    ],
    "transitions": [
        {"from": "configured", "to": "holding", "label": "imported by a Run Profile"},
        {"from": "holding", "to": "configured", "label": "`Clear-JIMConnectedSystem`",
         "note": "keeps configuration, schema and rules"},
    ],
    "end": {"from": "container", "label": "`Remove-JIMConnectedSystem`",
            "note": "check first with `Get-JIMConnectedSystemDeletionPreview`",
            "state": {"label": "Deleted", "sub": "with all its data"}},
}

ANATOMY_VALUE = {
    "archetype": "anatomy",
    "title": "What an API key is made of",
    "desc": "An API key is the prefix jim_ak_ and a random secret; JIM keeps only a SHA-256 hash of the secret.",
    "subject": {"kind": "value", "parts": [
        {"text": "jim_ak_", "strong": True, "callout": "Prefix: always visible"},
        {"id": "secret", "text": "\u2022" * 18, "callout": "Secret: shown once, at creation"},
    ]},
    "derived": {"from": "secret", "label": "stored as", "box": "SHA-256 hash",
                "note": "all JIM keeps; a lost secret means a new key"},
    "properties": [
        {"name": "Roles", "value": "what it may do"},
        {"name": "Expiry", "value": "a date, or none"},
        {"name": "Enabled", "value": "on or off"},
        {"name": "Last used", "value": "time and source IP"},
    ],
}

ANATOMY_OBJECT = {
    "archetype": "anatomy",
    "title": "What a Connected System holds",
    "desc": "A Connected System holds its connection settings, schema, Connector Space and Run Profiles.",
    "subject": {"kind": "object", "label": "Connected System", "parts": [
        {"label": "Connection settings", "detail": "from its Connector"},
        {"label": "Schema", "detail": "Object Types and attributes"},
        {"label": "Connector Space", "detail": "its Connected System Objects"},
        {"label": "Run Profiles", "detail": "import, synchronise, export"},
    ]},
}


class CodeSpanTests(unittest.TestCase):
    def setUp(self):
        self.m = diagrams.Metrics({"sans": {"400": {"default": 500, "chars": {}}},
                                   "mono": {"400": {"default": 600, "chars": {}}}})

    def test_a_code_span_is_measured_in_the_mono_face(self):
        plain = self.m.measure("ab", "note")
        code = self.m.measure("`ab`", "note")
        self.assertAlmostEqual(code / plain, 600 / 500)

    def test_a_code_span_renders_as_a_mono_tspan_without_its_backticks(self):
        out = diagrams.Canvas().text(0, 0, "run `Get-Thing` first", "note")
        self.assertIn('<tspan class="jimdg-dg-code">Get-Thing</tspan>', out)
        self.assertNotIn("`", out)

    def test_wrapping_keeps_punctuation_on_its_code_span(self):
        text = "a page, `-All` or `-Count`; filter by change type or status"
        lines = diagrams.wrap(text, "note", self.m.measure(text, "note") * 0.6, self.m)
        self.assertEqual(len(lines), 2)
        self.assertEqual(" ".join(lines), text)

    def test_a_property_chip_renders_code_spans(self):
        spec = copy.deepcopy(ANATOMY_VALUE)
        spec["properties"][0]["value"] = "`$null` when unset"
        html = render(spec)
        self.assertIn('<tspan class="jimdg-dg-code">$null</tspan>', html)
        self.assertNotIn("`", html)

    def test_wrapping_never_splits_a_code_span(self):
        lines = diagrams.wrap("check first with `Get-JIMConnectedSystemDeletionPreview`", "note", 300, self.m)
        self.assertEqual(lines[-1], "`Get-JIMConnectedSystemDeletionPreview`")


class LifecycleTests(unittest.TestCase):
    def setUp(self):
        self.html = render(LIFECYCLE, "lc")

    def test_it_renders_a_wide_and_a_narrow_svg(self):
        self.assertTrue(self.html.startswith('<div class="jim-dg jim-dg-lifecycle">'))
        self.assertEqual(len(svgs(self.html)), 2)

    def test_every_state_and_transition_appears(self):
        for text in ("Configured", "Holding objects", "Deleted", "imported by a Run Profile",
                     "Clear-JIMConnectedSystem", "Remove-JIMConnectedSystem", "keeps configuration, schema and rules"):
            self.assertIn(text, self.html)

    def test_the_end_state_is_drawn_as_a_terminal(self):
        self.assertEqual(svgs(self.html)[0].count("jimdg-dg-terminal"), 1)

    def test_the_start_is_a_dot(self):
        self.assertEqual(svgs(self.html)[0].count('class="jimdg-dg-start"'), 1)

    def test_a_transition_between_states_that_are_not_neighbours_is_an_error(self):
        spec = copy.deepcopy(LIFECYCLE)
        spec["states"].append({"id": "archived", "label": "Archived"})
        spec["transitions"].append({"from": "configured", "to": "archived", "label": "skips one"})
        with self.assertRaisesRegex(diagrams.DiagramError, "next to each other"):
            render(spec)

    def test_two_transitions_the_same_way_between_one_pair_is_an_error(self):
        spec = copy.deepcopy(LIFECYCLE)
        spec["transitions"].append({"from": "configured", "to": "holding", "label": "again"})
        with self.assertRaisesRegex(diagrams.DiagramError, "already"):
            render(spec)

    def test_a_lifecycle_needs_two_to_four_states(self):
        spec = copy.deepcopy(LIFECYCLE)
        spec["states"] = spec["states"][:1]
        spec["transitions"] = []
        with self.assertRaisesRegex(diagrams.DiagramError, "two to four"):
            render(spec)

    def test_the_end_names_a_state_or_the_container(self):
        spec = copy.deepcopy(LIFECYCLE)
        spec["end"]["from"] = "nowhere"
        with self.assertRaisesRegex(diagrams.DiagramError, "nowhere"):
            render(spec)

    def test_a_forward_transition_note_is_drawn(self):
        spec = copy.deepcopy(LIFECYCLE)
        spec["transitions"][0]["note"] = "a forward note"
        for svg in svgs(render(spec)):
            self.assertIn("a forward note", svg)

    def test_the_narrow_start_arrow_runs_into_the_first_state(self):
        spec = copy.deepcopy(LIFECYCLE)
        del spec["container"]
        del spec["end"]
        narrow = svgs(render(spec))[1]
        cx = float(re.search(r'class="jimdg-dg-start" cx="([\d.]+)"', narrow).group(1))
        x, w = (float(v) for v in re.search(r'class="jimdg-stage" x="([\d.]+)" y="[\d.]+" width="([\d.]+)"',
                                            narrow).groups())
        self.assertTrue(x < cx < x + w, f"start dot at {cx}, first state spans {x} to {x + w}")

    def test_the_narrow_end_label_sits_beside_its_arrow_above_the_terminal(self):
        narrow = svgs(self.html)[1]
        term_y = float(re.search(r'class="jimdg-dg-terminal" x="[\d.]+" y="([\d.]+)"', narrow).group(1))
        label_y = float(re.search(r'<text class="jimdg-dg-edge[^"]*" x="[\d.]+" y="([\d.]+)">'
                                  r'<tspan class="jimdg-dg-code">Remove-JIMConnectedSystem', narrow).group(1))
        self.assertLess(label_y, term_y)

    def test_an_end_from_a_framed_state_puts_its_label_below_the_frame(self):
        spec = copy.deepcopy(LIFECYCLE)
        spec["end"]["from"] = "holding"
        spec["end"]["label"] = "it ends"
        for svg in svgs(render(spec)):
            hub = re.search(r'class="jimdg-hub" x="[\d.]+" y="([\d.]+)" width="[\d.]+" height="([\d.]+)"', svg)
            frame_bottom = float(hub.group(1)) + float(hub.group(2))
            label_y = float(re.search(r'y="([\d.]+)">it ends<', svg).group(1))
            self.assertGreater(label_y - 10, frame_bottom, "the end label's text must clear the frame's border")

    def test_an_end_from_a_state_with_a_later_state_is_an_error(self):
        spec = copy.deepcopy(LIFECYCLE)
        spec["end"]["from"] = "configured"
        with self.assertRaisesRegex(diagrams.DiagramError, "last state"):
            render(spec)

    def test_it_has_no_colour_literals_or_blank_lines(self):
        self.assertNotRegex(self.html, r"#[0-9a-fA-F]{3,8}\b|rgba?\(|\n\s*\n")


class AnatomyTests(unittest.TestCase):
    def test_a_value_is_drawn_as_a_token_with_its_parts_callouts_and_properties(self):
        html = render(ANATOMY_VALUE, "an")
        self.assertTrue(html.startswith('<div class="jim-dg jim-dg-anatomy">'))
        wide = svgs(html)[0]
        self.assertIn("jim_ak_", wide)
        self.assertIn("jimdg-dg-token-strong", wide)
        for text in ("Prefix: always visible", "Secret: shown once, at creation", "SHA-256 hash", "stored as"):
            self.assertIn(text, wide)
        self.assertEqual(wide.count('class="jimdg-dg-chip-name"'), 4)

    def test_an_object_is_drawn_as_a_card_listing_its_parts(self):
        wide = svgs(render(ANATOMY_OBJECT, "ob"))[0]
        self.assertIn("jimdg-dg-card", wide)
        for text in ("Connected System", "Schema", "its Connected System Objects"):
            self.assertIn(text, wide)

    def test_derived_must_name_a_part_of_a_value(self):
        spec = copy.deepcopy(ANATOMY_VALUE)
        spec["derived"]["from"] = "nope"
        with self.assertRaisesRegex(diagrams.DiagramError, "nope"):
            render(spec)

    def test_at_most_four_properties(self):
        spec = copy.deepcopy(ANATOMY_VALUE)
        spec["properties"].append({"name": "Five", "value": "too many"})
        with self.assertRaisesRegex(diagrams.DiagramError, "four"):
            render(spec)

    def test_the_subject_kind_must_be_value_or_object(self):
        spec = copy.deepcopy(ANATOMY_OBJECT)
        spec["subject"]["kind"] = "blob"
        with self.assertRaisesRegex(diagrams.DiagramError, "blob"):
            render(spec)


DECISION = {
    "archetype": "decision",
    "title": "How an Object Matching Rule decides a join",
    "desc": "Rules are tried in order: one match joins, several is an Ambiguous Match, a rule that cannot be worked out fails, and none tries the next rule until none are left.",
    "start": {"label": "Connected System Object"},
    "questions": [
        {"id": "rule", "label": "Next Object Matching Rule", "sub": "how many Metaverse Objects match?"},
        {"id": "more", "label": "More rules?"},
    ],
    "outcomes": [
        {"id": "joined", "label": "Joined", "sub": "the rule and value recorded", "good": True},
        {"id": "ambiguous", "label": "Ambiguous Match", "sub": "nothing joins or projects"},
        {"id": "failed", "label": "Object Matching Rule Failed", "sub": "no later rule is tried"},
        {"id": "none", "label": "No match", "sub": "projection or provisioning may create one"},
    ],
    "answers": [
        {"from": "rule", "to": "joined", "label": "exactly one"},
        {"from": "rule", "to": "ambiguous", "label": "several"},
        {"from": "rule", "to": "failed", "label": "cannot be worked out"},
        {"from": "rule", "to": "more", "label": "none, or no value"},
        {"from": "more", "to": "rule", "label": "yes: try it"},
        {"from": "more", "to": "none", "label": "no"},
    ],
}

COMPARISON = {
    "archetype": "comparison",
    "title": "Full and Delta runs compared",
    "desc": "Full runs read or evaluate every object; Delta runs only what changed. Export has no Full or Delta form.",
    "columns": [{"label": "Full", "sub": "every object, every time"}, {"label": "Delta", "sub": "only what changed, faster"}],
    "rows": [
        {"label": "Import", "cells": [
            {"text": "reads every object", "sub": "one no longer returned is deleted", "meter": 12},
            {"text": "reads changes since the last import", "sub": "needs a connector that tracks changes", "meter": 3}]},
        {"label": "Synchronisation", "cells": [
            {"text": "evaluates every Connected System Object", "sub": "previewable before it runs", "meter": 12},
            {"text": "evaluates objects with pending changes", "meter": 3}]},
    ],
    "footer": "Export flushes the Pending Exports either way: it has no Full or Delta form",
}

MAP = {
    "archetype": "map",
    "title": "How JIM's core terms connect",
    "desc": "A Connector reaches a Connected System, whose Connector Space holds Connected System Objects; a Synchronisation Rule flows them into Metaverse Objects.",
    "nodes": [
        {"id": "connector", "label": "Connector", "link": "#connector", "row": 0, "col": 0},
        {"id": "cs", "label": "Connected System", "link": "#connected-system", "row": 0, "col": 1},
        {"id": "rp", "label": "Run Profile", "link": "#run-profile", "row": 0, "col": 2},
        {"id": "space", "label": "Connector Space", "link": "#connector-space", "row": 1, "col": 1},
        {"id": "cso", "label": "Connected System Object", "link": "#connected-system-object", "row": 2, "col": 1},
        {"id": "sr", "label": "Synchronisation Rule", "link": "#synchronisation-rule", "row": 2, "col": 2},
        {"id": "mvo", "label": "Metaverse Object", "link": "#metaverse-object", "row": 2, "col": 3},
    ],
    "edges": [
        {"from": "connector", "to": "cs", "label": "reaches"},
        {"from": "rp", "to": "cs", "label": "runs against"},
        {"from": "cs", "to": "space", "label": "has its"},
        {"from": "space", "to": "cso", "label": "holds"},
        {"from": "cso", "to": "sr", "label": "flows through"},
        {"from": "sr", "to": "mvo", "label": "into"},
    ],
}


class DecisionTests(unittest.TestCase):
    def test_it_renders_questions_outcomes_and_answers(self):
        html = render(DECISION, "de")
        self.assertTrue(html.startswith('<div class="jim-dg jim-dg-decision">'))
        wide = svgs(html)[0]
        for text in ("Next Object Matching Rule", "More rules?", "Joined", "Ambiguous Match", "No match",
                     "exactly one", "cannot be worked out", "yes: try it"):
            self.assertIn(text, wide)
        self.assertEqual(wide.count('class="jimdg-dg-question"'), 2)

    def test_a_good_outcome_is_drawn_solid(self):
        wide = svgs(render(DECISION, "de"))[0]
        self.assertEqual(wide.count('class="jimdg-core"'), 1)

    def test_an_answer_names_a_question_or_an_outcome(self):
        spec = copy.deepcopy(DECISION)
        spec["answers"][0]["to"] = "nowhere"
        with self.assertRaisesRegex(diagrams.DiagramError, "nowhere"):
            render(spec)

    def test_an_answer_to_a_later_question_must_be_the_next_one(self):
        spec = copy.deepcopy(DECISION)
        spec["questions"].append({"id": "third", "label": "Third?"})
        spec["answers"].append({"from": "rule", "to": "third", "label": "skip"})
        with self.assertRaisesRegex(diagrams.DiagramError, "next question"):
            render(spec)

    def test_every_outcome_is_reached(self):
        spec = copy.deepcopy(DECISION)
        spec["outcomes"].append({"id": "orphan", "label": "Orphan"})
        with self.assertRaisesRegex(diagrams.DiagramError, "orphan"):
            render(spec)


    def test_two_answers_cannot_share_an_outcome(self):
        spec = copy.deepcopy(DECISION)
        first = next(a for a in spec["answers"] if a["to"] in {o["id"] for o in spec["outcomes"]})
        spec["answers"].append({"from": spec["questions"][-1]["id"], "to": first["to"], "label": "also"})
        with self.assertRaisesRegex(diagrams.DiagramError, "more than one answer"):
            render(spec)


class ComparisonTests(unittest.TestCase):
    def test_it_renders_a_grid_of_cells_with_meters(self):
        html = render(COMPARISON, "co")
        self.assertTrue(html.startswith('<div class="jim-dg jim-dg-comparison">'))
        wide = svgs(html)[0]
        self.assertEqual(wide.count('class="jimdg-dg-dot-on"'), 12 + 3 + 12 + 3)
        self.assertEqual(wide.count('class="jimdg-dg-dot"'), 9 + 9)
        self.assertIn("Export flushes the Pending Exports", wide)

    def test_each_row_has_a_cell_per_column(self):
        spec = copy.deepcopy(COMPARISON)
        spec["rows"][0]["cells"].pop()
        with self.assertRaisesRegex(diagrams.DiagramError, "cell"):
            render(spec)

    def test_a_meter_is_zero_to_twelve(self):
        spec = copy.deepcopy(COMPARISON)
        spec["rows"][0]["cells"][0]["meter"] = 13
        with self.assertRaisesRegex(diagrams.DiagramError, "meter"):
            render(spec)


class MapTests(unittest.TestCase):
    def test_every_node_links_to_its_entry(self):
        html = render(MAP, "mp")
        self.assertTrue(html.startswith('<div class="jim-dg jim-dg-map">'))
        wide = svgs(html)[0]
        self.assertEqual(wide.count('<a href="#'), 7)
        self.assertIn('<a href="#connected-system-object"', wide)

    def test_an_edge_runs_along_a_row_or_a_column(self):
        spec = copy.deepcopy(MAP)
        spec["edges"].append({"from": "connector", "to": "mvo", "label": "diagonal"})
        with self.assertRaisesRegex(diagrams.DiagramError, "row or a column"):
            render(spec)

    def test_an_edge_cannot_pass_through_another_node(self):
        spec = copy.deepcopy(MAP)
        spec["edges"].append({"from": "cs", "to": "cso", "label": "skips the space"})
        with self.assertRaisesRegex(diagrams.DiagramError, "through"):
            render(spec)

    def test_two_nodes_cannot_share_a_cell(self):
        spec = copy.deepcopy(MAP)
        spec["nodes"][1]["col"] = 0
        with self.assertRaisesRegex(diagrams.DiagramError, "same place"):
            render(spec)

    def test_a_link_is_a_page_anchor_or_relative_path(self):
        spec = copy.deepcopy(MAP)
        spec["nodes"][0]["link"] = "javascript:alert(1)"
        with self.assertRaisesRegex(diagrams.DiagramError, "link"):
            render(spec)


class HookTests(unittest.TestCase):
    PAGE = "Intro paragraph.\n\n```jim-diagram\n{spec}\n```\n\n## Next\n"

    @unittest.skipUnless(HAVE_YAML, "PyYAML is installed with the docs toolchain")
    def test_a_page_link_is_resolved_to_its_built_url_and_an_anchor_is_left_alone(self):
        import json
        spec = copy.deepcopy(MAP)
        spec["nodes"][0]["link"] = "other-page.md#a-heading"
        spec["nodes"][1]["link"] = "#an-anchor"
        out = diagrams.replace_fences(self.PAGE.format(spec=json.dumps(spec)), "p.md",
                                      resolve=lambda link: "../built/" + link.upper())
        self.assertIn('href="../built/OTHER-PAGE.MD#A-HEADING"', out)
        self.assertIn('href="#an-anchor"', out)
        self.assertNotIn('href="other-page.md', out)

    def test_a_page_without_a_diagram_is_unchanged(self):
        md = "# Title\n\n```powershell\nGet-Thing\n```\n"
        self.assertEqual(diagrams.replace_fences(md, "p.md", lambda s: s), md)

    @unittest.skipUnless(HAVE_YAML, "PyYAML is installed with the docs toolchain")
    def test_a_fence_is_replaced_by_the_rendered_diagram(self):
        import json
        md = self.PAGE.format(spec=json.dumps(FLOW))
        out = diagrams.replace_fences(md, "configuration/synchronisation-rules.md")
        self.assertNotIn("```jim-diagram", out)
        self.assertIn('<div class="jim-dg jim-dg-flow">', out)
        self.assertIn("jimdg-configuration-synchronisation-rules-1-w-t", out)
        self.assertTrue(out.startswith("Intro paragraph.\n\n<div"))
        self.assertIn("</div>\n\n## Next", out)

    @unittest.skipUnless(HAVE_YAML, "PyYAML is installed with the docs toolchain")
    def test_an_invalid_spec_names_the_page_and_line(self):
        md = self.PAGE.format(spec="archetype: flow")
        with self.assertRaisesRegex(diagrams.DiagramError, r"p\.md:3"):
            diagrams.replace_fences(md, "p.md")


if __name__ == "__main__":
    unittest.main()
