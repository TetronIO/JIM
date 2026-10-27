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

    def test_text_wider_than_the_box_wraps_onto_two_lines(self):
        lines = diagrams.wrap("Object Matching Rules", "label", 110, self.m)
        self.assertEqual(lines, ["Object Matching", "Rules"])

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

    def test_a_flow_without_a_container_or_ends_still_renders(self):
        spec = {"archetype": "flow", "title": "t", "desc": "d",
                "steps": [{"id": "a", "label": "First"}, {"id": "b", "label": "Second"}]}
        self.assertEqual(len(svgs(render(spec))), 2)


class HookTests(unittest.TestCase):
    PAGE = "Intro paragraph.\n\n```jim-diagram\n{spec}\n```\n\n## Next\n"

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
