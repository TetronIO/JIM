# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.

"""Render jim-diagram specs in a page to inline SVG.

An author writes a diagram as a short spec, in the page, where it belongs:

    ```jim-diagram
    archetype: flow
    title: ...
    desc: ...
    steps: [...]
    ```

and this hook replaces the fence with the diagram before Markdown is converted.
Each spec names an archetype (a flow, for now; the others follow in later
phases of engineering/plans/doing/DOCS_DIAGRAM_ON_EVERY_PAGE.md), and the archetype
owns the layout, so the author only supplies what the diagram says.

Every diagram is drawn twice: a wide layout, 800 units across, drawn at close
to body-caption size in the reading column, and a narrow one, 360 units, for
phones. custom.css shows one per breakpoint ("Generated diagrams"); the hidden
one is display: none, so a screen reader meets one. Both carry the spec's title
and desc as <title> and <desc> (A11Y-2), and take every colour from the
--jimdg-* tokens through the same classes as the hand-drawn concept diagrams,
so they follow the light and dark themes.

Labels are sized with committed font metrics (diagram_metrics.json, generated
by scripts/Update-DiagramFontMetrics.ps1), because Python cannot measure the
webfont itself. A label that cannot fit fails the build rather than
overflowing: the fix is a shorter label, never a cleverer layout. Edge labels
sit beside their edges, never on them, so no label cut is needed.

The renderer is standard library only. PyYAML, which MkDocs depends on, is
imported only by the hook, so the unit tests (docs/hooks/tests/) and the
browser check (scripts/Test-Diagrams.ps1, through the command line below)
need nothing installed.

    python3 docs/hooks/diagrams.py specs.json > fragments.html
"""

import html
import json
import re
from pathlib import Path

METRICS_PATH = Path(__file__).with_name("diagram_metrics.json")

# A fence at the start of a line, holding a spec, closed by a bare fence.
FENCE = re.compile(r"^```jim-diagram[ \t]*\n(.*?)^```[ \t]*$", re.MULTILINE | re.DOTALL)


class DiagramError(Exception):
    """A spec the renderer cannot draw. The message says what to change."""


# ── Text ──────────────────────────────────────────────────────────────────────

# Every text role the renderer draws: its class, size (SVG units), weight and
# face. custom.css gives each class the same size and weight.
STYLES = {
    "label": ("jimdg-dg-label", 13, 600, False),
    "sub": ("jimdg-dg-sub", 12, 400, False),
    "note": ("jimdg-dg-note", 12, 400, False),
    "edge": ("jimdg-dg-edge", 12, 600, False),
    "title": ("jimdg-dg-title", 14, 700, False),
    "tag": ("jimdg-dg-tag", 12, 400, False),
    "badge": ("jimdg-dg-badge-text", 12, 700, False),
}

# Room left either side of measured text, for kerning the metrics leave out.
SLACK = 1.04


class Metrics:
    """Advance widths, in thousandths of an em, per face and weight."""

    _loaded = None

    def __init__(self, data):
        self.data = data

    @classmethod
    def load(cls):
        if cls._loaded is None:
            cls._loaded = cls(json.loads(METRICS_PATH.read_text(encoding="utf-8")))
        return cls._loaded

    def width(self, text, size, weight, mono=False):
        face = self.data["mono" if mono else "sans"]
        key = str(weight)
        if key not in face:
            key = min(face, key=lambda k: abs(int(k) - weight))
        table = face[key]
        chars, default = table["chars"], table["default"]
        return sum(chars.get(ch, default) for ch in text) * size / 1000

    def measure(self, text, role):
        _, size, weight, mono = STYLES[role]
        return self.width(text, size, weight, mono) * SLACK


