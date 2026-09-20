# Decisions

Architectural decisions and hard-won constraints for the homelab fork. Grep this before touching an area.

## 2026-09-20 — Mutation testing with Stryker.NET, two gates, unit tests only

**Decision.** Stryker.NET 4.14.0 (pinned in `dotnet-tools.json`). Two configs: a strict gate on `AudioYotoShelf.Core`
(`stryker-config.core.json`, break 95%) and a whole-solution floor (`stryker-config.json`, break 94%).

**Why two.** The Core gate is the fast loop (~1 min) and holds the pure logic to a stricter bar. The whole-solution
run (~4 min) is the floor against decay everywhere else. At setup the whole-solution number was dominated by
`Infrastructure`/`Api` code with thin unit tests (32.13%), which is why one bar could not serve both; after the
follow-up work it is 96.90% and the two differ mainly in speed and strictness.
Baseline 2026-09-20: whole solution 32.13% at setup (272 unit tests), **96.90%** after the follow-up work (1,013 tests);
Core 67.57% then 99.58%.

**Unit tests only.** The Testcontainers suite is not run under Stryker (reasoned to be too slow per mutant; not
measured). Whatever only the integration suite reaches (startup wiring, EF mappings) is excluded or unscored; see
`docs/mutation-testing.md` §1 and §3.

**Excluded, and why:** migrations, `Program.cs`, EF configurations, `DbContext`, observability. Reasons and the
methods Stryker cannot mutate are in `docs/mutation-testing.md`.

**Found on the way.** `AgeSuggestionService` had an unreachable "no signals, use default range" branch (the duration
signal is always added), and the test named for it asserted loose ranges that never exercised it. The dead branch was
deleted; the test now asserts what the code does (the duration bucket).

**Not in CI (2026-09-20, Anthony).** Mutation runs are expensive, so no workflow runs them: not on pull requests, not on
a schedule, not on demand. The gates are run locally before merging. An earlier draft had a `mutation.yml` that ran both
on PRs and weekly; it was removed before it ever ran.

**Rollback.** Delete `stryker-config*.json`, `dotnet-tools.json`, `scripts/mutation-summary.py`; nothing in `src/`
depends on them. The source refactors are behaviour-preserving and stay.

## 2026-09-20 — Findings from mutation testing, and what became of them

Each was confirmed by reading the code at the cited place. Every fix was test-first (the test failed before the change).

**Fixed**
1. **`/api/health` reported Postgres healthy when it was down** (`HealthController`): `CanConnectAsync` returns `false`
   for an unreachable server rather than throwing, and the result was discarded. Now returns 503. Consequence, accepted
   on 2026-09-20: the Docker `HEALTHCHECK` uses this endpoint, so during a database outage the container goes unhealthy
   and Traefik drops the whole app from routing. Roll back by reverting the `fix: /api/health` commit.
2. **Admin was granted but never revoked** (`AuthController`): on a login against the trusted admin server the stored
   `IsAdmin` now follows `Admin:Usernames`. A login anywhere else leaves it untouched, so a forged BaseUrl can neither
   grant nor strip admin. Existing sessions last until the cookie expires. A flag set by hand in the database is reset on
   that user's next trusted login (nothing in the code sets it any other way).
3. **`CreateSeriesTransferRequestValidator` lacked the min-below-max rule** the other three validators have. Now shared
   as `AgeRange.IsMinBelowMax`.
4. **ffmpeg time arguments were culture-dependent** (`FfmpegChapterExtractor`): under a comma-decimal culture ffmpeg
   would have received `-ss 12,500`. Now formatted with the invariant culture. The Dockerfile sets no `LANG`, so
   production was probably unaffected; this closes the gap for any other host.
5. **`FfmpegChapterExtractor` had no test seam.** It now runs through `IProcessRunner` (default `SystemProcessRunner`,
   registered in `Program.cs`). Arguments, segment discovery and error mapping are tested with a fake; no unit test
   spawns a real process (checked with a tripwire `ffmpeg` on PATH that fired 0 times).
6. **Non-hermetic tests:** the two ffmpeg tests that ran a real binary are replaced. **The admin usage tests could
   fail once a night** when UTC midnight fell mid-run; they now wait out the last seconds of the day.

**Accepted as designed**
7. **`CardsController.GetCard` / `DeleteCard` do no local ownership check.** They pass a caller-supplied `cardId` to
   Yoto with the caller's own token, so Yoto enforces access, and this app holds no data that could check it locally.
   Revisit if a second Yoto account can ever be reached with one user's token.

**Still open (low)**
8. `CardCapacityCalculator` messages format numbers with the current culture (`{x:F1}`), so a comma-decimal host would
   show "6,0h". Cosmetic; its tests assume a point.
9. `SystemProcessRunner` redirects stdout but never reads it, so a very chatty ffmpeg could in theory block on a full
   pipe. Moved as-is from the original code; not observed.
10. One unit test (`PostgresHealthCheck` false-path) attempts a loopback connection to `127.0.0.1:1`. It needs no
    server and is deterministic (connection refused), but it is not fully hermetic.
