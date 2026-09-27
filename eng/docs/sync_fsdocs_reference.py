"""Generate GitHub-readable F# member pages from fsdocs and Release XML docs."""

from __future__ import annotations

import argparse
import pathlib
import re
import subprocess
import sys
import tempfile
from urllib.parse import urlsplit

ROOT = pathlib.Path(__file__).parents[2]
DESTINATION = ROOT / "docs/fsharp-reference/reference"
SOURCE_REPO = "https://github.com/libtmux/libtmux-dotnet/blob/master/"
SOURCE_ICON = re.compile(
    r"\[!\[Link to source code\]\(\.\./content/img/github\.png\)\]"
    r"\(([^)]*)\)"
)
REFERENCE_LINK = re.compile(r'(?P<lead>\]\(|href=")(?P<url>\.\./[^)"\s]+)')
SOURCE_LINE = re.compile(r"#L(?P<first>\d+)-(?P<last>\d+)")
LOCAL_PATH = re.compile(r"(?:/home/|/tmp/|/Users/|[A-Za-z]:\\Users\\)")
ENTITY_HEADING = re.compile(r"(?m)^(## .+?) (Type|Module|Namespace)$")
EMPTY_INDEX_DESCRIPTION = re.compile(r"(?m)^(\* \[[^\n]+\]\([^)]+\)) -$")
EXCEPTION_TEXT = re.compile(
    r"(\]\(https://learn\.microsoft\.com/dotnet/api/[^)]+\))(?=[A-Z])"
)
CORE_MARKDOWN_LINK = re.compile(
    r"\[([^\]]+)\]\(https://learn\.microsoft\.com/dotnet/api/libtmux\.[^)]+\)"
)
CORE_HTML_LINK = re.compile(
    r'<a href="https://learn\.microsoft\.com/dotnet/api/libtmux\.[^"]+">([^<]+)</a>'
)


def normalize_documents(pages: dict[str, str]) -> dict[str, str]:
    """Make fsdocs links resolve in GitHub's Markdown viewer."""
    names = set(pages)
    normalized: dict[str, str] = {}

    for name, body in pages.items():
        def source_link(match: re.Match[str]) -> str:
            url = match.group(1)
            if not url.startswith(SOURCE_REPO + "src/"):
                raise ValueError(f"{name}: invalid fsdocs source link")
            return f"[Source]({url})"

        body = SOURCE_ICON.sub(source_link, body)
        body = SOURCE_LINE.sub(
            lambda match: (
                f"#L{match.group('first')}"
                if match.group("first") == match.group("last")
                else f"#L{match.group('first')}-L{match.group('last')}"
            ),
            body,
        )
        body = EXCEPTION_TEXT.sub(r"\1 ", body)
        body = CORE_MARKDOWN_LINK.sub(lambda match: f"`{match.group(1)}`", body)
        body = CORE_HTML_LINK.sub(r"\1", body)

        def link(match: re.Match[str]) -> str:
            url = match.group("url")
            parts = urlsplit(url)
            if not parts.path.startswith("../reference/"):
                raise ValueError(f"{name}: unsupported relative link {parts.path}")
            target = pathlib.PurePosixPath(parts.path).name
            if not target.endswith(".md"):
                target += ".md"
            if target not in names:
                raise ValueError(f"{name}: missing reference page {target}")
            fragment = f"#{parts.fragment}" if parts.fragment else ""
            return f"{match.group('lead')}../reference/{target}{fragment}"

        body = REFERENCE_LINK.sub(link, body)
        body = body.replace("# API Reference\n", "# API reference\n")
        body = body.replace("## Available Namespaces\n", "## Available namespaces\n")
        body = ENTITY_HEADING.sub(
            lambda match: f"{match.group(1)} {match.group(2).lower()}", body
        )
        body = "\n".join(line.rstrip() for line in body.splitlines())
        body = EMPTY_INDEX_DESCRIPTION.sub(r"\1", body).rstrip() + "\n"
        if (
            "/content/img/github.png" in body
            or "https://github.com/libtmux/libtmux-dotnet/reference/" in body
            or "https://learn.microsoft.com/dotnet/api/libtmux." in body
            or LOCAL_PATH.search(body)
        ):
            raise ValueError(f"{name}: generated page contains a broken or local link")
        normalized[name] = body

    return normalized


def compare_documents(expected: dict[str, str], destination: pathlib.Path) -> list[str]:
    """Report missing, stale, and extra generated Markdown pages."""
    actual = {
        page.name: page.read_text(encoding="utf-8")
        for page in destination.glob("*.md")
    }
    errors = [f"missing generated page: {name}" for name in sorted(expected.keys() - actual.keys())]
    errors += [f"extra generated page: {name}" for name in sorted(actual.keys() - expected.keys())]
    errors += [
        f"stale generated page: {name}"
        for name in sorted(expected.keys() & actual.keys())
        if expected[name] != actual[name]
    ]
    return errors


def generate_documents(output: pathlib.Path) -> dict[str, str]:
    """Run the pinned local fsdocs tool against the F# Release build."""
    subprocess.run(
        [
            "dotnet", "fsdocs", "build",
            "--input", "docs/fsharp",
            "--projects", "src/LibTmux.FSharp/LibTmux.FSharp.fsproj",
            "--output", str(output),
            "--properties", "Configuration=Release",
            "--sourcefolder", "/_/",
            "--sourcerepo", SOURCE_REPO,
            "--parameters", "root", "../",
            "--strict",
        ],
        check=True,
        cwd=ROOT,
    )
    pages = {
        page.name: page.read_text(encoding="utf-8")
        for page in (output / "reference").glob("*.md")
    }
    if "index.md" not in pages or len(pages) < 2:
        raise ValueError("fsdocs did not generate the F# member reference")
    normalized = normalize_documents(pages)
    if not any("[Source](" in body for body in normalized.values()):
        raise ValueError("fsdocs did not generate source links")
    return normalized


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    with tempfile.TemporaryDirectory(prefix="libtmux-fsharp-fsdocs-") as temporary:
        expected = generate_documents(pathlib.Path(temporary) / "output")

    if args.check:
        errors = compare_documents(expected, DESTINATION)
        for error in errors:
            print(error, file=sys.stderr)
        return int(bool(errors))

    DESTINATION.mkdir(parents=True, exist_ok=True)
    for page in DESTINATION.glob("*.md"):
        if page.name not in expected:
            page.unlink()
    for name, body in expected.items():
        (DESTINATION / name).write_text(body, encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