def wrap(text, role, max_width, metrics):
    """Fit text into at most two lines no wider than max_width, or fail."""
    if metrics.measure(text, role) <= max_width:
        return [text]
    words = text.split()
    for cut in range(len(words) - 1, 0, -1):
        first, second = " ".join(words[:cut]), " ".join(words[cut:])
        if metrics.measure(first, role) <= max_width and metrics.measure(second, role) <= max_width:
            return [first, second]
    raise DiagramError(f'"{text}" does not fit a box {max_width:.0f} units wide on two lines; shorten it')


def esc(text):
    return html.escape(str(text), quote=False)


def num(value):
    return f"{value:.1f}".rstrip("0").rstrip(".")


class Canvas:
    """Collects one SVG's elements."""

    def __init__(self):
        self.parts = []

    def add(self, markup):
        self.parts.append(markup)

    def text(self, x, y, value, role, anchor="middle"):
        cls = STYLES[role][0] + ("" if anchor == "middle" else f" jimdg-anchor-{anchor}")
        return f'<text class="{cls}" x="{num(x)}" y="{num(y)}">{esc(value)}</text>'

    def arrow(self, points):
        """A polyline ending in an arrowhead, along axis-aligned segments."""
        (x1, y1), (x2, y2) = points[-2], points[-1]
        dx, dy = (x2 > x1) - (x2 < x1), (y2 > y1) - (y2 < y1)
        end = (x2 - 6 * dx, y2 - 6 * dy)
        line = " ".join(f"{num(x)},{num(y)}" for x, y in points[:-1] + [end])
        self.add(f'<polyline class="jimdg-flow" fill="none" points="{line}"/>')
        if dx:
            head = f"{num(x2)},{num(y2)} {num(x2 - 8 * dx)},{num(y2 - 4.5)} {num(x2 - 8 * dx)},{num(y2 + 4.5)}"
        else:
            head = f"{num(x2)},{num(y2)} {num(x2 - 4.5)},{num(y2 - 8 * dy)} {num(x2 + 4.5)},{num(y2 - 8 * dy)}"
        self.add(f'<polygon class="jimdg-flow-arrow" points="{head}"/>')

    def badge(self, cx, cy, value):
        return (f'<circle class="jimdg-dg-badge" cx="{num(cx)}" cy="{num(cy)}" r="10"/>'
                + self.text(cx, cy + 4, value, "badge"))


# ── Nodes ─────────────────────────────────────────────────────────────────────

PAD_X, PAD_Y, LINE = 12, 12, 16


class Node:
    """A box with a label (one or two lines), and optionally a sub-line, a note
    line inside the box (narrow layouts) and a badge on its corner."""

    def __init__(self, spec, max_width, min_width, metrics, cls, note_inside=False):
        self.id = spec.get("id")
        self.cls = cls
        self.badge = spec.get("badge")
        self.note = spec.get("note")
        self.lines = [(line, "label") for line in wrap(spec["label"], "label", max_width - 2 * PAD_X, metrics)]
        if spec.get("sub"):
            self.lines.append((spec["sub"], "sub"))
        if note_inside and self.note:
            self.lines.append((self.note, "note"))
        widest = max(metrics.measure(text, role) for text, role in self.lines)
        self.w = max(min_width, widest + 2 * PAD_X)
        self.h = 2 * PAD_Y + LINE * len(self.lines) + 4
        self.x = self.y = 0

    @property
    def cx(self):
        return self.x + self.w / 2

    @property
    def cy(self):
        return self.y + self.h / 2

    @property
    def right(self):
        return self.x + self.w

    @property
    def bottom(self):
        return self.y + self.h

    def draw(self, canvas):
        parts = [f'<rect class="{self.cls}" x="{num(self.x)}" y="{num(self.y)}" width="{num(self.w)}" '
                 f'height="{num(self.h)}" rx="8"/>']
        top = self.cy - LINE * (len(self.lines) - 1) / 2 + 4.5
        for i, (text, role) in enumerate(self.lines):
            parts.append(canvas.text(self.cx, top + i * LINE, text, role))
        if self.badge is not None:
            parts.append(canvas.badge(self.x, self.y, self.badge))
        canvas.add('<g class="jimdg-dg-node">' + "".join(parts) + "</g>")


