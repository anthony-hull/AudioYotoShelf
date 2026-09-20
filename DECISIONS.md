# Decisions

Architectural decisions and hard-won constraints for the homelab fork. Grep this before touching an area.

## 2026-09-20 — Mutation testing with Stryker.NET, two gates, unit tests only

**Decision.** Stryker.NET 4.14.0 (pinned in `dotnet-tools.json`). Two configs: a strict gate on `AudioYotoShelf.Core`
(`stryker-config.core.json`, break 95%) and a whole-solution floor (`stryker-config.json`, break 60%).

**Why two.** One whole-solution number is dominated by `Infrastructure`/`Api` code that only the integration suite
reaches, so a bar high enough to protect Core would fail permanently and one low enough to pass would protect
nothing. Baseline 2026-09-20: whole solution 32.13% before the Core tests were written, 37.80% after; Core 67.57% then 99.58%.

**Unit tests only.** The Testcontainers suite is not run under Stryker (reasoned to be too slow per mutant; not
measured). Integration-only code therefore shows as `NoCoverage`; see `docs/mutation-testing.md` §3.

**Excluded, and why:** migrations, `Program.cs`, EF configurations, `DbContext`, observability. Reasons and the
methods Stryker cannot mutate are in `docs/mutation-testing.md`.

**Found on the way.** `AgeSuggestionService` had an unreachable "no signals, use default range" branch (the duration
signal is always added), and the test named for it asserted loose ranges that never exercised it. The dead branch was
deleted; the test now asserts what the code does (the duration bucket).

**Rollback.** Delete `stryker-config*.json`, `dotnet-tools.json`, `.github/workflows/mutation.yml`; nothing in `src/`
depends on them. The source refactors are behaviour-preserving and stay.
