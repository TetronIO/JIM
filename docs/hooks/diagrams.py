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
    "label-on": ("jimdg-dg-label-on", 13, 700, False),
    "sub-on": ("jimdg-dg-sub-on", 12, 400, False),
    "chip": ("jimdg-dg-chip", 12, 400, False),
    "chip-name": ("jimdg-dg-chip-name", 12, 700, False),
    "token": ("jimdg-dg-token", 22, 400, True),
    "token-strong": ("jimdg-dg-token-strong", 22, 700, True),
    "token-narrow": ("jimdg-dg-token-sm", 15, 400, True),
    "token-narrow-strong": ("jimdg-dg-token-sm-strong", 15, 700, True),
}

# A `backticked` span in any label is code: set in Plex Mono, measured as mono.
CODE = re.compile(r"`([^`]+)`")


def spans(text):
    """Split text into (segment, is_code) pairs."""
    out, pos = [], 0
    for match in CODE.finditer(text):
        if match.start() > pos:
            out.append((text[pos:match.start()], False))
        out.append((match.group(1), True))
        pos = match.end()
    if pos < len(text):
        out.append((text[pos:], False))
    return out

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
        total = 0
        for segment, code in spans(str(text)):
            total += self.width(segment, size, 400 if code and not mono else weight, mono or code)
        return total * SLACK


def wrap(text, role, max_width, metrics):
    """Fit text into at most two lines no wider than max_width, or fail."""
    if metrics.measure(text, role) <= max_width:
        return [text]
    words = re.findall(r"`[^`]*`|\S+", text)
    fits = []
    for cut in range(1, len(words)):
        first, second = " ".join(words[:cut]), " ".join(words[cut:])
        widest = max(metrics.measure(first, role), metrics.measure(second, role))
        if widest <= max_width:
            fits.append((widest, cut, [first, second]))
    if fits:
        return min(fits)[2]  # the most even split, so no word is left alone on a line
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
        body = "".join(f'<tspan class="jimdg-dg-code">{esc(seg)}</tspan>' if code else esc(seg)
                       for seg, code in spans(str(value)))
        return f'<text class="{cls}" x="{num(x)}" y="{num(y)}">{body}</text>'

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
            self.lines += [(line, "sub") for line in wrap(spec["sub"], "sub", max_width - 2 * PAD_X, metrics)]
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


# ── Lifecycle ─────────────────────────────────────────────────────────────────

LIFECYCLE_KEYS = {"archetype", "title", "desc", "start", "container", "states", "transitions", "end"}
STATE_KEYS = {"id", "label", "sub"}
TRANSITION_KEYS = {"from", "to", "label", "note"}
LC_END_KEYS = {"from", "label", "note", "state"}


def validate_lifecycle(spec):
    check_keys(spec, LIFECYCLE_KEYS, "the diagram")
    states = spec.get("states") or []
    if not 2 <= len(states) <= 4:
        raise DiagramError("a lifecycle needs two to four states")
    ids = []
    for i, state in enumerate(states):
        check_keys(state, STATE_KEYS, f"state {i + 1}")
        require(state, "id", f"state {i + 1}")
        require(state, "label", f"state {i + 1}")
        if state["id"] in ids:
            raise DiagramError(f'state id "{state["id"]}" is used twice')
        ids.append(state["id"])
    seen = set()
    for i, tr in enumerate(spec.get("transitions") or []):
        where = f"transition {i + 1}"
        check_keys(tr, TRANSITION_KEYS, where)
        for key in ("from", "to", "label"):
            require(tr, key, where)
        if tr["from"] not in ids or tr["to"] not in ids:
            raise DiagramError(f"{where} must name two states by id")
        if abs(ids.index(tr["from"]) - ids.index(tr["to"])) != 1:
            raise DiagramError(f"{where} must join states next to each other; reorder the states")
        if (tr["from"], tr["to"]) in seen:
            raise DiagramError(f'{where}: there is already a transition from "{tr["from"]}" to "{tr["to"]}"')
        seen.add((tr["from"], tr["to"]))
    if "start" in spec:
        check_keys(spec["start"], {"label"}, "the start")
        require(spec["start"], "label", "the start")
    if "container" in spec:
        check_keys(spec["container"], {"label", "note"}, "the container")
        require(spec["container"], "label", "the container")
    if "end" in spec:
        end = spec["end"]
        check_keys(end, LC_END_KEYS, "the end")
        for key in ("from", "label", "state"):
            require(end, key, "the end")
        allowed = ids + (["container"] if "container" in spec else [])
        if end["from"] not in allowed:
            raise DiagramError(f'the end comes from "{end["from"]}", which is not a state'
                               + (' or "container"' if "container" in spec else ""))
        check_keys(end["state"], {"label", "sub"}, "the end state")
        require(end["state"], "label", "the end state")


def pairs(spec, ids):
    """Transitions grouped by the gap they cross: {gap index: {"fwd": tr, "back": tr}}."""
    out = {}
    for tr in spec.get("transitions") or []:
        a, b = ids.index(tr["from"]), ids.index(tr["to"])
        out.setdefault(min(a, b), {})["fwd" if b > a else "back"] = tr
    return out