# ── Spec validation ───────────────────────────────────────────────────────────

FLOW_KEYS = {"archetype", "title", "desc", "in", "out", "container", "steps", "detours"}
STEP_KEYS = {"id", "label", "sub", "badge", "note", "next"}
END_KEYS = {"label", "note"}
CONTAINER_KEYS = {"label", "tag", "badge"}
DETOUR_KEYS = {"from", "to", "label", "step"}


def check_keys(obj, allowed, where):
    if not isinstance(obj, dict):
        raise DiagramError(f"{where} must be a mapping")
    unknown = sorted(set(obj) - allowed)
    if unknown:
        raise DiagramError(f"{where} has unknown key(s) {', '.join(unknown)}; allowed: {', '.join(sorted(allowed))}")


def require(obj, key, where):
    if not obj.get(key):
        raise DiagramError(f"{where} needs a {key}")


def validate_flow(spec):
    check_keys(spec, FLOW_KEYS, "the diagram")
    steps = spec.get("steps") or []
    if len(steps) < 2:
        raise DiagramError("a flow needs at least two steps")
    ids = []
    for i, step in enumerate(steps):
        check_keys(step, STEP_KEYS, f"step {i + 1}")
        require(step, "id", f"step {i + 1}")
        require(step, "label", f"step {i + 1}")
        if step["id"] in ids:
            raise DiagramError(f'step id "{step["id"]}" is used twice')
        ids.append(step["id"])
    for end in ("in", "out"):
        if end in spec:
            check_keys(spec[end], END_KEYS, f"the {end} node")
            require(spec[end], "label", f"the {end} node")
    if "container" in spec:
        check_keys(spec["container"], CONTAINER_KEYS, "the container")
        require(spec["container"], "label", "the container")
    for d, detour in enumerate(spec.get("detours") or []):
        where = f"detour {d + 1}"
        check_keys(detour, DETOUR_KEYS, where)
        for key in ("from", "to", "step"):
            require(detour, key, where)
        if detour["from"] not in ids or detour["to"] not in ids or ids.index(detour["from"]) >= ids.index(detour["to"]):
            raise DiagramError(f"{where} must run from one step to a later one, naming both by id")
        check_keys(detour["step"], STEP_KEYS - {"note", "next"}, f"{where}'s step")
        require(detour["step"], "label", f"{where}'s step")
        for end in ("from", "to"):
            if steps[ids.index(detour[end])].get("note"):
                raise DiagramError(f'step "{detour[end]}" cannot have a note: {where}\'s edge would cross it')


# ── Flow: wide layout ─────────────────────────────────────────────────────────

WIDE, NARROW = 800, 360


