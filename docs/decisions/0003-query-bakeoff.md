# ADR 0003: Closed query catalog

## Status

Accepted for the local query document surface. Automatic native pushdown is
rejected for production.

The retained bakeoff measured pushdown, but production does not assemble typed
documents into tmux formats. A native `-f` expression is executable tmux
format-language input, including shell-job forms. Avoiding a few kilobytes on a
local pipe does not justify adding an escaping, version-profile, command-budget,
residual-evaluation, and relation-capture subsystem to that boundary. Typed
documents therefore evaluate only over captured objects. `UnsafeTmuxFilter`
remains the explicit native escape hatch and makes no equivalence guarantee.

The remote-planning results and graft list below are historical bakeoff evidence,
not unimplemented production requirements.

The production catalog is checked-in code rather than a private analyzer
project. The imported generator had become a post-initialization emitter over a
hard-coded table and retained none of the bakeoff contender's `LTQG001`–`LTQG008`
integrity diagnostics. Keeping that extra build project therefore added an
indirection without the property that selected it. One immutable field table
now owns target, value kind, relation, CLR property, and bound accessors. A
unit contract compares those entries with the shipped JSON Schema, and the
package NativeAOT smoke exercises the table.

## Context

The bakeoff compared attribute discovery, a hand-written static table, and a
source-generated field catalog against one expression, AST, JSON, local
interpretation, pushdown, and NativeAOT corpus. The evaluated tree is
`953a1970d91bbe319906a8a2e294799eb4b966ca` with source fingerprint
`8e2e192bc0bd0bbaabc8368dff7de8d1bd1b3cd4fa42f2a77d8ab0213a64aee3`.

The retained matrix ran on Linux with .NET SDK 10.0.302, `net8.0` and
`net10.0`, and tmux 3.2a through 3.7b. Advisory tmux master reported itself as
`next-3.8`; no capability profile is inferred from that development version.
Its failed rows inform the compatibility boundary but do not determine the
winner.

All three catalogs implement the same closed 12-field schema and pass the
required semantic corpus. The generated catalog wins because it checks the
complete field manifest, owning snapshot type, value kind, and relation target
at compile time while emitting deterministic lookup code. The attribute
catalog discovers the schema at runtime. The static catalog duplicates the
schema in a hand-maintained table.

| Requirement | Attributes | Static | Generated |
| --- | --- | --- | --- |
| Shared semantic and JSON corpus | Passed | Passed | Passed |
| Exact compile-time closed-manifest diagnostics | No | No | Yes |
| Runtime catalog discovery | Yes | No | No |
| NativeAOT execution on both frameworks | Passed | Passed | Passed |

Every contender still receives `MemberInfo` at its lookup boundary and reports
that runtime member metadata and public property metadata are required. The
generated result therefore establishes deterministic discovery and diagnostics,
not metadata-free execution.

## Decision

Use a closed field catalog with one immutable canonical query AST shared by
expression translation, direct local interpretation, and JSON. The catalog is
an internal implementation detail; public API does not expose its vocabulary.

Public query entry points do not require a catalog or capability object. Query
translation and interpretation are independent of the connected tmux version.

Public query documents have structural value equality and hashing across
sequence-bearing nodes. Equality does not depend on `ImmutableArray` backing
storage or object identity.

Expression translation is pure and tmux-version-independent. It freezes
captured constants, rejects unsupported conversions and culture-sensitive
overloads, validates the complete document against the closed catalog, and
never compiles the original expression as a fallback. The direct AST
interpreter is the required semantic path. An optional cached dynamic-code path
may run only when supported and must remain differential-equivalent.

`Matching()` filters an `IEnumerable<T>` snapshot and materializes an
`IReadOnlyList<T>`. Cardinality keeps BCL names and exceptions. The only
Python-style lookup parser is the narrow `name__contains` edge adapter; it
produces the same canonical AST.

Approve schema `libtmux-query`, version 1, and the retained JSON Schema and four
positive golden documents. The wire grammar is closed, rejects unknown members
and discriminators, and uses tagged null, Boolean, signed 64-bit integer,
string, typed-ID, enum, and Unix-seconds instant constants. Custom parser limits
may tighten but not widen the v1 limits. Regex nodes use the .NET dialect,
require `CultureInvariant`, reject unsupported option bits and inline
culture-dependent case behavior, count pattern limits by Unicode scalar, and
execute with a timeout.

