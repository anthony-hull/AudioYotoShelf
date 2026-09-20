# AudioYotoShelf (homelab fork)

This project follows the Right Advance Digital global standards. Global config is loaded from
`~/.claude/CLAUDE.md` — read it first on every session.

## Mutation testing

Read `docs/mutation-testing.md` **before** running Stryker or chasing a surviving mutant. It lists what is
excluded, what Stryker cannot mutate, and what only looks like a gap (integration-only code, equivalent
mutants, generated code), so those are not re-investigated.

- Core gate: `dotnet stryker --config-file stryker-config.core.json` (~1 min, must stay >= 95%).
- Whole solution: `dotnet stryker` (~3 min, floor 94%). Read results with `python3 scripts/mutation-summary.py --survivors`, not the console table.
- Never lower a `break` threshold or delete an exclusion glob without a `DECISIONS.md` entry.