def flow_wide(spec, metrics):
    canvas = Canvas()
    steps = [Node(s, 132, 72, metrics, "jimdg-stage") for s in spec["steps"]]
    by_id = {n.id: n for n in steps}
    ends = {k: Node(spec[k], 124, 88, metrics, "jimdg-system") for k in ("in", "out") if k in spec}
    container = spec.get("container")
    detours = [(d, Node(d["step"], 132, 92, metrics, "jimdg-stage")) for d in spec.get("detours") or []]

    gaps = []
    for s in spec["steps"][:-1]:
        label = s.get("next")
        gaps.append(max(26, metrics.measure(label, "edge") + 20) if label else 26)
    inner = sum(n.w for n in steps) + sum(gaps)
    pad = 14 if container else 0
    if container:
        inner = max(inner, metrics.measure(container["label"], "title") + 2 * pad)
    end_gap = 26
    total = inner + 2 * pad + sum(n.w + end_gap for n in ends.values())
    if total > WIDE - 12:
        raise DiagramError(f"the flow is too wide for the wide layout ({total:.0f} of {WIDE - 12} units); "
                           "shorten labels or use fewer steps")

    x = (WIDE - total) / 2
    if "in" in ends:
        ends["in"].x = x
        x += ends["in"].w + end_gap
    box_x = x
    x += pad + (inner - sum(n.w for n in steps) - sum(gaps)) / 2
    for i, node in enumerate(steps):
        node.x = x
        x += node.w + (gaps[i] if i < len(gaps) else 0)
    box_w = inner + 2 * pad
    if "out" in ends:
        ends["out"].x = box_x + box_w + end_gap

    y = 8
    head = 0
    if container:
        head = 30 + (20 if container.get("tag") else 0)
    row_top = y + head + (22 if container else 12)
    row_h = max(n.h for n in steps)
    for node in steps:
        node.y = row_top + (row_h - node.h) / 2
    mid = row_top + row_h / 2
    row_bottom = row_top + row_h
    has_notes = any(s.get("note") for s in spec["steps"])

    bottom = row_bottom + (30 if has_notes else 14)
    for detour, node in detours:
        a, b = by_id[detour["from"]], by_id[detour["to"]]
        node.x = (a.cx + b.cx) / 2 - node.w / 2
        node.y = row_bottom + 44
        if node.x < a.cx + 16 or node.right > b.cx - 16:
            raise DiagramError(f'the detour step "{detour["step"]["label"]}" does not fit between '
                               f'"{detour["from"]}" and "{detour["to"]}"; shorten its label or move them apart')
        bottom = max(bottom, node.bottom + 16)
    for node in ends.values():
        node.y = mid - node.h / 2
    height = bottom + (8 if container else 0)
    for key, node in ends.items():
        if spec[key].get("note"):
            height = max(height, node.bottom + 26)

    if container:
        canvas.add(f'<rect class="jimdg-hub" x="{num(box_x)}" y="{num(y)}" width="{num(box_w)}" '
                   f'height="{num(bottom - y)}" rx="10"/>')
        canvas.add(canvas.text(box_x + box_w / 2, y + 26, container["label"], "title"))
        if container.get("tag"):
            draw_tag(canvas, box_x + box_w / 2, y + 48, container, metrics)

    for i, node in enumerate(steps[:-1]):
        nxt = steps[i + 1]
        canvas.arrow([(node.right, mid), (nxt.x, mid)])
        if spec["steps"][i].get("next"):
            canvas.add(canvas.text((node.right + nxt.x) / 2, mid - 8, spec["steps"][i]["next"], "edge"))
    if "in" in ends:
        canvas.arrow([(ends["in"].right, mid), (steps[0].x, mid)])
    if "out" in ends:
        canvas.arrow([(steps[-1].right, mid), (ends["out"].x, mid)])
    for detour, node in detours:
        a, b = by_id[detour["from"]], by_id[detour["to"]]
        canvas.arrow([(a.cx, a.bottom), (a.cx, node.cy), (node.x, node.cy)])
        canvas.arrow([(node.right, node.cy), (b.cx, node.cy), (b.cx, b.bottom)])
        if detour.get("label"):
            canvas.add(canvas.text(a.cx - 8, a.bottom + 36, detour["label"], "edge", "end"))

    for node in steps + [n for _, n in detours] + list(ends.values()):
        node.draw(canvas)
    for s, node in zip(spec["steps"], steps):
        if s.get("note"):
            canvas.add(canvas.text(node.cx, row_bottom + 18, s["note"], "note"))
    for key, node in ends.items():
        if spec[key].get("note"):
            canvas.add(canvas.text(node.cx, node.bottom + 18, spec[key]["note"], "note"))
    return canvas, height


def draw_tag(canvas, cx, y, container, metrics):
    text = container["tag"]
    if container.get("badge") is None:
        canvas.add(canvas.text(cx, y, text, "tag"))
        return
    width = 20 + 6 + metrics.measure(text, "tag")
    left = cx - width / 2
    canvas.add(canvas.badge(left + 10, y - 4, container["badge"]))
    canvas.add(canvas.text(left + 26, y, text, "tag", "start"))


