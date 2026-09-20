# Decisions

Architectural decisions and hard-won constraints for the homelab fork. Grep this before touching an area.

## 2026-09-20 — Mutation testing with Stryker.NET, two gates, unit tests only

**Decision.** Stryker.NET 4.14.0 (pinned in `dotnet-tools.json`). Two configs: a strict gate on `AudioYotoShelf.Core`
(`stryker-config.core.json`, break 95%) and a whole-solution floor (`stryker-config.json`, break 90%).

**Why two.** One whole-solution number is dominated by `Infrastructure`/`Api` code that only the integration suite
reaches, so a bar high enough to protect Core would fail permanently and one low enough to pass would protect
nothing. Baseline 2026-09-20: whole solution 32.13% at setup (272 unit tests), 94.56% after the follow-up test work (971 tests); Core 67.57% then 99.58%.

**Unit tests only.** The Testcontainers suite is not run under Stryker (reasoned to be too slow per mutant; not
measured). Integration-only code therefore shows as `NoCoverage`; see `docs/mutation-testing.md` §3.

**Excluded, and why:** migrations, `Program.cs`, EF configurations, `DbContext`, observability. Reasons and the
methods Stryker cannot mutate are in `docs/mutation-testing.md`.

**Found on the way.** `AgeSuggestionService` had an unreachable "no signals, use default range" branch (the duration
signal is always added), and the test named for it asserted loose ranges that never exercised it. The dead branch was
deleted; the test now asserts what the code does (the duration bucket).

**Rollback.** Delete `stryker-config*.json`, `dotnet-tools.json`, `.github/workflows/mutation.yml`; nothing in `src/`
depends on them. The source refactors are behaviour-preserving and stay.

## 2026-09-20 — Open findings from mutation testing (found, NOT fixed)

Each is confirmed by reading the code at the cited place. None was changed, because each is a behaviour or design
decision rather than a test gap. Delete an entry when it is fixed or decided.

1. **`/api/health` reports Postgres healthy when it is down** — `HealthController.cs:25` awaits
   `db.Database.CanConnectAsync(ct)` and discards the returned bool. That call returns `false` on an unreachable
   database instead of throwing, so the catch is never reached. Measured by a throwaway test against a closed
   loopback port: HTTP 200 with `"postgres":{"status":"healthy"}`. The Dockerfile `HEALTHCHECK` curls this endpoint.
   `PostgresHealthCheck` (`Health/HealthChecks.cs:16`, behind `/health/ready`) does it correctly and is tested.
   One-line fix: check the bool. **Not applied because it changes operations:** the container would go unhealthy
   during a database outage, and Traefik skips unhealthy containers, so the whole app would drop out of routing.
2. **Admin rights are granted but never revoked** — `AuthController.cs` ~86-102 sets `IsAdmin = true` for an
   allow-listed username and never clears it, and the session check is `IsAdmin && fromAdminServer`. Removing a name
   from `Admin:Usernames` does not remove admin for someone who already has the stored flag. Impact here is small
   (the admin page shows usage figures). A test pinning the current behaviour was deliberately not kept.
3. **`CreateSeriesTransferRequestValidator` has no min-less-than-max rule**; the single, batch and settings
   validators do. Only the 0-18 bounds are checked for series transfers.
4. **`CardsController.GetCard` and `DeleteCard` do no local ownership check.** They pass a caller-supplied `cardId`
   to Yoto with the caller's own token, so Yoto enforces access. Sound while that holds; nothing here would notice if it did not.
5. **`FfmpegChapterExtractor` has no test seam** for the `Process` it spawns (see `docs/mutation-testing.md` §6).
6. **Three unit tests are not hermetic:** two ffmpeg validation tests run the real `ffmpeg` if it is installed
   (they assert only "not an `ArgumentException`", so they pass either way), and the `PostgresHealthCheck` false-path
   test attempts a loopback connection to `127.0.0.1:1`.
7. **Possible midnight flake:** the admin usage tests derive "today" from `DateTime.UtcNow`, so a run that straddles
   UTC midnight could fail. The risk is tiny.