def lifecycle_wide(spec, metrics):
    canvas = Canvas()
    container = spec.get("container")
    ids = [s["id"] for s in spec["states"]]
    states = [Node(s, 180, 120, metrics, "jimdg-stage") for s in spec["states"]]
    for node in states:
        node.h = max(node.h, 72)
    gaps = pairs(spec, ids)
    widths = []
    for g in range(len(states) - 1):
        need = 60
        for key, tr in gaps.get(g, {}).items():
            need = max(need, metrics.measure(tr["label"], "edge") + 24)
            if tr.get("note"):
                need = max(need, metrics.measure(tr["note"], "note") + 24)
        widths.append(need)
    pad = 22 if container else 0
    inner = sum(n.w for n in states) + sum(widths)
    note_lines = []
    if container:
        inner = max(inner, metrics.measure(container["label"], "title"))
        if container.get("note"):
            note_lines = wrap(container["note"], "tag", inner, metrics)
    start_len = 0
    if "start" in spec:
        start_len = max(60, metrics.measure(spec["start"]["label"], "edge") + 20)
    total = (18 + start_len if start_len else 0) + inner + 2 * pad
    if total > WIDE - 12:
        raise DiagramError(f"the lifecycle is too wide for the wide layout ({total:.0f} of {WIDE - 12} units); "
                           "shorten its labels or use fewer states")
    x0 = (WIDE - total) / 2
    box_x = x0 + (18 + start_len if start_len else 0)
    box_w = inner + 2 * pad
    x = box_x + pad + (inner - sum(n.w for n in states) - sum(widths)) / 2
    for i, node in enumerate(states):
        node.x = x
        x += node.w + (widths[i] if i < len(widths) else 0)
    y = 8
    head = (30 + 18 * len(note_lines) + 18) if container else 0
    row_top = y + head + 6
    row_h = max(n.h for n in states)
    for node in states:
        node.y = row_top + (row_h - node.h) / 2
    cy = row_top + row_h / 2
    box_bottom = row_top + row_h + (18 if container else 0)
    for g, lanes in gaps.items():
        if lanes.get("back", {}).get("note"):
            box_bottom = max(box_bottom, cy + 14 + 22 + (14 if container else 0))
    if container:
        canvas.add(f'<rect class="jimdg-hub" x="{num(box_x)}" y="{num(y)}" width="{num(box_w)}" '
                   f'height="{num(box_bottom - y)}" rx="10"/>')
        canvas.add(canvas.text(box_x + box_w / 2, y + 26, container["label"], "title"))
        for i, line in enumerate(note_lines):
            canvas.add(canvas.text(box_x + box_w / 2, y + 46 + 18 * i, line, "tag"))
    if start_len:
        canvas.add(f'<circle class="jimdg-dg-start" cx="{num(x0 + 9)}" cy="{num(cy)}" r="9"/>')
        target = box_x if container else states[0].x
        canvas.arrow([(x0 + 18, cy), (target, cy)])
        canvas.add(canvas.text((x0 + 18 + target) / 2, cy - 8, spec["start"]["label"], "edge"))
    for g in range(len(states) - 1):
        a, b = states[g], states[g + 1]
        mid = (a.right + b.x) / 2
        lanes = gaps.get(g, {})
        if "fwd" in lanes:
            canvas.arrow([(a.right, cy - 14), (b.x, cy - 14)])
            canvas.add(canvas.text(mid, cy - 22, lanes["fwd"]["label"], "edge"))
        if "back" in lanes:
            canvas.arrow([(b.x, cy + 14), (a.right, cy + 14)])
            canvas.add(canvas.text(mid, cy + 8, lanes["back"]["label"], "edge"))
            if lanes["back"].get("note"):
                canvas.add(canvas.text(mid, cy + 32, lanes["back"]["note"], "note"))
    height = box_bottom + 8
    end = spec.get("end")
    if end:
        term = Node(end["state"], 180, 120, metrics, "jimdg-dg-terminal")
        if end["from"] == "container":
            fx, fy = box_x + box_w / 2, box_bottom
        else:
            src = states[ids.index(end["from"])]
            fx, fy = src.cx, src.bottom
        term.x, term.y = fx - term.w / 2, fy + 44
        canvas.arrow([(fx, fy), (fx, term.y)])
        canvas.add(canvas.text(fx + 10, fy + 26, end["label"], "edge", "start"))
        if end.get("note"):
            if fx - 10 - metrics.measure(end["note"], "note") < 4:
                raise DiagramError("the end's note is too wide to sit beside its arrow; shorten it")
            canvas.add(canvas.text(fx - 10, fy + 26, end["note"], "note", "end"))
        if fx + 10 + metrics.measure(end["label"], "edge") > WIDE - 4:
            raise DiagramError("the end's label is too wide to sit beside its arrow; shorten it")
        term.draw(canvas)
        height = term.bottom + 8
    for node in states:
        node.draw(canvas)
    return canvas, height


def lifecycle_narrow(spec, metrics):
    canvas = Canvas()
    container = spec.get("container")
    ids = [s["id"] for s in spec["states"]]
    states = [Node(s, 220, 160, metrics, "jimdg-stage") for s in spec["states"]]
    gaps = pairs(spec, ids)
    cx = NARROW / 2
    left = cx - max(n.w for n in states) / 2
    for node in states:
        node.x = left
    label_x = left + 50
    layer = Canvas()  # transitions, painted after the container so its fill never covers them
    y = 8
    if "start" in spec:
        # The start dot sits top left with its label beside it; the arrow runs down from it.
        canvas.add(f'<circle class="jimdg-dg-start" cx="24" cy="17" r="9"/>')
        if 42 + metrics.measure(spec["start"]["label"], "edge") > NARROW - 4:
            raise DiagramError("the start's label is too wide for the narrow layout; shorten it")
        canvas.add(canvas.text(42, 21, spec["start"]["label"], "edge", "start"))
        y = 60
    box_y = y
    if container:
        note_lines = wrap(container["note"], "tag", NARROW - 28, metrics) if container.get("note") else []
        if metrics.measure(container["label"], "title") > NARROW - 24:
            raise DiagramError("the container's label is too wide for the narrow layout; shorten it")
        y += 30 + 18 * len(note_lines) + 16
    for i, node in enumerate(states):
        node.y = y
        y = node.bottom
        if i < len(states) - 1:
            rows = []  # (text, role, direction marker or None)
            lanes = gaps.get(i, {})
            if "fwd" in lanes:
                rows.append((lanes["fwd"]["label"], "edge", 1))
            if "back" in lanes:
                rows.append((lanes["back"]["label"], "edge", -1))
                if lanes["back"].get("note"):
                    rows.append((lanes["back"]["note"], "note", None))
            rows = [(line, role, mark if n == 0 else None) for text, role, mark in rows
                    for n, line in enumerate(wrap(text, role, NARROW - 8 - label_x - 14, metrics))]
            gap = max(36, 18 * len(rows) + 18)
            nxt_top = y + gap
            if "fwd" in lanes:
                layer.arrow([(left + 18, y), (left + 18, nxt_top)])
            if "back" in lanes:
                layer.arrow([(left + 34, nxt_top), (left + 34, y)])
            for r, (text, role, mark) in enumerate(rows):
                ty = y + 22 + 18 * r
                if mark:  # a small arrowhead saying which arrow the label belongs to
                    tip, base = (ty - 1, ty - 9) if mark > 0 else (ty - 9, ty - 1)
                    layer.add(f'<polygon class="jimdg-flow-arrow" points="{num(label_x + 4)},{num(tip)} '
                              f'{num(label_x)},{num(base)} {num(label_x + 8)},{num(base)}"/>')
                layer.add(layer.text(label_x + 14, ty, text, role, "start"))
            y = nxt_top
    box_bottom = y + (16 if container else 0)
    if container:
        canvas.add(f'<rect class="jimdg-hub" x="6" y="{num(box_y)}" width="{NARROW - 12}" '
                   f'height="{num(box_bottom - box_y)}" rx="10"/>')
        canvas.add(canvas.text(cx, box_y + 26, container["label"], "title"))
        for i, line in enumerate(note_lines):
            canvas.add(canvas.text(cx, box_y + 46 + 18 * i, line, "tag"))
    canvas.parts.extend(layer.parts)
    if "start" in spec:
        canvas.arrow([(24, 26), (24, box_y if container else states[0].y)])
    height = box_bottom + 8
    end = spec.get("end")
    if end:
        term = Node(end["state"], 220, 160, metrics, "jimdg-dg-terminal")
        if end["from"] == "container":
            fy = box_bottom
        else:
            fy = states[ids.index(end["from"])].bottom
        term.x, term.y = cx - term.w / 2, fy + 34
        canvas.arrow([(cx, fy), (cx, term.y)])
        term.draw(canvas)
        y = term.bottom + 20
        for text, role in [(end["label"], "edge")] + ([(end["note"], "note")] if end.get("note") else []):
            for line in wrap(text, role, NARROW - 16, metrics):
                canvas.add(canvas.text(cx, y, line, role))
                y += 18
        height = y - 6
    for node in states:
        node.draw(canvas)
    return canvas, height


