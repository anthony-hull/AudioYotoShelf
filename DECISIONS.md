# Decisions

Architectural decisions and hard-won constraints for the homelab fork. Grep this before touching an area.

## 2026-09-23 — A downloaded track is uploaded to Yoto as the content type Audiobookshelf served it as

**Decision.** `TransferOrchestrator.DownloadToFileAsync` now downloads through `DownloadAudioFileWithMetadataAsync`
and returns the real content type, which `UploadTracksAsync` sends to Yoto instead of a hardcoded `"audio/mpeg"`.
`PlaylistTransferOrchestrator.ContentTypeFor` now maps `.ogg`/`.opus` to `audio/ogg`, `.flac` to `audio/flac` and
`.wav` to `audio/wav`, instead of folding everything that isn't m4a/m4b/mp4/aac into `audio/mpeg`.

**Why (measured live on `pablo`, card `7NQ2D`, 2026-09-23).** A 17-file "Harry Potter and the Philosopher's Stone"
transfer played each track for about a second on the Yoto app before skipping to the next. The server log for that
transfer read `Streaming audio file 36441 from item f648c8fd-...: 24041522 bytes, audio/ogg` — Audiobookshelf serves
this library's audio as Opus-in-Ogg — but `UploadTracksAsync`'s direct-download branch declared every such track
`audio/mpeg` to Yoto's upload regardless of source format. Yoto's transcoder is told what the bytes are, not shown
them (`Content-Type` on a presigned PUT), so it decoded an Ogg/Opus stream as MP3.

**The fix was already half-written.** `IAudiobookshelfService.DownloadAudioFileWithMetadataAsync` — same download,
plus the real `Content-Type` — existed with its own tests (`AudiobookshelfServiceTests`) but was never called from
either orchestrator; both called the plain `DownloadAudioFileAsync`, which discards the header. An existing test,
`UploadTracks_WholeFileTrack_IsDownloadedFirstAndUploadedAsMpeg_...`, pinned the wrong behaviour as intended; it is
renamed and now asserts pass-through. `PlaylistTransferOrchestratorPipelineTests`' content-type theory had the same
`.ogg` row pinned to `audio/mpeg`.

