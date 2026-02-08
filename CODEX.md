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