# ── Anatomy ───────────────────────────────────────────────────────────────────

ANATOMY_KEYS = {"archetype", "title", "desc", "subject", "derived", "properties"}


def validate_anatomy(spec):
    check_keys(spec, ANATOMY_KEYS, "the diagram")
    subject = spec.get("subject")
    if not isinstance(subject, dict):
        raise DiagramError("an anatomy needs a subject")
    kind = subject.get("kind")
    part_ids = []
    if kind == "value":
        check_keys(subject, {"kind", "parts"}, "the subject")
        parts = subject.get("parts") or []
        if not 1 <= len(parts) <= 4:
            raise DiagramError("a value subject needs one to four parts")
        for i, part in enumerate(parts):
            check_keys(part, {"id", "text", "strong", "callout"}, f"part {i + 1}")
            require(part, "text", f"part {i + 1}")
            if part.get("id"):
                part_ids.append(part["id"])
    elif kind == "object":
        check_keys(subject, {"kind", "label", "parts"}, "the subject")
        require(subject, "label", "the subject")
        parts = subject.get("parts") or []
        if not 2 <= len(parts) <= 7:
            raise DiagramError("an object subject needs two to seven parts")
        for i, part in enumerate(parts):
            check_keys(part, {"label", "detail"}, f"part {i + 1}")
            require(part, "label", f"part {i + 1}")
    else:
        raise DiagramError(f'the subject kind is "{kind}"; use value or object')
    if "derived" in spec:
        derived = spec["derived"]
        check_keys(derived, {"from", "label", "box", "note"}, "derived")
        for key in ("from", "box"):
            require(derived, key, "derived")
        if kind != "value" or derived["from"] not in part_ids:
            raise DiagramError(f'derived comes from "{derived["from"]}", which is not the id of a part of a value')
    props = spec.get("properties") or []
    if len(props) > 4:
        raise DiagramError("an anatomy shows at most four properties")
    for i, prop in enumerate(props):
        check_keys(prop, {"name", "value"}, f"property {i + 1}")
        require(prop, "name", f"property {i + 1}")


def draw_chips(canvas, props, metrics, width, top, per_row):
    """Property chips in rows of per_row, centred; returns the bottom."""
    if not props:
        return top
    gap = 12
    longest = max(metrics.measure(p["name"], "chip-name") + metrics.measure("  " + (p.get("value") or ""), "chip")
                  for p in props)
    # As many to a row as the longest chip allows, up to per_row.
    while per_row > 1 and (width - 16 - gap * (per_row - 1)) / per_row < longest + 16:
        per_row -= 1
    chip_w = (width - 16 - gap * (per_row - 1)) / per_row
    rows = [props[i:i + per_row] for i in range(0, len(props), per_row)]
    y = top
    for row in rows:
        x = (width - (len(row) * chip_w + gap * (len(row) - 1))) / 2
        for prop in row:
            text_w = metrics.measure(prop["name"], "chip-name") + metrics.measure("  " + (prop.get("value") or ""), "chip")
            if text_w > chip_w - 16:
                raise DiagramError(f'the property "{prop["name"]}: {prop.get("value", "")}" is too long for its chip; shorten it')
            body = f'<tspan class="jimdg-dg-chip-name">{esc(prop["name"])}</tspan>'
            if prop.get("value"):
                body += "  " + esc(prop["value"])
            canvas.add('<g class="jimdg-dg-node">'
                       f'<rect class="jimdg-chip" x="{num(x)}" y="{num(y)}" width="{num(chip_w)}" height="34" rx="17"/>'
                       f'<text class="jimdg-dg-chip" x="{num(x + chip_w / 2)}" y="{num(y + 21.5)}">{body}</text></g>')
            x += chip_w + gap
        y += 34 + 10
    return y - 10


