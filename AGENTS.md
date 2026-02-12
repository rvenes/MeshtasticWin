# CODEX Rules (MeshtasticWin)

## Core principles

1. Prefer minimal diffs: change only what is required to satisfy the request.
   - If the request genuinely needs larger changes, do them, but keep them tightly scoped and explain the tradeoff.
2. English only inside the repo for: source code, identifiers, comments, UI text, commit messages, and documentation.
3. This repository is a **.NET 8 WinUI 3** project. Keep changes compatible with that stack unless explicitly upgraded with approval.
4. Ask questions when anything is unclear or requires a decision (UI/UX, behavior, naming, edge cases, data format, etc.).
5. Work safely: avoid destructive operations and do not overwrite or discard work unless it is clearly safe.

## Language Rules

1. Explanations and chat responses: Norwegian (Nynorsk preferred).
2. ALL source code, identifiers, comments, UI text, and commit messages: English only.
3. Never generate Norwegian text inside code or UI.

## Change scope

- Default: minimal, targeted edits.
- Allowed when necessary: larger refactors or multi-file changes **only** when they clearly reduce bugs, improve stability, or are required by the feature/fix.
- Avoid unrelated cleanups.

## Frameworks / libraries

- Default: do **not** introduce new frameworks, UI libraries, or architectural patterns.
- Exception: if a new dependency would make the app **materially better** (stability, performance, accessibility, maintainability, UX), it may be proposed.
  - Before adding it, ask for approval and provide:
    - What it solves and why current stack is insufficient
    - Cost/risks (size, complexity, licensing, maintenance)
    - Minimal integration plan
  - Prefer Microsoft-supported, well-maintained libraries with clear licensing.

## Build / validation (required)

After making changes, validate locally so the user does not need to discover basic failures manually:

1. Ensure the solution builds (no compilation errors).
2. If tests exist, run them.
3. If a specific packaging/build configuration is relevant, validate that path too.

Use standard .NET CLI commands from repo root (examples, adjust to the solution layout):

- dotnet --info
- dotnet restore
- dotnet build -c Debug
- dotnet test (only if tests exist)

If a build/test cannot be run in the current environment, state exactly what could not be run and why, and still keep changes minimal and safe.

## Git operations (automation)

Codex should handle the full Git workflow so the user can simply review a PR link and then pull/merge from GitHub.

### Safety rules

- Run git commands only when safe and unambiguous.
- Never use: force push, hard reset, rewriting history, deleting remote branches, rebasing shared branches, or mass file rewrites unless explicitly requested.
- Never delete anything if there is uncertainty.
- If the working tree is not clean, stop and ask what to do.

### Standard workflow (run from repo root)

1. Update main:

- git switch main
- git pull

2. Create a feature branch:

- git switch -c feature/<name>

3. Implement changes + validate build/tests (see section above).

4. Commit:

- git add -A
- git commit -m "<English technical message>"

5. Push and provide a PR link:

- git push -u origin feature/<name>

Then provide the GitHub URL to open a PR (or use `gh pr create` only if GitHub CLI is installed and authenticated).

6. Do not delete the local branch automatically.
   - After the PR is merged, suggest cleanup commands, but do not run them unless asked:
     - git switch main
     - git pull
     - git branch -d feature/<name>

## Manual approval before commit & push (mandatory)

After implementing changes and successfully validating the build/tests:

1. STOP before running:
   - git add
   - git commit
   - git push

2. Provide a concise summary including:
   - Files changed
   - What was implemented
   - Build/test status
   - Any assumptions or UI decisions

3. Clearly instruct the user how to proceed by printing exactly:

To continue, reply with:
Approved – proceed with commit and push

4. Only after the user replies exactly:

Approved – proceed with commit and push

Then execute the standard Git workflow:

- git switch main
- git pull
- git switch -c feature/<name>
- git add -A
- git commit -m "<English technical message>"
- git push -u origin feature/<name>

Then provide the GitHub PR link.

Never push automatically without explicit approval.

## Environment assumptions

- Windows 11
- Repository path: `H:\Koding\MeshtasticWin`
- Git is executed from Windows PowerShell in the repo root.
- Do not use WSL paths like `/mnt/h/...`.
- Do not use `git -C <path>`.