# ── Flow: narrow layout ───────────────────────────────────────────────────────

def flow_narrow(spec, metrics):
    canvas = Canvas()
    container = spec.get("container")
    has_detour = bool(spec.get("detours"))
    col_max = 200 if has_detour else 290
    cx = 116 if has_detour else NARROW / 2
    steps = [Node(s, col_max, 140, metrics, "jimdg-stage", note_inside=True) for s in spec["steps"]]
    by_id = {n.id: n for n in steps}
    ends = {k: Node(spec[k], col_max, 140, metrics, "jimdg-system", note_inside=True)
            for k in ("in", "out") if k in spec}
    detours = [(d, Node(d["step"], 118, 96, metrics, "jimdg-stage")) for d in spec.get("detours") or []]

    for node in steps + list(ends.values()):
        node.x = cx - node.w / 2
    if container:
        title_w = metrics.measure(container["label"], "title")
        tag_w = metrics.measure(container.get("tag") or "", "tag") + (26 if container.get("badge") is not None else 0)
        if max(title_w, tag_w) > NARROW - 24:
            raise DiagramError("the container's label or tag is too wide for the narrow layout; shorten it")

    y = 8
    if "in" in ends:
        ends["in"].y = y
        y = ends["in"].bottom + 30
    box_y = y
    if container:
        y += 30 + (20 if container.get("tag") else 0) + 16
    detour_after = {d["from"]: (d, n) for d, n in detours}
    for i, node in enumerate(steps):
        node.y = y
        y = node.bottom
        if i < len(steps) - 1:
            gap = 32
            if node.id in detour_after:
                gap = max(gap, detour_after[node.id][1].h + 40)
            y += gap
    for detour, node in detours:
        a, b = by_id[detour["from"]], by_id[detour["to"]]
        node.x = NARROW - 12 - node.w
        node.y = (a.cy + b.cy) / 2 - node.h / 2
        if node.x < max(a.right, b.right) + 24:
            raise DiagramError(f'the detour step "{detour["step"]["label"]}" does not fit beside the column '
                               "in the narrow layout; shorten the labels")
        if detour.get("label"):
            room = node.cx - a.right - 8
            if metrics.measure(detour["label"], "edge") > room:
                raise DiagramError(f'the detour label "{detour["label"]}" is too wide for the narrow layout')
    box_bottom = y + (16 if container else 0)
    if "out" in ends:
        ends["out"].y = box_bottom + 30
        height = ends["out"].bottom + 8
    else:
        height = box_bottom + 8

    if container:
        canvas.add(f'<rect class="jimdg-hub" x="6" y="{num(box_y)}" width="{NARROW - 12}" '
                   f'height="{num(box_bottom - box_y)}" rx="10"/>')
        canvas.add(canvas.text(NARROW / 2, box_y + 26, container["label"], "title"))
        if container.get("tag"):
            draw_tag(canvas, NARROW / 2, box_y + 48, container, metrics)

    right_limit = min((n.x for _, n in detours), default=NARROW) - 8
    for i, node in enumerate(steps[:-1]):
        nxt = steps[i + 1]
        canvas.arrow([(cx, node.bottom), (cx, nxt.y)])
        label = spec["steps"][i].get("next")
        if label:
            if cx + 8 + metrics.measure(label, "edge") > right_limit:
                raise DiagramError(f'the edge label "{label}" is too wide for the narrow layout')
            canvas.add(canvas.text(cx + 8, (node.bottom + nxt.y) / 2 + 4, label, "edge", "start"))
    # With a container, the ends join its edge: a line on to the first step would
    # cross the container's title and tag.
    if "in" in ends:
        canvas.arrow([(cx, ends["in"].bottom), (cx, box_y if container else steps[0].y)])
    if "out" in ends:
        canvas.arrow([(cx, box_bottom if container else steps[-1].bottom), (cx, ends["out"].y)])
    for detour, node in detours:
        a, b = by_id[detour["from"]], by_id[detour["to"]]
        canvas.arrow([(a.right, a.cy), (node.cx, a.cy), (node.cx, node.y)])
        canvas.arrow([(node.cx, node.bottom), (node.cx, b.cy), (b.right, b.cy)])
        if detour.get("label"):
            canvas.add(canvas.text((a.right + node.cx) / 2, a.cy - 7, detour["label"], "edge"))

    for node in steps + [n for _, n in detours] + list(ends.values()):
        node.draw(canvas)
    return canvas, height