Typed documents are not lowered into native tmux filters. Callers capture or
list entities, then use `Matching()`; relation predicates declare the snapshot
depth they need and fail on uncaptured relations. Raw native filters remain a
separate `UnsafeTmuxFilter` operation with no typed-query equivalence claim.

The measured `list-*` projections remain historical evidence about tmux filter
behavior. Ordinary production listings continue to use ADR 0001 framing and
ADR 0002 acquisition and error policies; typed queries add no second listing
path.

## Matrix observations

Every required lane passed 650 tests with zero skips. The two advisory master
rows failed because `next-3.8` has no accepted capability profile.

| tmux | Source commit | `net8.0` | `net10.0` | Role |
| --- | --- | --- | --- | --- |
| 3.2a | `3b929f332aafa7f1080eacc31feb11ffbb1d1841` | Passed | Passed | Required |
| 3.3a | `0b355ae8114511e1ff6359272b164f1cdf718e80` | Passed | Passed | Required |
| 3.4 | `9ae69c3795ab5ef6b4d760f6398cd9281151f632` | Passed | Passed | Required |
| 3.5 | `ac44566c9c7e3e94d23be6def4c7ae83472543f5` | Passed | Passed | Required |
| 3.6 | `0dac7fe434d029a4f0b819cba8eb7963df291990` | Passed | Passed | Required |
| 3.7a | `0e418b62d259ce8da8970f75732cc6632ee4c3a0` | Passed | Passed | Required |
| 3.7b | `e802909de06012a4df6209d55e86487c56223163` | Passed | Passed | Required |
| master | `851c5a933d4838c32ad06c248b2ba975d106149c` | Failed | Failed | Advisory |

The retained real-server corpus compares the reference interpreter, direct
interpreter, `Matching()`, disabled planning, automatic planning, eligible
required planning, and raw tmux IDs. It covers residual regex evaluation,
typed IDs, relation truth tables, target-specific list commands, unsafe native
filters, escaped literals, a balanced 101-conjunct filter, and command-size
fallback.

## Tmux capabilities

The measured server-wide candidate capability profile is selected from an
exact supported tmux version, not a version-order guess.

| tmux | Sessions | Windows | Panes | Clients |
| --- | --- | --- | --- | --- |
| 3.2a–3.3a | `list-sessions -f` | `list-windows -a -f` | `list-panes -a -f` | Residual only |
| 3.4–3.7b | `list-sessions -f` | `list-windows -a -f` | `list-panes -a -f` | `list-clients -f` |

Every required version uses a tmux format-loop limit of 100 and a maximum
packed command-argument budget of 16,364 bytes. The planner counts UTF-8 bytes
plus the terminating NUL for each exact command argument. These protocol limits
and target command profiles cannot be widened through public construction.

## NativeAOT observations

All six catalog and framework NativeAOT lanes published and executed. The host
performed catalog lookup in both directions, canonical JSON round trips, all
four golden-document round trips, and a nested direct-interpreter scenario.
That is evidence of static publish-and-run viability, not full production AOT
or package readiness. Allocation values are observations from this host and are
not thresholds.

| Catalog | `net10.0` allocated bytes | `net8.0` allocated bytes |
| --- | ---: | ---: |
| Attributes | 48,792 | 53,152 |
| Static | 43,072 | 52,880 |
| Generated | 48,760 | 52,832 |

## Production consequences

The production implementation retains the parts that serve local portable
queries:

- One internal immutable catalog over the approved production snapshots, with
  its field manifest checked against the shipped schema.
- One public immutable query-document contract shared with the optional JSON
  package, without public contender or source-generator vocabulary.
- Structural equality and hashing for the full public AST, including
  sequence-bearing Boolean nodes.
- The direct interpreter as semantic owner, with one-time binding and no
  compilation of the caller's original expression.
- ADR 0002 snapshot-depth and captured-relation behavior for relation
  quantifiers, including incomplete-snapshot errors before enumeration.
- Python parity dispositions for the complete QueryList inventory while keeping
  BCL cardinality and the narrow `name__contains` parser.
- Shipping trimming analysis, Public API baselines, package validation,
  platform annotations, and supported-platform AOT tests.
- Trimming annotations and AOT cases for the retained `MemberInfo`, public-
  property metadata, and captured-field access shapes.

## Rejected risks

- Runtime attribute discovery as the production schema authority.
- Independent hand-maintained catalog and schema tables without a drift gate.
- `IQueryable`, silent client evaluation, or compilation of the caller's
  original expression as a fallback.