def value_layout(spec, metrics, width, narrow):
    canvas = Canvas()
    parts = spec["subject"]["parts"]
    role, strong = ("token-narrow", "token-narrow-strong") if narrow else ("token", "token-strong")
    widths = [metrics.measure(p["text"], strong if p.get("strong") else role) for p in parts]
    text_w = sum(widths)
    pill_w = text_w + (40 if narrow else 64)
    pill_h = 44 if narrow else 56
    if pill_w > width - 12:
        raise DiagramError(f"the value is too long for the {'narrow' if narrow else 'wide'} layout; shorten it")
    callouts = [(i, p["callout"]) for i, p in enumerate(parts) if p.get("callout")]
    pill_x = (width - pill_w) / 2
    top = 8 if narrow else (48 if callouts else 8)
    starts, x = [], pill_x + (pill_w - text_w) / 2
    for w in widths:
        starts.append(x)
        x += w
    centres = [s + w / 2 for s, w in zip(starts, widths)]
    tspans = "".join(f'<tspan class="{STYLES[strong if p.get("strong") else role][0]}">{esc(p["text"])}</tspan>'
                     for p in parts)
    size = STYLES[role][1]
    canvas.add('<g class="jimdg-dg-node">'
               f'<rect class="jimdg-dg-token-pill" x="{num(pill_x)}" y="{num(top)}" width="{num(pill_w)}" '
               f'height="{pill_h}" rx="{pill_h / 2:.0f}"/>'
               f'<text class="{STYLES[role][0]} jimdg-anchor-start" x="{num(starts[0])}" '
               f'y="{num(top + pill_h / 2 + size * 0.35)}">{tspans}</text></g>')
    y = top + pill_h
    if callouts and not narrow:
        boxes = []
        for i, text in callouts:
            w = metrics.measure(text, "label")
            left = max(4, min(width - 4 - w, centres[i] - w / 2))
            if boxes and left < boxes[-1][1] + 16:
                left = boxes[-1][1] + 16  # nudge right of the previous callout
            if not left + 8 <= centres[i] <= left + w - 8 or left + w > width - 4:
                raise DiagramError("two callouts are too close to sit over their parts; shorten them")
            boxes.append((left, left + w))
            canvas.add(f'<line class="jimdg-dg-leader" x1="{num(centres[i])}" y1="{num(top)}" '
                       f'x2="{num(centres[i])}" y2="{num(top - 16)}"/>')
            canvas.add(canvas.text(left + w / 2, top - 24, text, "label"))
    elif callouts:
        for n, (i, text) in enumerate(callouts):
            canvas.add(canvas.badge(centres[i], top, n + 1))
        y += 14
        for n, (i, text) in enumerate(callouts):
            for line in wrap(text, "label", width - 48, metrics):
                y += 18
                canvas.add(canvas.text(34, y, line, "label", "start"))
            canvas.add(canvas.badge(18, y - 4, n + 1))
            y += 4
    derived = spec.get("derived")
    if derived:
        i = next(k for k, p in enumerate(parts) if p.get("id") == derived["from"])
        ax = centres[i] if not narrow else width / 2
        box = Node({"label": derived["box"]}, width - 40 if narrow else 280, 160, metrics, "jimdg-core")
        box.lines = [(text, "label-on") for text, _ in box.lines]
        start_y = y
        box.x, box.y = ax - box.w / 2, start_y + 34
        box.x = max(6, min(width - 6 - box.w, box.x))
        canvas.arrow([(ax, start_y), (ax, box.y)])
        if derived.get("label"):
            canvas.add(canvas.text(ax + 10, start_y + 22, derived["label"], "edge", "start"))
        box.draw(canvas)
        y = box.bottom
        if derived.get("note"):
            for line in wrap(derived["note"], "note", width - 16, metrics):
                y += 18
                canvas.add(canvas.text(box.cx, y, line, "note"))
    y = draw_chips(canvas, spec.get("properties") or [], metrics, width, y + 20, 2 if narrow else 4)
    return canvas, y + 8


def object_layout(spec, metrics, width, narrow):
    canvas = Canvas()
    subject = spec["subject"]
    rows = subject["parts"]
    row_h = 40 if not narrow or not any(r.get("detail") for r in rows) else 52
    if narrow:
        card_w = width - 16
    else:
        need = max(metrics.measure(r["label"], "label") + metrics.measure(r.get("detail") or "", "sub") + 48
                   for r in rows)
        card_w = min(width - 16, max(360, need, metrics.measure(subject["label"], "title") + 40))
    card_x = (width - card_w) / 2
    top = 8
    head = 40
    card_h = head + row_h * len(rows)
    canvas.add(f'<rect class="jimdg-dg-card" x="{num(card_x)}" y="{top}" width="{num(card_w)}" height="{num(card_h)}" rx="10"/>')
    r10, right = 10, card_x + card_w
    canvas.add(f'<path class="jimdg-layer" d="M{num(card_x)},{top + head} V{top + r10} Q{num(card_x)},{top} {num(card_x + r10)},{top} '
               f'H{num(right - r10)} Q{num(right)},{top} {num(right)},{top + r10} V{top + head} Z"/>')
    canvas.add(canvas.text(card_x + card_w / 2, top + 26, subject["label"], "title"))
    y = top + head
    for r in rows:
        parts = [f'<rect class="jimdg-dg-row" x="{num(card_x)}" y="{num(y)}" width="{num(card_w)}" height="{row_h}"/>']
        if r is not rows[-1]:
            parts.append(f'<line class="jimdg-card-divider" x1="{num(card_x)}" y1="{num(y + row_h)}" '
                         f'x2="{num(card_x + card_w)}" y2="{num(y + row_h)}"/>')
        if narrow and r.get("detail"):
            for text, role in ((r["label"], "label"), (r["detail"], "sub")):
                if metrics.measure(text, role) > card_w - 32:
                    raise DiagramError(f'"{text}" is too wide for the narrow layout; shorten it')
            parts.append(canvas.text(card_x + 16, y + 21, r["label"], "label", "start"))
            parts.append(canvas.text(card_x + 16, y + 39, r["detail"], "sub", "start"))
        else:
            if metrics.measure(r["label"], "label") + metrics.measure(r.get("detail") or "", "sub") + 48 > card_w:
                raise DiagramError(f'the part "{r["label"]}" and its detail are too wide; shorten them')
            parts.append(canvas.text(card_x + 16, y + row_h / 2 + 4.5, r["label"], "label", "start"))
            if r.get("detail"):
                parts.append(canvas.text(card_x + card_w - 16, y + row_h / 2 + 4.5, r["detail"], "sub", "end"))
        canvas.add('<g class="jimdg-dg-node">' + "".join(parts) + "</g>")
        y += row_h
    y = draw_chips(canvas, spec.get("properties") or [], metrics, width, top + card_h + 20, 2 if narrow else 4)
    return canvas, y + 8


