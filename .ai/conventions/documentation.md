# Documentation Conventions

## Goals

- Make it easy to understand what a script/module does, how to use it, and what it depends on.
- Keep documentation close to the code.

## Style

- Use Markdown (`.md`) for docs.
- Use clear headings and short sections.
- Prefer examples over long prose when explaining usage.

## For Scripts

Each script should have:

- A short description of its purpose.
- A list of parameters/flags and their meaning.
- Examples of common usage.
- Notes on assumptions and side effects.
- Troubleshooting tips if applicable.

This can be:

- in a `README.md` next to the script, or
- in comment-based help (PowerShell), or
- in a header comment (Bash) plus a central doc.

## For IaC

Each module/role should document:

- Purpose and scope.
- Inputs (variables, parameters).
- Outputs (resources, exposed values).
- Dependencies and assumptions.
- Any environment-specific considerations.

## Homelab-Specific Notes

- When relevant, reference the context files in `.ai/context/` to explain why certain choices were made.