- Culture-sensitive string translation or regex evaluation.
- JSON limits that widen the frozen v1 resource contract.
- Caller-supplied tmux command profiles, physical mappings, or protocol safety
  limits.
- Public AST equality that depends on collection backing identity.
- Left-deep or oversized filters that can return silent false negatives.
- Numeric or instant pushdown without a proven canonical physical
  representation.
- A public plan shape that lets callers omit residual evaluation.
- Residual relation evaluation without a declared snapshot-depth requirement.
- Safe typed queries and raw native filters sharing one execution surface.
- Automatic lowering of typed documents into tmux's executable format language.
- Delimiter-joined tmux rows as a production materialization protocol.
- One global list command per target that erases acquisition scope and list-
  error policy.
- Treating `next-3.8` as a known stable profile through version ordering.
- Selecting a catalog from one allocation observation or NativeAOT smoke
  success alone.
- Literal recreation of every Python QueryList lookup spelling and `get()`
  alias.
- Public bakeoff projects and contender names as the shipping package boundary.

## Remaining unknowns

- macOS behavior for NativeAOT publication.
- Shipping trimming, Public API, and platform-annotation results.
- Allocation and local-matching behavior on large captured topologies.
- Final public names and exhaustive Python inventory dispositions, which belong
  to the public-API approval decision.

Decision 0003 adds causal version evidence for the target-specific list filter
commands and the measured format and packed-command safety boundaries. It does
not claim the complete tmux command-flag or format-field and operator surface.

## Critic dispositions

Framework-design, Python-parity, and tmux-protocol reviews are recorded in
`evidence/0003/critic-reviews.md`. Every accepted finding is represented by a
causal fix, a bounded measured claim, or a production consequence. No review
blocks the generated closed-catalog decision.

## Study-source removal proof

`evidence/0003/deletion.json` is generated from the staged index and current
solution. It proves the query AOT runner and complete spike tree are absent,
the solution contains no bakeoff or test-child project token, and zero projects
remain. The validator retains the evaluated source through Git ancestry while
rechecking the live absence claims.

`evidence/0003/SHA256SUMS` binds the complete retained evidence tree after the
proof is added.

## Machine-readable decision inputs