def anatomy_wide(spec, metrics):
    layout = value_layout if spec["subject"]["kind"] == "value" else object_layout
    return layout(spec, metrics, WIDE, False)


def anatomy_narrow(spec, metrics):
    layout = value_layout if spec["subject"]["kind"] == "value" else object_layout
    return layout(spec, metrics, NARROW, True)


# ── Decision ──────────────────────────────────────────────────────────────────

DECISION_KEYS = {"archetype", "title", "desc", "start", "questions", "outcomes", "answers"}


def validate_decision(spec):
    check_keys(spec, DECISION_KEYS, "the diagram")
    questions = spec.get("questions") or []
    outcomes = spec.get("outcomes") or []
    if not 1 <= len(questions) <= 4:
        raise DiagramError("a decision needs one to four questions")
    if not 2 <= len(outcomes) <= 6:
        raise DiagramError("a decision needs two to six outcomes")
    q_ids, o_ids = [], []
    for kind, items, keys, ids in (("question", questions, STATE_KEYS, q_ids),
                                   ("outcome", outcomes, STATE_KEYS | {"good"}, o_ids)):
        for i, item in enumerate(items):
            check_keys(item, keys, f"{kind} {i + 1}")
            require(item, "id", f"{kind} {i + 1}")
            require(item, "label", f"{kind} {i + 1}")
            if item["id"] in q_ids + o_ids:
                raise DiagramError(f'the id "{item["id"]}" is used twice')
            ids.append(item["id"])
    if "start" in spec:
        check_keys(spec["start"], {"label"}, "the start")
        require(spec["start"], "label", "the start")
    reached = set()
    downs, loops = set(), set()
    for i, ans in enumerate(spec.get("answers") or []):
        where = f"answer {i + 1}"
        check_keys(ans, {"from", "to", "label"}, where)
        for key in ("from", "to", "label"):
            require(ans, key, where)
        if ans["from"] not in q_ids:
            raise DiagramError(f'{where} comes from "{ans["from"]}", which is not a question')
        if ans["to"] not in q_ids + o_ids:
            raise DiagramError(f'{where} goes to "{ans["to"]}", which is neither a question nor an outcome')
        reached.add(ans["to"])
        if ans["to"] in q_ids:
            a, b = q_ids.index(ans["from"]), q_ids.index(ans["to"])
            if b > a:
                if b != a + 1:
                    raise DiagramError(f"{where} must go to the next question, or back to an earlier one")
                if a in downs:
                    raise DiagramError(f'question "{ans["from"]}" already has an answer to the next question')
                downs.add(a)
            elif b == a:
                raise DiagramError(f"{where} goes from a question to itself")
            else:
                if a in loops:
                    raise DiagramError(f'question "{ans["from"]}" already loops back')
                loops.add(a)
    for oid in o_ids:
        if oid not in reached:
            raise DiagramError(f'no answer reaches the outcome "{oid}"')
    for i, qid in enumerate(q_ids[1:], 1):
        if i - 1 not in downs:
            raise DiagramError(f'no answer leads to the question "{qid}"')


def outcome_node(spec, max_w, min_w, metrics):
    if spec.get("good"):
        node = Node(spec, max_w, min_w, metrics, "jimdg-core")
        node.lines = [(text, {"label": "label-on", "sub": "sub-on"}.get(role, role)) for text, role in node.lines]
        return node
    return Node(spec, max_w, min_w, metrics, "jimdg-stage")


def decision_parts(spec):
    q_ids = [q["id"] for q in spec["questions"]]
    o_ids = [o["id"] for o in spec["outcomes"]]
    to_out = {qid: [a for a in spec["answers"] if a["from"] == qid and a["to"] in o_ids] for qid in q_ids}
    down = {a["from"]: a for a in spec["answers"]
            if a["to"] in q_ids and q_ids.index(a["to"]) == q_ids.index(a["from"]) + 1}
    loop = {a["from"]: a for a in spec["answers"]
            if a["to"] in q_ids and q_ids.index(a["to"]) < q_ids.index(a["from"])}
    return q_ids, o_ids, to_out, down, loop


