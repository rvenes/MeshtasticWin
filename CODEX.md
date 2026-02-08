# CODEX Rules

1. Always produce minimal diffs. Change only what is required for the request.
2. Write code, comments, identifiers, commit messages, and docs in English only.
3. This repository is a .NET 8 WinUI project. Keep all changes compatible with that stack.
4. Do not refactor unless the user explicitly asks for refactoring.
5. Do not introduce new frameworks, UI libraries, or architectural patterns.
6. Perform safe file edits only: no destructive commands, no unrelated file changes, no broad rewrites.

## Language Rules

1. Explanations and chat responses: Norwegian (Nynorsk preferred).
2. ALL source code, identifiers, comments, UI text, and commit messages: English only.
3. Never generate Norwegian text inside code or UI.

## Git Guidance

When changes are completed, provide suggested git commands for the user, but NEVER run them automatically.

Typical workflow suggestions:

- git switch main
- git pull
- git switch -c feature/<name>
- git add -A
- git commit -m "<message>"
- git push -u origin feature/<name>

Only suggest commands. Do not execute git operations.

## Environment

- The user works on Windows 11.
- Git is executed from Windows PowerShell, not inside WSL.
- The repository is opened directly in `H:\Koding\MeshtasticWin`.
- Never use WSL paths like `/mnt/h/...`.
- Never use `git -C <path>`.
- Always suggest simple git commands assuming the current directory is already the repo root.

Example commands to suggest:

- git status
- git diff
- git add -A
- git commit -m "<message>"
- git push -u origin feature/<name>

## Commit Messages

When changes are completed, always suggest a clear git commit message.

Rules:
- English only
- Short and technical
- Describe WHAT changed, not why or story
- One line summary, optionally bullet points below

Format:

Short summary line

Optional details:
- change 1
- change 2

Only suggest the message. Never run git commit automatically.
