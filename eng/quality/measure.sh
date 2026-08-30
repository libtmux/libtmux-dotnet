#!/usr/bin/env bash
# Print the numbers docs/quality-bar.md cites, so the rating is a measurement
# rather than a memory. Every line here is something that can fall, and the
# point of printing them together is that a regression is visible before it is
# argued about.
set -euo pipefail
cd "$(dirname "$0")/../.."

count_uses() { rg -o 'uses: [^ ]+' .github/workflows/*.yml | wc -l | tr -d ' '; }
count_pinned() { rg -o 'uses: [^ ]+' .github/workflows/*.yml | rg -c '@[0-9a-f]{40}' || echo 0; }
sum_matches() { rg -c "$1" "${@:2}" 2>/dev/null | awk -F: '{s+=$2} END {print s+0}'; }

DOC_FILES=(README.md src/LibTmux/README.md src/LibTmux.Query.Json/README.md
           src/LibTmux.Workspace/README.md src/LibTmux.Mcp/README.md
           docs/modes/one-shot.md docs/modes/control-mode.md
           docs/modes/chaining.md docs/modes/matrix.md)

printf '%-38s %s\n' "tmux versions proven in CI" \
  "$(rg -o "'3\.[0-9a-z]+'" .github/workflows/dotnet-tmux.yml | sort -u | wc -l | tr -d ' ')"
printf '%-38s %s\n' "target frameworks" \
  "$(rg -o "'net[0-9.]+'" .github/workflows/dotnet-tmux.yml | sort -u | tr '\n' ' ')"
printf '%-38s %s\n' "doc examples compiled" "$(sum_matches '```csharp' "${DOC_FILES[@]}")"
printf '%-38s %s\n' "doc examples executed on live tmux" "$(sum_matches '```csharp run' "${DOC_FILES[@]}")"
printf '%-38s %s\n' "fuzz cases per run" \
  "$(rg -o 'CasesPerTarget = [0-9_]+' tests/LibTmux.UnitTests/Fuzzing/ParserFuzzTests.cs \
     | rg -o '[0-9_]+' | tail -1 | tr -d '_')"
printf '%-38s %s/%s\n' "actions pinned to a commit SHA" "$(count_pinned)" "$(count_uses)"
# Counted from the workflow rather than from the scripts on disk: the gate is
# what CI runs, and a script nobody invokes is not a check.
printf '%-38s %s\n' "document validators" \
  "$(rg -c 'uv run.*(verify_.*\.py|--check)' .github/workflows/dotnet.yml | tr -d ' ')"
printf '%-38s %s\n' "decision records" "$(fd -e md . docs/decisions -d 1 | wc -l | tr -d ' ')"
printf '%-38s %s\n' "recorded benchmark runs" "$(fd -e md . docs/benchmarks/runs 2>/dev/null | wc -l | tr -d ' ')"
printf '%-38s %s\n' "published packages" \
  "$(rg -l '<IsPackable>true' src/*/*.csproj | wc -l | tr -d ' ')"
printf '%-38s %s\n' "projects suppressing CS1591 (want 0)" \
  "$(rg -l 'CS1591' src/LibTmux/LibTmux.csproj src/LibTmux.Query.Json/LibTmux.Query.Json.csproj 2>/dev/null | wc -l | tr -d ' ')"

missing=0
# GitHub reads these from the root or from .github, so both count as present.
for f in README.md LICENSE SECURITY.md CONTRIBUTING.md CODE_OF_CONDUCT.md CHANGELOG.md; do
  [ -e "$f" ] || [ -e ".github/$f" ] \
    || { echo "MISSING: $f" >&2; missing=$((missing + 1)); }
done
printf '%-38s %s\n' "standard project files missing" "$missing"

# A raw control byte in a source file compiles, is invisible in review, and
# makes the file binary to git and unreadable to rg.
control_bytes=$(python3 - <<'PY'
import pathlib
bad = []
for path in list(pathlib.Path("src").rglob("*.cs")) + list(pathlib.Path("tests").rglob("*.cs")):
    if "/obj/" in path.as_posix() or "/bin/" in path.as_posix():
        continue
    raw = path.read_bytes()
    if any(bytes([b]) in raw for b in range(32) if b not in (9, 10, 13)):
        bad.append(str(path))
print(len(bad))
for path in bad:
    print(path)
PY
)
offenders=$(printf '%s' "$control_bytes" | head -1)
printf '%-38s %s\n' "source files with control bytes" "$offenders"
if [ "$offenders" != "0" ]; then
  printf '%s\n' "$control_bytes" | tail -n +2 >&2
  missing=$((missing + offenders))
fi

exit "$missing"
