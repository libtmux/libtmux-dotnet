"""Edit JSONC without reserializing it.

A config with comments and trailing commas belongs to the person who wrote
it, and no PyPI library round-trips that format without losing the parts
JSON has no room for. So edits are applied as text splices located by an
offset-preserving scanner: everything the edit did not name comes out byte
for byte as it went in.

This knows nothing about MCP or about any particular CLI. It is here rather
than inside the swap because it is a format, not a policy.
"""

from __future__ import annotations

import json
import typing as t

_JSON_WS = " \t\n\r"

#: Longest inline rendering of a scalar list before it is broken across
#: lines. A swapped ``command`` array is the common case and reads
#: better on one line, which is how these configs are written by hand.
_INLINE_WIDTH = 88


def blank_comments(text: str) -> str:
    """Replace comment bytes with spaces, preserving every offset.

    Scanning rather than matching a regex is the whole point: ``//``
    inside a URL and ``/*`` inside a Windows path are string content, not
    comments, and only a scanner that tracks string state can tell them
    apart. Offsets are preserved so a span found in the blanked text
    addresses the same bytes in the original.
    """
    out = list(text)
    i, n = 0, len(text)
    in_string = False
    while i < n:
        char = text[i]
        if in_string:
            if char == "\\":
                i += 2
                continue
            if char == '"':
                in_string = False
            i += 1
        elif char == '"':
            in_string = True
            i += 1
        elif char == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                out[i] = " "
                i += 1
        elif char == "/" and i + 1 < n and text[i + 1] == "*":
            end = text.find("*/", i + 2)
            end = n if end == -1 else end + 2
            for j in range(i, end):
                if out[j] != "\n":
                    out[j] = " "
            i = end
        else:
            i += 1
    return "".join(out)


def blank_trailing_commas(blanked: str) -> str:
    """Blank trailing commas so stdlib :func:`json.loads` accepts the text."""
    out = list(blanked)
    i, n = 0, len(blanked)
    in_string = False
    last_comma = -1
    while i < n:
        char = blanked[i]
        if in_string:
            if char == "\\":
                i += 2
                continue
            if char == '"':
                in_string = False
            i += 1
            continue
        if char == '"':
            in_string = True
            last_comma = -1
        elif char == ",":
            last_comma = i
        elif char in "}]":
            if last_comma != -1:
                out[last_comma] = " "
            last_comma = -1
        elif char not in _JSON_WS:
            last_comma = -1
        i += 1
    return "".join(out)


def loads(text: str) -> t.Any:
    """Parse JSONC text into plain Python objects."""
    if not text.strip():
        return {}
    return json.loads(blank_trailing_commas(blank_comments(text)))


class _JsoncScanner:
    """Locate value spans inside comment-blanked JSON text."""

    def __init__(self, text: str) -> None:
        self.text = text
        self.pos = 0

    def skip_ws(self) -> None:
        """Advance past insignificant whitespace."""
        while self.pos < len(self.text) and self.text[self.pos] in _JSON_WS:
            self.pos += 1

    def read_string(self) -> str:
        """Consume one string token and return its raw text, quotes included."""
        start = self.pos
        self.pos += 1
        while self.pos < len(self.text):
            char = self.text[self.pos]
            if char == "\\":
                self.pos += 2
                continue
            self.pos += 1
            if char == '"':
                break
        return self.text[start : self.pos]

    def read_value(self) -> tuple[int, int]:
        """Consume one value and return its ``(start, end)`` span."""
        self.skip_ws()
        start = self.pos
        char = self.text[self.pos]
        if char == '"':
            self.read_string()
        elif char in "{[":
            self._read_container()
        else:
            while (
                self.pos < len(self.text)
                and self.text[self.pos] not in ",}]"
                and self.text[self.pos] not in _JSON_WS
            ):
                self.pos += 1
        return start, self.pos

    def _read_container(self) -> None:
        self.pos += 1
        depth = 1
        while self.pos < len(self.text) and depth:
            char = self.text[self.pos]
            if char == '"':
                self.read_string()
                continue
            if char in "{[":
                depth += 1
            elif char in "}]":
                depth -= 1
            self.pos += 1

    def read_members(self, obj_start: int) -> list[_JsoncMember]:
        """Enumerate an object's members. ``obj_start`` indexes its ``{``."""
        self.pos = obj_start + 1
        found: list[_JsoncMember] = []
        while True:
            self.skip_ws()
            if self.pos >= len(self.text) or self.text[self.pos] == "}":
                return found
            if self.text[self.pos] == ",":
                self.pos += 1
                continue
            member_start = self.pos
            raw_key = self.read_string()
            self.skip_ws()
            self.pos += 1  # the ':'
            value_start, value_end = self.read_value()
            found.append(
                _JsoncMember(
                    key=json.loads(raw_key),
                    start=member_start,
                    end=value_end,
                    value_start=value_start,
                    value_end=value_end,
                )
            )