```json
{
  "schemaVersion": 1,
  "decisionId": "0003",
  "evaluatedCommit": "953a1970d91bbe319906a8a2e294799eb4b966ca",
  "decisionInputs": {
    "approvedDesign": "docs/superpowers/specs/2026-08-09-libtmux-csharp-design.md",
    "approvedPlan": "docs/superpowers/plans/2026-08-09-libtmux-csharp-bakeoffs.md",
    "pythonSourceRevision": "c4a980b32fedb10539fddf836373e4618c53731c",
    "sourceTreeFingerprint": "8e2e192bc0bd0bbaabc8368dff7de8d1bd1b3cd4fa42f2a77d8ab0213a64aee3",
    "contenderRevisions": {
      "attributes": [
        "73a115bd5b7886bea038c65b4a5dacab9b0dd46a"
      ],
      "static": [
        "9bfed1a0d8465dd5ca4a005433795e53625606fe"
      ],
      "generated": [
        "40efcd598fb45d0e49101d2e8511d8d6409fe509"
      ]
    },
    "reviewFixRevisions": [
      "95e3725d34d7d43fdfc242e8d454dae523a08a64",
      "7aead4c64c6f9510f85e0a58b6b4d70e5849788f"
    ],
    "evidenceToolRevisions": [
      "b0a2af0ca2bbba7adf369c3167616407c67a6145",
      "c49ba526ba315fe8a5a7246e5abb578058812569",
      "953a1970d91bbe319906a8a2e294799eb4b966ca"
    ],
    "workloadContract": "csharp/spikes/LibTmux.QueryBakeoff.Core/QueryAst.cs",
    "corpus": "csharp/spikes/LibTmux.QueryBakeoff.Tests/QueryOracleCases.cs",
    "referenceInterpreter": "csharp/spikes/LibTmux.QueryBakeoff.Tests/ReferenceQueryInterpreter.cs",
    "matrix": "evidence/0003/results.ndjson",
    "aotResults": "evidence/0003/aot/aot-results.ndjson",
    "allocations": "evidence/0003/aot/allocations.ndjson",
    "apiExamples": "evidence/0003/aot/api-examples.md",
    "schema": "evidence/0003/aot/libtmux-query-v1.schema.json",
    "goldens": "evidence/0003/aot/goldens",
    "environment": "evidence/0003/environment.json",
    "pythonParity": "csharp/docs/parity/python-public-api.json",
    "versionLedger": "csharp/docs/parity/version-deltas.json"
  },
  "commands": [
    "cd csharp && eng/tmux/run-matrix.sh --include-master-advisory --evidence-dir artifacts/evidence-staging/0003/matrix spikes/LibTmux.QueryBakeoff.Tests/LibTmux.QueryBakeoff.Tests.csproj",
    "cd csharp && eng/aot/run-query-aot.sh --evidence-dir artifacts/evidence-staging/0003/aot --write-contract",
    "uv run python csharp/eng/evidence/assemble_bundle.py --producer matrix=csharp/artifacts/evidence-staging/0003/matrix --producer aot=csharp/artifacts/evidence-staging/0003/aot --output csharp/docs/decisions/evidence/0003",
    "uv run python csharp/eng/parity/reconcile_versions.py --evidence csharp/docs/decisions/evidence/0003/results.ndjson --write",
    "uv run python csharp/eng/evidence/validate.py --phase pre-deletion csharp/docs/decisions/evidence/0003",
    "uv run python csharp/eng/evidence/record_deletion.py --solution csharp/LibTmux.slnx --absent csharp/eng/aot --absent csharp/spikes --absent csharp/artifacts --tracked-prefix csharp/eng/aot/run-query-aot.sh --tracked-prefix csharp/spikes --project-token LibTmux.QueryBakeoff --project-token LibTmux.BakeoffSupport --project-token LibTmux.TestChild --project-count 0 --output csharp/docs/decisions/evidence/0003/deletion.json",
    "uv run python csharp/eng/evidence/hash_tree.py csharp/docs/decisions/evidence/0003",
    "uv run python csharp/eng/evidence/validate.py csharp/docs/decisions/evidence/0003"
  ],
  "hardGates": [
    {
      "name": "required version and framework matrix",
      "status": "passed",
      "evidence": "evidence/0003/results.ndjson contains 14 passing required lanes with 650 tests and zero skips per lane"
    },
    {
      "name": "canonical semantic equivalence",
      "status": "passed",
      "evidence": "The shared corpus compares the independent reference interpreter, direct interpreter, Matching, disabled planning, automatic planning, eligible required planning, and raw tmux IDs"
    },
    {
      "name": "closed version-one JSON contract",
      "status": "passed",
      "evidence": "The retained schema and four positive goldens pass exact round-trip, unknown-member, discriminator, culture, and resource-limit tests on both target frameworks"
    },
    {
      "name": "versioned server-wide pushdown safety",
      "status": "passed",
      "evidence": "Required lanes prove target-specific list-filter support, balanced conjunctions, exact format-depth and packed-argv bounds, residual fallback, and fail-before-dispatch validation"
    },
    {
      "name": "generated closed-manifest diagnostics",
      "status": "passed",
      "evidence": "The generated contender rejects missing fields and owner, member, kind, and relation declaration mismatches before emitting source"
    },
    {
      "name": "NativeAOT static catalog execution",
      "status": "passed",
      "evidence": "evidence/0003/aot/aot-results.ndjson contains six passing catalog and framework lanes with exact schema and golden artifacts"
    },
    {
      "name": "public evidence redaction",
      "status": "passed",
      "evidence": "evidence/0003/redaction-proof.json and evidence/0003/aot/redaction-proof.json cover the canonical sensitive-data categories"
    },
    {
      "name": "adversarial review resolution",
      "status": "passed",
      "evidence": "evidence/0003/critic-reviews.md records three complete reviews with every accepted finding resolved"
    },
    {
      "name": "historical study-source removal proof",
      "status": "passed",
      "evidence": "evidence/0003/deletion.json binds the staged removal of the query AOT runner and complete spike tree while preserving evaluated source ancestry and live absence checks"
    }
  ],
  "winner": "generated closed field catalog with direct canonical-AST interpretation",
  "grafts": [
    "Internal generator and generated catalog over the approved production snapshots with an exact compile-time manifest",
    "One public immutable query-document contract shared with the optional JSON package without contender vocabulary",
    "Catalog-free public query entry points and immutable read-only explain results",
    "Structural equality and hashing for the full public AST including sequence-bearing nodes",
    "Internal execution that always applies the residual predicate after candidate materialization",
    "Residual-plan relation-depth requirements that drive capture or fail before local evaluation",
    "A separately named unsafe native-filter operation with no typed-query equivalence claim",
    "ADR 0001 byte-length framing instead of delimiter parsing for query candidate rows",
    "Acquisition-scoped Server, Session, and Window list commands that preserve ADR 0002 list-error policies",
    "Exact internal source-bound server-wide filter and physical-field profiles for each supported tmux version",
    "An internal parsed-version capability service rather than caller-authored strings or source-label aliases",
    "Balanced filter composition and fail-before-dispatch format-depth and packed-argv checks",
    "Direct canonical-AST interpretation with an optional differential-tested dynamic-code fast path",
    "ADR 0002 snapshot-depth and captured-relation semantics for relation quantifiers",
    "Python parity dispositions using BCL cardinality and only the narrow name__contains edge parser",
    "Build-private or separately packaged analyzer deployment with tested compiler compatibility and transitivity",
    "Shipping trimming analysis, Public API baselines, package validation, platform annotations, and supported-platform AOT tests",
    "Trimming annotations and AOT cases for runtime member metadata and captured-field access"
  ],
  "rejectedRisks": [
    "Runtime attribute discovery as the production schema authority",
    "A manually duplicated static table as the production schema authority",
    "IQueryable, silent client evaluation, or compilation of the original expression as fallback",
    "Culture-sensitive string translation or regex evaluation",
    "JSON limits that widen the frozen version-one resource contract",
    "Caller-supplied tmux command profiles, physical mappings, or protocol safety limits",
    "Public AST equality that depends on collection backing identity",
    "Left-deep or oversized filters that can return silent false negatives",
    "Numeric or instant pushdown without a proven canonical physical representation",
    "A public plan shape that permits omission of residual evaluation",
    "Residual relation evaluation without a declared snapshot-depth requirement",
    "Safe typed queries and raw native filters sharing one execution surface",
    "Delimiter-joined tmux rows as a production materialization protocol",
    "One global list command per target that erases acquisition scope and list-error policy",
    "Treating next-3.8 as a known stable profile through version ordering",
    "Selection from one allocation observation or NativeAOT smoke success alone",
    "Literal recreation of every Python QueryList lookup spelling and get alias",
    "Public bakeoff projects and contender names as the shipping package boundary"
  ],
  "remainingUnknowns": [
    "the exact query capability profile for the current tmux development branch",
    "macOS behavior for NativeAOT publication and real-tmux pushdown",
    "analyzer package layout, transitivity, compiler compatibility, and package validation",
    "shipping trimming, Public API, and platform-annotation results",
    "production query execution over the complete approved hierarchy and snapshot materializer",
    "scoped Session and Window query execution through production acquisition and list-error policy adapters",
    "allocation and candidate-materialization behavior on large real topologies",
    "final public names and exhaustive Python inventory dispositions"
  ],
  "capabilities": [
    "closed sealed-record query AST and seven tagged constant kinds",
    "pure expression translation with frozen constants and no silent fallback",
    "direct local interpretation and immutable Matching results",
    "version-one canonical JSON schema with bounded parsing and invariant regex semantics",
    "server-wide versioned list-filter and physical-field capabilities",
    "disabled, automatic, and required planning with explicit residual work",
    "balanced and protocol-bounded typed-ID and Boolean pushdown",
    "separate unsafe native-filter explanation",
    "compile-time exact-manifest diagnostics for the generated catalog",
    "static NativeAOT execution on net8.0 and net10.0"
  ],
  "evidenceFiles": [
    "evidence/0003/environment.json",
    "evidence/0003/results.ndjson",
    "evidence/0003/redaction-proof.json",
    "evidence/0003/protocol-transcripts/control.txt",
    "evidence/0003/protocol-transcripts/pty.txt",
    "evidence/0003/aot/aot-results.ndjson",
    "evidence/0003/aot/allocations.ndjson",
    "evidence/0003/aot/api-examples.md",
    "evidence/0003/aot/libtmux-query-v1.schema.json",
    "evidence/0003/aot/goldens/attached-nvim.json",
    "evidence/0003/aot/goldens/regex-invariant.json",
    "evidence/0003/aot/goldens/turkish-ignore-case.json",
    "evidence/0003/aot/goldens/typed-id.json",
    "evidence/0003/aot/redaction-proof.json",
    "evidence/0003/critic-reviews.md",
    "evidence/0003/deletion.json",
    "evidence/0003/SHA256SUMS"
  ],
  "criticDispositions": "evidence/0003/critic-reviews.md"
}
```