def decision_wide(spec, metrics):
    canvas = Canvas()
    q_ids, o_ids, to_out, down, loop = decision_parts(spec)
    qs = {q["id"]: Node(q, 240, 120, metrics, "jimdg-dg-question") for q in spec["questions"]}
    os_ = {o["id"]: outcome_node(o, 236, 150, metrics) for o in spec["outcomes"]}
    start = Node(spec["start"], 124, 96, metrics, "jimdg-system") if "start" in spec else None
    qw = max(n.w for n in qs.values())
    ow = max(n.w for n in os_.values())
    for n in qs.values():
        n.w = qw
    for n in os_.values():
        n.w = ow
    label_w = max([metrics.measure(a["label"], "edge") for v in to_out.values() for a in v] or [0])
    loop_room = 0
    if loop:
        loop_room = 24 + max(metrics.measure(a["label"], "edge") for a in loop.values())
    lead = max(start.w + 30 if start else 0, loop_room)
    trunk_gap, out_gap = 24, max(56, label_w + 20)
    total = lead + qw + trunk_gap + out_gap + ow
    if total > WIDE - 12:
        raise DiagramError(f"the decision is too wide for the wide layout ({total:.0f} of {WIDE - 12} units); "
                           "shorten its labels")
    x0 = (WIDE - total) / 2
    qx = x0 + lead
    bx = qx + qw + trunk_gap
    ox = bx + out_gap
    y = 8
    prev_q, group_bottom = None, 8
    for qid in q_ids:
        q = qs[qid]
        q.x = qx
        outs = [os_[a["to"]] for a in to_out[qid]]
        group_h = sum(n.h for n in outs) + 14 * max(0, len(outs) - 1)
        top = max(y, (prev_q.bottom + 48) if prev_q else y)
        top = max(top, group_bottom + 14 + group_h / 2 - q.h / 2 if outs else top)
        q.y = top
        gy = q.cy - group_h / 2
        if outs and gy < group_bottom + (14 if prev_q else 0):
            shift = group_bottom + 14 - gy
            q.y += shift
            gy += shift
        for n in outs:
            n.x, n.y = ox, gy
            gy += n.h + 14
        if outs:
            group_bottom = gy - 14
        prev_q = q
    if start:
        first = qs[q_ids[0]]
        start.x, start.y = x0, first.y + first.h * 0.4 - start.h / 2
        start.y = max(8, start.y)
    shift = 8 - min([n.y for n in list(qs.values()) + list(os_.values())] + ([start.y] if start else []))
    for n in list(qs.values()) + list(os_.values()) + ([start] if start else []):
        n.y += shift
    for qid in q_ids:
        q = qs[qid]
        for a in to_out[qid]:
            o = os_[a["to"]]
            canvas.arrow([(q.right, q.cy), (bx, q.cy), (bx, o.cy), (o.x, o.cy)])
            canvas.add(canvas.text((bx + o.x) / 2, o.cy - 8, a["label"], "edge"))
        if qid in down:
            nxt = qs[down[qid]["to"]]
            canvas.arrow([(q.cx, q.bottom), (q.cx, nxt.y)])
            canvas.add(canvas.text(q.cx + 8, (q.bottom + nxt.y) / 2 + 4, down[qid]["label"], "edge", "start"))
        if qid in loop:
            back = qs[loop[qid]["to"]]
            lx = qx - 16
            ty = back.y + back.h * 0.78
            canvas.arrow([(q.x, q.cy), (lx, q.cy), (lx, ty), (back.x, ty)])
            ly = (q.cy + ty) / 2 + 4
            if start and start.y - 4 < ly < start.bottom + 14:
                raise DiagramError("the loop's label would sit on the start box; put more between the questions")
            canvas.add(canvas.text(lx - 8, ly, loop[qid]["label"], "edge", "end"))
    if start:
        first = qs[q_ids[0]]
        canvas.arrow([(start.right, start.cy), (first.x, start.cy)])
        start.draw(canvas)
    for n in list(qs.values()) + list(os_.values()):
        n.draw(canvas)
    height = max(n.bottom for n in list(qs.values()) + list(os_.values()) + ([start] if start else [])) + 8
    return canvas, height


def decision_narrow(spec, metrics):
    canvas = Canvas()
    q_ids, o_ids, to_out, down, loop = decision_parts(spec)
    q_labels = {q["id"]: q["label"] for q in spec["questions"]}
    trunk, ox = 26, 46
    y = 8
    nodes = []
    if "start" in spec:
        start = Node(spec["start"], NARROW - 24, NARROW - 24, metrics, "jimdg-system")
        start.x, start.y = 12, y
        nodes.append(start)
        canvas.arrow([(trunk, start.bottom), (trunk, start.bottom + 26)])
        y = start.bottom + 26
    for qid in q_ids:
        q = Node(next(s for s in spec["questions"] if s["id"] == qid), NARROW - 24, NARROW - 24, metrics,
                 "jimdg-dg-question")
        q.x, q.y = 12, y
        nodes.append(q)
        y = q.bottom
        if qid in loop:
            text = f'{loop[qid]["label"]}, back to “{q_labels[loop[qid]["to"]]}”'
            for n, line in enumerate(wrap(text, "edge", NARROW - 48, metrics)):
                y += 20
                if n == 0:
                    canvas.add(f'<polygon class="jimdg-flow-arrow" points="{num(trunk)},{num(y - 10)} '
                               f'{num(trunk - 4)},{num(y - 2)} {num(trunk + 4)},{num(y - 2)}"/>')
                canvas.add(canvas.text(trunk + 14, y, line, "edge", "start"))
        last_cy = None
        branch_top = y
        for a in to_out[qid]:
            o = outcome_node(next(s for s in spec["outcomes"] if s["id"] == a["to"]), NARROW - ox - 12,
                             NARROW - ox - 12, metrics)
            lines = wrap(a["label"], "edge", NARROW - ox - 12, metrics)
            for line in lines:
                y += 18
                canvas.add(canvas.text(ox, y, line, "edge", "start"))
            o.x, o.y = ox, y + 8
            nodes.append(o)
            canvas.arrow([(trunk, o.cy), (o.x, o.cy)])
            last_cy = o.cy
            y = o.bottom
        if last_cy is not None:
            canvas.add(f'<line class="jimdg-flow" x1="{trunk}" y1="{num(branch_top)}" x2="{trunk}" y2="{num(last_cy)}"/>')
        if qid in down:
            lines = wrap(down[qid]["label"], "edge", NARROW - 48, metrics)
            seg_top = y
            for line in lines:
                y += 18
                canvas.add(canvas.text(trunk + 14, y, line, "edge", "start"))
            y += 14
            canvas.arrow([(trunk, seg_top if last_cy is None else last_cy), (trunk, y)])
    for n in nodes:
        n.draw(canvas)
    return canvas, y + 8


# ── Comparison ────────────────────────────────────────────────────────────────

COMPARISON_KEYS = {"archetype", "title", "desc", "columns", "rows", "footer"}


def validate_comparison(spec):
    check_keys(spec, COMPARISON_KEYS, "the diagram")
    cols = spec.get("columns") or []
    rows = spec.get("rows") or []
    if not 2 <= len(cols) <= 3:
        raise DiagramError("a comparison needs two or three columns")
    if not 1 <= len(rows) <= 4:
        raise DiagramError("a comparison needs one to four rows")
    for i, col in enumerate(cols):
        check_keys(col, {"label", "sub"}, f"column {i + 1}")
        require(col, "label", f"column {i + 1}")
    for i, row in enumerate(rows):
        check_keys(row, {"label", "cells"}, f"row {i + 1}")
        require(row, "label", f"row {i + 1}")
        cells = row.get("cells") or []
        if len(cells) != len(cols):
            raise DiagramError(f"row {i + 1} needs one cell per column ({len(cols)})")
        for j, cell in enumerate(cells):
            check_keys(cell, {"text", "sub", "meter"}, f"row {i + 1}, cell {j + 1}")
            require(cell, "text", f"row {i + 1}, cell {j + 1}")
            meter = cell.get("meter")
            if meter is not None and (not isinstance(meter, int) or not 0 <= meter <= 12):
                raise DiagramError(f"row {i + 1}, cell {j + 1}: a meter is a whole number from 0 to 12")