# ── Rendering ─────────────────────────────────────────────────────────────────

ARCHETYPES = {"flow": (validate_flow, flow_wide, flow_narrow)}


def svg(canvas, width, height, layout, uid, spec):
    ident = f"jimdg-{uid}-{layout[0]}"
    return "\n".join([
        f'<svg class="jim-diagram jim-dg-{layout}" viewBox="0 0 {width} {num(height)}" role="img" '
        f'aria-labelledby="{ident}-t {ident}-d" xmlns="http://www.w3.org/2000/svg">',
        f'<title id="{ident}-t">{esc(spec["title"])}</title>',
        f'<desc id="{ident}-d">{esc(spec["desc"])}</desc>',
        *canvas.parts,
        "</svg>",
    ])


def render(spec, uid, metrics=None):
    """Render one spec to the HTML that replaces its fence."""
    if not isinstance(spec, dict):
        raise DiagramError("a diagram spec must be a mapping")
    for key in ("title", "desc"):
        if not spec.get(key):
            raise DiagramError(f"every diagram needs a {key}: the {key} is what a screen reader gets")
    archetype = spec.get("archetype")
    if archetype not in ARCHETYPES:
        raise DiagramError(f'unknown archetype "{archetype}"; use one of {", ".join(sorted(ARCHETYPES))}')
    validate, wide, narrow = ARCHETYPES[archetype]
    validate(spec)
    metrics = metrics or Metrics.load()
    wide_canvas, wide_h = wide(spec, metrics)
    narrow_canvas, narrow_h = narrow(spec, metrics)
    return "\n".join([
        f'<div class="jim-dg jim-dg-{archetype}">',
        svg(wide_canvas, WIDE, wide_h, "wide", uid, spec),
        svg(narrow_canvas, NARROW, narrow_h, "narrow", uid, spec),
        "</div>",
    ])


def page_slug(src_path):
    return re.sub(r"[^a-z0-9]+", "-", str(src_path).lower().removesuffix(".md")).strip("-")


def replace_fences(markdown, src_path, parse=None):
    """Replace every jim-diagram fence in a page with its rendered diagram."""
    if parse is None:
        import yaml
        parse = yaml.safe_load
    count = 0

    def substitute(match):
        nonlocal count
        count += 1
        line = markdown.count("\n", 0, match.start()) + 1
        try:
            return render(parse(match.group(1)), f"{page_slug(src_path)}-{count}")
        except DiagramError as err:
            raise DiagramError(f"{src_path}:{line}: {err}") from None
        except Exception as err:  # a YAML syntax error, say
            raise DiagramError(f"{src_path}:{line}: the diagram spec could not be read: {err}") from None

    return FENCE.sub(substitute, markdown)


def on_page_markdown(markdown, page, config, files, **kwargs):
    from mkdocs.exceptions import PluginError

    try:
        return replace_fences(markdown, page.file.src_uri)
    except DiagramError as err:
        raise PluginError(f"jim-diagram: {err}") from None


if __name__ == "__main__":
    import sys

    specs = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    sys.stdout.write("\n".join(render(s, f"fixture-{i + 1}") for i, s in enumerate(specs)))