class _JsoncMember(t.NamedTuple):
    """One ``"key": value`` pair located inside a JSONC document.

    Attributes
    ----------
    key : str
        The decoded member name.
    start : int
        Offset of the opening quote of the key.
    end : int
        Offset just past the value — the end of the whole member.
    value_start : int
        Offset of the first byte of the value.
    value_end : int
        Offset just past the last byte of the value.
    """

    key: str
    start: int
    end: int
    value_start: int
    value_end: int


def _render(value: t.Any, depth: int, *, ensure_ascii: bool) -> str:
    """Render ``value`` as JSON text indented for nesting ``depth``."""
    pad = "  " * depth
    if isinstance(value, list) and all(
        isinstance(item, (str, int, float, bool)) or item is None for item in value
    ):
        inline = json.dumps(value, ensure_ascii=ensure_ascii)
        if len(inline) + len(pad) <= _INLINE_WIDTH:
            return inline
    return json.dumps(value, indent=2, ensure_ascii=ensure_ascii).replace(
        "\n", "\n" + pad
    )


def _object_span(blanked: str, path: tuple[str, ...]) -> tuple[int, int] | None:
    """Return the span of the object reached by ``path``, or ``None``."""
    scanner = _JsoncScanner(blanked)
    scanner.skip_ws()
    if scanner.pos >= len(blanked) or blanked[scanner.pos] != "{":
        return None
    cursor = scanner.pos
    for key in path:
        match = next(
            (m for m in _JsoncScanner(blanked).read_members(cursor) if m.key == key),
            None,
        )
        if match is None or blanked[match.value_start] != "{":
            return None
        cursor = match.value_start
    tail = _JsoncScanner(blanked)
    tail.pos = cursor
    return tail.read_value()


def _next_edit(
    text: str,
    data: t.Mapping[str, t.Any],
    path: tuple[str, ...],
    *,
    ensure_ascii: bool,
) -> tuple[int, int, str] | None:
    """Find the one next splice that brings ``path`` closer to ``data``."""
    blanked = blank_comments(text)
    span = _object_span(blanked, path)
    if span is None:
        return None
    obj_start, obj_end = span
    members = _JsoncScanner(blanked).read_members(obj_start)
    by_key = {member.key: member for member in members}
    depth = len(path) + 1
    pad = "  " * depth

    for key, value in data.items():
        member = by_key.get(key)
        if member is None:
            body = _render(value, depth, ensure_ascii=ensure_ascii)
            # Escape the key like any other value: written raw, a backslash
            # or quote in a server name emits text that cannot be parsed
            # back, so the member is never found and the merge re-inserts
            # it until the pass ceiling, holding the swap lock throughout.
            name = json.dumps(key, ensure_ascii=ensure_ascii)
            if members:
                tail = members[-1].end
                return tail, tail, f",\n{pad}{name}: {body}"
            if blanked[obj_start + 1 : obj_end - 1].strip():
                return None
            # Blanking hid any comment the object holds, so measure the
            # interior in the original text and splice after it, not over it.
            interior = text[obj_start + 1 : obj_end - 1]
            anchor = obj_start + 1 + len(interior.rstrip())
            closing = "  " * (depth - 1)
            return anchor, obj_end - 1, f"\n{pad}{name}: {body}\n{closing}"
        current = json.loads(
            blank_trailing_commas(blanked[member.value_start : member.value_end])
        )
        if isinstance(value, dict) and isinstance(current, dict):
            nested = _next_edit(
                text, value, (*path, key), ensure_ascii=ensure_ascii
            )
            if nested is not None:
                return nested
        elif current != value:
            return (
                member.value_start,
                member.value_end,
                _render(value, depth, ensure_ascii=ensure_ascii),
            )

    for index, member in enumerate(members):
        if member.key in data:
            continue
        # Exactly one delimiter leaves with the member: the comma before
        # it, or, for the first member which has none, the comma after.
        if index:
            return members[index - 1].end, member.end, ""
        # Read that comma out of the blanked text -- one inside a comment
        # is not a delimiter, and a real one behind a comment still is.
        trailing = blanked[member.end : obj_end]
        drop_to = member.end
        if trailing.lstrip(_JSON_WS).startswith(","):
            drop_to += trailing.index(",") + 1
        return obj_start + 1, drop_to, ""
    return None


def merge(text: str, data: t.Mapping[str, t.Any], *, ensure_ascii: bool) -> str:
    """Reconcile ``data`` into ``text``, rewriting only members that differ.

    Applies one splice at a time and rescans, so offsets are always
    computed against current text rather than patched up after the fact.
    Config files are small enough that the extra passes do not matter and
    the invariant is worth far more than the cycles.
    """
    if not text.strip():
        return json.dumps(dict(data), indent=2, ensure_ascii=ensure_ascii) + "\n"
    # One splice per member, plus slack; a config that needs more than
    # this has a pathology worth surfacing rather than looping on.
    for _ in range(10_000):
        edit = _next_edit(text, data, (), ensure_ascii=ensure_ascii)
        if edit is None:
            return text
        start, end, replacement = edit
        text = text[:start] + replacement + text[end:]
    msg = "JSONC merge did not converge"
    raise RuntimeError(msg)