def meter(canvas_parts, cx, y, filled, spacing=14):
    on = set(range(12)) if filled >= 12 else {round((i + 0.5) * 12 / filled - 0.5) for i in range(filled)} if filled else set()
    left = cx - spacing * 5.5
    for i in range(12):
        cls = "jimdg-dg-dot-on" if i in on else "jimdg-dg-dot"
        canvas_parts.append(f'<circle class="{cls}" cx="{num(left + i * spacing)}" cy="{num(y)}" r="4.5"/>')


def cell_lines(cell, width, metrics):
    lines = [(line, "label") for line in wrap(cell["text"], "label", width, metrics)]
    if cell.get("sub"):
        lines += [(line, "sub") for line in wrap(cell["sub"], "sub", width, metrics)]
    return lines


def comparison_wide(spec, metrics):
    canvas = Canvas()
    cols, rows = spec["columns"], spec["rows"]
    head_w = max(110, min(180, max(metrics.measure(r["label"], "label") for r in rows) + 32))
    gap = 14
    cell_w = (WIDE - 16 - head_w - gap * len(cols)) / len(cols)
    x0 = 8 + head_w + gap
    y = 8
    for j, col in enumerate(cols):
        cx = x0 + j * (cell_w + gap) + cell_w / 2
        canvas.add(canvas.text(cx, y + 16, col["label"], "title"))
        if col.get("sub"):
            if metrics.measure(col["sub"], "note") > cell_w:
                raise DiagramError(f'the column caption "{col["sub"]}" is wider than its column; shorten it')
            canvas.add(canvas.text(cx, y + 34, col["sub"], "note"))
    y += 50 if any(c.get("sub") for c in cols) else 30
    for row in rows:
        contents = [cell_lines(c, cell_w - 24, metrics) for c in row["cells"]]
        has_meter = any(c.get("meter") is not None for c in row["cells"])
        h = 24 + LINE * max(len(c) for c in contents) + (26 if has_meter else 0) + 4
        label = Node({"label": row["label"]}, head_w, head_w, metrics, "jimdg-system")
        label.x, label.y, label.h = 8, y, max(h, label.h)
        h = label.h
        label.draw(canvas)
        for j, (cell, lines) in enumerate(zip(row["cells"], contents)):
            x = x0 + j * (cell_w + gap)
            parts = [f'<rect class="jimdg-stage" x="{num(x)}" y="{num(y)}" width="{num(cell_w)}" height="{num(h)}" rx="8"/>']
            for k, (text, role) in enumerate(lines):
                parts.append(canvas.text(x + cell_w / 2, y + 22 + LINE * k, text, role))
            if cell.get("meter") is not None:
                meter(parts, x + cell_w / 2, y + h - 18, cell["meter"], 14 if cell_w >= 200 else 12)
            canvas.add('<g class="jimdg-dg-node">' + "".join(parts) + "</g>")
        y += h + 12
    if spec.get("footer"):
        lines = wrap(spec["footer"], "note", WIDE - 16, metrics)
        canvas.add(f'<line class="jimdg-dg-leader" x1="8" y1="{num(y)}" x2="{WIDE - 8}" y2="{num(y)}"/>')
        for line in lines:
            y += 22
            canvas.add(canvas.text(WIDE / 2, y, line, "note"))
        y += 4
    return canvas, y + 4


def comparison_narrow(spec, metrics):
    canvas = Canvas()
    cols, rows = spec["columns"], spec["rows"]
    width = NARROW - 16
    y = 8
    for j, col in enumerate(cols):
        canvas.add(canvas.text(NARROW / 2, y + 16, col["label"], "title"))
        y += 22
        if col.get("sub"):
            for line in wrap(col["sub"], "note", width, metrics):
                y += 18
                canvas.add(canvas.text(NARROW / 2, y, line, "note"))
        y += 12
        for row in rows:
            cell = row["cells"][j]
            lines = [(row["label"], "edge")] + cell_lines(cell, width - 24, metrics)
            h = 24 + LINE * len(lines) + (26 if cell.get("meter") is not None else 0) + 4
            parts = [f'<rect class="jimdg-stage" x="8" y="{num(y)}" width="{width}" height="{num(h)}" rx="8"/>']
            for k, (text, role) in enumerate(lines):
                parts.append(canvas.text(NARROW / 2, y + 22 + LINE * k, text, role))
            if cell.get("meter") is not None:
                meter(parts, NARROW / 2, y + h - 18, cell["meter"])
            canvas.add('<g class="jimdg-dg-node">' + "".join(parts) + "</g>")
            y += h + 10
        y += 10
    if spec.get("footer"):
        canvas.add(f'<line class="jimdg-dg-leader" x1="8" y1="{num(y)}" x2="{NARROW - 8}" y2="{num(y)}"/>')
        for line in wrap(spec["footer"], "note", width, metrics):
            y += 20
            canvas.add(canvas.text(NARROW / 2, y, line, "note"))
        y += 4
    return canvas, y + 4


# ── Relationship map ──────────────────────────────────────────────────────────

MAP_KEYS = {"archetype", "title", "desc", "nodes", "edges"}
LINK = re.compile(r"^(#[\w-]+|[\w./-]+?(#[\w-]+)?)$")


