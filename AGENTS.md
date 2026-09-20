# Agent instructions

Follow the existing project conventions and keep changes narrowly scoped to
what was asked for.

## Change discipline

These apply to every change, whatever it touches:

- Make the smallest coherent change that solves the verified problem. Keep
  unrelated cleanup out of it.
- Reuse an existing file, helper, API, or test before adding a new one.
- Keep a new type or member internal until a caller outside the assembly needs
  it. PublicApiAnalyzers owns declaration baselines; compiler metadata supplies
  documentation identities and project policy checks.
- Add a file only for a durable boundary — a distinct responsibility or
  independent reuse — not for a single-use helper or a one-line re-export.
- A passing gate is evidence only once it has been shown capable of failing.
  Pair a new test with a deliberate break that proves it bites.

## Which policy applies

This file routes; it does not restate. Read the one that governs the change
being made:

- For changes to documentation or user-facing prose — `README.md`,
  `CHANGELOG.md`, release notes, commit messages, CLI and help text, error
  messages, XML documentation, or source comments — follow
  [`.github/WRITING.md`](.github/WRITING.md).
- For building, testing, the gates, pull requests, and releases, follow
  [`.github/CONTRIBUTING.md`](.github/CONTRIBUTING.md).
- For a security-sensitive change, or to report a vulnerability, follow
  [`SECURITY.md`](SECURITY.md).

Each is the single home for its subject. Where a rule appears to be stated
twice, the file listed above governs.