**What this does not cover.** The card already on Yoto (`7NQ2D`) is not fixed by this change — it needs
re-transferring once deployed. Extracted chapter files (ffmpeg's own m4a output) and merged/split playlist files
are unaffected; they were never guessing at the source format.


## 2026-09-21 — A single-file book's chapter files are deleted when its transfer ends

**Decision.** `TransferBookAsync` deletes the chapter files it extracted in its `finally`, whether the transfer completed,
failed or was cancelled. `BuildTrackMappingsAsync` deletes the ones already extracted if it fails part-way, because it
never returns their paths to the caller.

**Why (measured live, 2026-09-21).** After cancelling a 9-chapter single-file book on `pablo`, `/app/temp` still held nine
`chapter_<guid>.m4a` files (70 MB). The transfer's own clean-up removes only files named `<transferId>*`, and
`FfmpegChapterExtractor` names its output `chapter_<guid>`, so those files were never matched. The only `File.Delete` calls
in `homelab-4` were the ones for the ffmpeg concat list, the playlist orchestrator and that glob, so a completed single-file
book leaked the same way. That is read from the code; only the cancelled case was observed running.

**What it does not cover.** Files from `TransferPlaylistAsync` (its own clean-up, not looked at here) and any file left by
a process killed mid-transfer: the container losing power still leaves the volume as it was.

**Test note.** The shared orchestrator test fixture used to hand every chapter of every transfer the same temp file. It now
creates one per call, as ffmpeg does; with real clean-up in place a second transfer in the same test found the file gone.

## 2026-09-20 — Cancelling a transfer stops it between tracks, and is not a failure

**Decision.** The per-track loop looks for a cancel before each track, and a person's cancel ends the Hangfire job
quietly. A cancelled token (the server stopping) still propagates, so that job is requeued.

**Why (measured on a live 17-track book).** Cancel writes `Cancelled` to the transfer and nothing else; the running job
looked only when its status next changed, which is after every track. It carried on for about 50 minutes, then threw
"Transfer was cancelled". The job wrapper rethrew that as a failure, so Hangfire's automatic retry
(`Retry attempt 1 of 1`) restarted the transfer 29 seconds later.

**Limit.** A track already with Yoto cannot be cancelled there; its transcode finishes (about three minutes) and the
transfer stops before the next one. Interrupting mid-transcode would need the poll loop to read the database, which it
does not have a context for, and was not worth the machinery.

## 2026-09-20 — Connect to Audiobookshelf by single sign-on, through ABS's own API-client OpenID flow

**Decision.** The app drives Audiobookshelf's OpenID flow for API clients (the one its mobile app uses) from the
server: `GET /auth/openid?response_type=code&redirect_uri&state&code_challenge` returns a 302 to the identity provider;
the browser signs in; the app then calls `/auth/openid/callback` with the `code_verifier` and returns the person's own
ABS access and refresh tokens. Nothing is typed and no API key changes hands. Offered only when `Audiobookshelf:Url`
is set. The API-key and password paths are untouched.

**Why not trust the identity headers Traefik forwards.** They are identity, not a credential (the app still needs a
token to call ABS as that person), and any container on the proxy network can reach the app directly and set them.

**Constraints found by measuring, not assuming:**
- **ABS builds the identity provider's return address from the request's `Host`.** Called by its container name it
  answers `redirect_uri=http://audiobookshelf/...`, which no browser can reach. `Audiobookshelf:PublicUrl` makes step 1
  present the public host and `X-Forwarded-Proto`. Only that call needs it: ABS keeps the result in its session.
- **The code exchange returns 400 "No session" without the ABS session cookies from step 1.** They are kept with the
  PKCE verifier in Redis (`abs-sso:<state>`, 5 minutes, single use), not in Postgres.
- **The callback address must be in ABS *Allowed Mobile Redirect URIs*, character for character. Never `*`:** with a
  single `*` ABS accepts any redirect address.
- **The attempt is bound to the browser that started it** by an HttpOnly, SameSite=Lax nonce cookie (`ays_sso`), so a
  link cannot finish someone else's attempt and sign the follower in as them (login CSRF). Lax, not Strict, because the
  browser returns from the provider on a cross-site navigation.
- **Match Existing By = email in ABS** links a hand-made account on first sign-in; it is safe only while the identity
  provider does not let people edit their own email.
- **A refused, expired, replayed or wrong-browser attempt redirects to `/setup?sso=expired|unavailable`**, not to a bare
  400 page: a refresh or double-click looks exactly like a replay. `App.vue` must not push `/setup` when already there
  (it did, and dropped the query, so the notice never showed).

## 2026-09-20 — Ask Yoto for the content and icon scopes

**Decision.** The authorize request asks for `profile offline_access openid user:content:manage user:content:view
user:icons:manage`.

**Why.** Yoto gives a client that does not ask only `user:account:view`, and then refuses the first upload call with
`403 "User does not have required scope(s): 'user:content:manage'"`. Measured on a live token, not inferred. Upstream
requests the short list too.

**Open gap.** A refresh does not add scopes, and the setup screen offers "Authorize with Yoto" only once the stored token
has expired, so anyone who authorized before this change has to have the token cleared to re-authorize.

## 2026-09-20 — A transfer reports each track, and reads Yoto's own progress

**Decision.** The live update says which track it concerns, what stage that track is at (downloading, uploading,
transcoding, uploaded, already on Yoto) and, while it transcodes, Yoto's percentage. The client keeps state per track.

**Why.** Yoto reports its transcode under `transcode.progress { phase, percent }`; the `status` field the app used to
read is always null, so a track's progress sat still for about three minutes and looked hung. Tracks go one after
another, so a 17-track book takes on the order of 50 minutes. Whether Yoto accepts several transcodes at once has not
been tested. Progress is reported with `InlineProgress`, not `Progress<T>`, which posts to the thread pool and can
reorder reports.

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