def validate_map(spec):
    check_keys(spec, MAP_KEYS, "the diagram")
    nodes = spec.get("nodes") or []
    if not 3 <= len(nodes) <= 14:
        raise DiagramError("a map needs three to fourteen nodes")
    ids, cells = [], {}
    for i, node in enumerate(nodes):
        where = f"node {i + 1}"
        check_keys(node, {"id", "label", "link", "row", "col"}, where)
        for key in ("id", "label"):
            require(node, key, where)
        for key, limit in (("row", 4), ("col", 4)):
            if not isinstance(node.get(key), int) or not 0 <= node[key] < limit:
                raise DiagramError(f"{where} needs a {key} from 0 to {limit - 1}")
        if node["id"] in ids:
            raise DiagramError(f'node id "{node["id"]}" is used twice')
        if (node["row"], node["col"]) in cells:
            raise DiagramError(f'"{node["id"]}" and "{cells[(node["row"], node["col"])]}" are in the same place')
        if node.get("link") and (not LINK.match(node["link"]) or node["link"].startswith("//")):
            raise DiagramError(f'{where}: a link is a page anchor (#term) or a relative path, not "{node["link"]}"')
        ids.append(node["id"])
        cells[(node["row"], node["col"])] = node["id"]
    by_id = {n["id"]: n for n in nodes}
    for i, edge in enumerate(spec.get("edges") or []):
        where = f"edge {i + 1}"
        check_keys(edge, {"from", "to", "label"}, where)
        for key in ("from", "to"):
            require(edge, key, where)
            if edge[key] not in by_id:
                raise DiagramError(f'{where} names "{edge[key]}", which is not a node')
        a, b = by_id[edge["from"]], by_id[edge["to"]]
        if a["row"] != b["row"] and a["col"] != b["col"]:
            raise DiagramError(f"{where} must run along a row or a column")
        if a["row"] == b["row"]:
            lo, hi = sorted((a["col"], b["col"]))
            between = [c for c in range(lo + 1, hi) if (a["row"], c) in cells]
        else:
            lo, hi = sorted((a["row"], b["row"]))
            between = [r for r in range(lo + 1, hi) if (r, a["col"]) in cells]
        if between:
            raise DiagramError(f"{where} would run through another node")


def map_node(canvas, node, spec_node):
    if spec_node.get("link"):
        canvas.add(f'<a href="{html.escape(spec_node["link"])}" class="jimdg-dg-link">')
    node.draw(canvas)
    if spec_node.get("link"):
        canvas.add("</a>")


def map_wide(spec, metrics):
    canvas = Canvas()
    specs = {n["id"]: n for n in spec["nodes"]}
    ncols = max(n["col"] for n in spec["nodes"]) + 1
    nrows = max(n["row"] for n in spec["nodes"]) + 1
    W = 128
    nodes = {k: Node(v, W, W, metrics, "jimdg-stage") for k, v in specs.items()}
    H = max(max(n.h for n in nodes.values()), 50)
    gap_x = min(140, (WIDE - 16 - ncols * W) / max(1, ncols - 1))
    total = ncols * W + (ncols - 1) * gap_x
    x0 = (WIDE - total) / 2
    gap_y = 56
    for k, n in nodes.items():
        n.w, n.h = W, H
        n.x = x0 + specs[k]["col"] * (W + gap_x)
        n.y = 8 + specs[k]["row"] * (H + gap_y)
    for edge in spec.get("edges") or []:
        a, b = nodes[edge["from"]], nodes[edge["to"]]
        label = edge.get("label")
        if specs[edge["from"]]["row"] == specs[edge["to"]]["row"]:
            if a.x < b.x:
                canvas.arrow([(a.right, a.cy), (b.x, b.cy)])
                left, right = a.right, b.x
            else:
                canvas.arrow([(a.x, a.cy), (b.right, b.cy)])
                left, right = b.right, a.x
            if label:
                if metrics.measure(label, "edge") + 8 > right - left:
                    raise DiagramError(f'the edge label "{label}" is wider than the gap it labels; shorten it')
                canvas.add(canvas.text((left + right) / 2, a.cy - 8, label, "edge"))
        else:
            if a.y < b.y:
                canvas.arrow([(a.cx, a.bottom), (b.cx, b.y)])
                top, bottom = a.bottom, b.y
            else:
                canvas.arrow([(a.cx, a.y), (b.cx, b.bottom)])
                top, bottom = b.bottom, a.y
            if label:
                if metrics.measure(label, "edge") > W / 2 + gap_x - 16:
                    raise DiagramError(f'the edge label "{label}" is too wide to sit beside its arrow; shorten it')
                canvas.add(canvas.text(a.cx + 8, (top + bottom) / 2 + 4, label, "edge", "start"))
    for k, n in nodes.items():
        map_node(canvas, n, specs[k])
    return canvas, 8 + nrows * H + (nrows - 1) * gap_y + 8


def map_narrow(spec, metrics):
    canvas = Canvas()
    specs = {n["id"]: n for n in spec["nodes"]}
    order = sorted(spec["nodes"], key=lambda n: (n["row"], n["col"]))
    edges = spec.get("edges") or []
    y = 8
    for sn in order:
        node = Node(sn, NARROW - 16, NARROW - 16, metrics, "jimdg-stage")
        node.x, node.y = 8, y
        map_node(canvas, node, sn)
        y = node.bottom
        for edge in [e for e in edges if e["from"] == sn["id"]]:
            text = f'{edge.get("label") or "to"} {specs[edge["to"]]["label"]}'
            for n, line in enumerate(wrap(text, "edge", NARROW - 52, metrics)):
                y += 20
                if n == 0:
                    canvas.add(f'<polygon class="jimdg-flow-arrow" points="{num(30)},{num(y - 5)} '
                               f'{num(22)},{num(y - 9.5)} {num(22)},{num(y - 0.5)}"/>')
                canvas.add(canvas.text(38, y, line, "edge", "start"))
        y += 12
    return canvas, y


# ── Rendering ─────────────────────────────────────────────────────────────────

ARCHETYPES = {
    "flow": (validate_flow, flow_wide, flow_narrow),
    "lifecycle": (validate_lifecycle, lifecycle_wide, lifecycle_narrow),
    "anatomy": (validate_anatomy, anatomy_wide, anatomy_narrow),
    "decision": (validate_decision, decision_wide, decision_narrow),
    "comparison": (validate_comparison, comparison_wide, comparison_narrow),
    "map": (validate_map, map_wide, map_narrow),
}


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
