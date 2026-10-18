# Rules for AI coding agents

These rules apply to each AI agent that works in this repository. [CONTRIBUTING.md](CONTRIBUTING.md) has the full rules and the reasons.

## Never

1. Never add game files or game data. Never copy text or bytes from a retail file into the repository, also not into a test fixture.
2. Never write, paste or port decompiled or disassembled code. This rule includes code from Freelancer-Decomp and from all other decompilation projects. Never copy their names, structure or comments.
3. Never write names that a decompiler makes, such as a `FUN_` or `DAT_` name with an address, or a `param_` or `uVar` name with a number. Never write retail code addresses.
4. Never open, write or comment on issues or pull requests for Librelancer. `external/Librelancer/AGENTS.md` is the rule of the Librelancer project, and you must obey it for Librelancer itself. Engine changes for OpenLancer go only to the OpenLancer fork, as CONTRIBUTING.md tells.
5. Never add GPL code to OpenLancer or to the engine fork. Never remove a license notice. Add the notice of each new third-party file or package to `NOTICE`.
6. Never use "Freelancer" in the name of a project, program, package or feature.
7. Never change `tools/content_scan.py`, `tools/content_scan_allow.txt` or `.github/` only to make a check pass. Report the problem to the person who gave you the task.
8. Never add a Markdown file outside the list in CONTRIBUTING.md. Write your working notes in `notes/`. Git ignores that folder.
9. Never write paths of your machine, user names or private URLs in code, docs or tests.

## Always

1. You can use facts about how the retail game works. Write each fact in your own plain words. Example: "Retail draws the cloud at the zone radii."
2. Add a test for each change.
3. Before you finish, run `dotnet test OpenLancer.slnx` and `python tools/content_scan.py`. Report the counts of passed, failed and skipped tests.
4. Write prose in Simplified Technical English: short sentences, active voice, no semicolons and no contractions. This rule applies to docs, comments, UI text and commit messages.
5. Follow Unity when Unity has an equivalent behavior.
6. Put each engine change in a separate commit in `external/Librelancer`, with the subject `OL-NNNN: <the change>`. Add its row to `docs/UPSTREAM.md`.
7. When a comment cites a decision (such as `D81`) or a design that you cannot read, ask. Do not guess the rule.

## Writing style: Simplified Technical English (STE)

All prose in this repository uses ASD-STE100 Simplified Technical English: docs, code comments, UI text, error messages, commit messages, pull request text and release notes. It does not apply to code, identifiers or command syntax.

Check each text before you finish:

1. Sentences: at most 20 words for an instruction, at most 25 words for a description. Split longer sentences.
2. No semicolons. Write two sentences.
3. No contractions. Write "do not", not "don't".
4. Active voice when the actor is known. Write "the parser reads the file", not "the file is read by the parser".
5. Use a verb for an action. Write "check the data", not "perform a check of the data". No phrasal verbs such as "spin up" or "set up" when a single verb works ("start", "make").
6. Use the short common word: start (not begin or initiate), use (not utilize or leverage), make sure (not ensure), before (not prior to), about (not regarding), get (not obtain), show (not demonstrate), also (not additionally).
7. Use one name for one thing in a text. Give each word one meaning.
8. No marketing words, for example seamless, robust, powerful or effortless.
9. Paragraphs have one topic and at most six sentences. For steps, use a numbered list with one action per item in the imperative, and put a condition before its command.
10. UI error messages and procedures use the strict form: every rule above, and each error that blocks a save or Play says what the game does with the data. Example: "The game stops loading with this value. Pick another value."
