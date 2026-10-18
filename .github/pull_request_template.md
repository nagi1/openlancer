## What this pull request does

<!-- One topic. Tell what changes and why, in plain words (STE). -->

Milestone: <!-- for example M23, see ROADMAP.md. For work outside a milestone, link the issue. -->

## Legal

- [ ] It adds no game files and no game data. Test fixtures are written by hand.
- [ ] It adds no decompiled or disassembled code, also none from Freelancer-Decomp.
- [ ] Facts about the retail game are in plain words, without decompiler names or addresses.
- [ ] License notices are kept. `NOTICE` has each new third-party file or package.
- [ ] It adds no GPL code.
- [ ] No new name uses "Freelancer".
- [ ] Nothing goes to Librelancer.

## Tests

- [ ] Each change has a test.
- [ ] `dotnet test OpenLancer.slnx` passes on my machine.
- [ ] `python tools/content_scan.py` passes.
- [ ] Retail tests with my own game data. Passed / failed / skipped: <!-- counts, or "no game data" -->
- [ ] Engine changes only: the fork tests pass.

## Docs

- [ ] The docs of the changed behavior are up to date.
- [ ] Prose in docs, comments, UI text and commit messages is in STE.
- [ ] When Unity has the same behavior, the change follows Unity.
- [ ] It adds no Markdown file outside the list in CONTRIBUTING.md.

## Engine fork

<!-- Only when the pull request moves external/Librelancer. -->

- [ ] Each engine change is a separate OL-NNNN commit, with a row in `docs/UPSTREAM.md`.
- [ ] `.gitmodules` does not change.
- Fork pull request: <!-- link -->
