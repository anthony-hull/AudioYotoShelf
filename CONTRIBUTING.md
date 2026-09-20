# Contributing to AudioYotoShelf

Thanks for contributing! This guide covers local setup, the test/lint gates CI
enforces, and the PR workflow.

## Prerequisites

Versions are pinned in source so they stay verifiable — install what these files declare:

| Tool | Required version | Source of truth |
|------|------------------|-----------------|
| .NET SDK | `net10.0` (SDK 10.0.x) | [`Directory.Build.props`](Directory.Build.props), [`global.json`](global.json) |
| Node.js | see file (currently 24) | [`.nvmrc`](.nvmrc) |
| C# language | `14` | [`Directory.Build.props`](Directory.Build.props) |
| Backend packages | central versions | [`Directory.Packages.props`](Directory.Packages.props) |
| Frontend packages | see file | [`src/AudioYotoShelf.ClientApp/package.json`](src/AudioYotoShelf.ClientApp/package.json) |

With [nvm](https://github.com/nvm-sh/nvm): `nvm use` reads `.nvmrc`.

PostgreSQL 17 and Redis 7 are required at runtime — easiest via `docker compose up -d postgres redis`.

## Local development

### Backend

```bash
cd src/AudioYotoShelf.Api
dotnet run
```

### Frontend

```bash
cd src/AudioYotoShelf.ClientApp
nvm use            # or ensure Node matches .nvmrc
npm install
npm run dev
```

The Vite dev server runs on port 5173 and proxies `/api` and `/hubs` to the
.NET backend on port 5000.

## Before you open a PR

CI runs these exact gates (see [`.github/workflows/ci.yml`](.github/workflows/ci.yml)).
Run them locally first:

```bash
# Backend
dotnet format AudioYotoShelf.sln --verify-no-changes   # format gate
dotnet build AudioYotoShelf.sln -c Release             # warnings are errors
dotnet test                                            # all tests must pass
dotnet stryker --config-file stryker-config.core.json   # mutation gate for Core; see docs/mutation-testing.md

# Frontend
cd src/AudioYotoShelf.ClientApp
npm run lint
npm run format:check
npm run type-check
npm run build
```

To auto-fix formatting: `dotnet format` (backend) and `npm run lint:fix && npm run format` (frontend).

> Note: the C# "unused using" check (IDE0005) is only reported by some SDK
> patch versions locally, but CI always enforces it. Run `dotnet format` before
> pushing to be safe.

## Branch & commit conventions

- **Branches:** `feature/<slug>`, `fix/<slug>`, `chore/<slug>`, or `docs/<slug>`
- **Commits:** [Conventional Commits](https://www.conventionalcommits.org/) —
  `feat:`, `fix:`, `chore:`, `ci:`, `docs:`, `refactor:`, `test:`
- Keep PRs focused; fill out the PR template and link related issues.

## Project layout

| Path | Purpose |
|------|---------|
| `src/AudioYotoShelf.Api` | ASP.NET Core API, controllers, SignalR hub, DI composition |
| `src/AudioYotoShelf.Core` | Domain entities, interfaces, DTOs (no infrastructure deps) |
| `src/AudioYotoShelf.Infrastructure` | EF Core, external API clients (ABS/Yoto/Gemini), services, background jobs |
| `src/AudioYotoShelf.ClientApp` | Vue 3 + TypeScript SPA |
| `tests/` | xUnit test projects mirroring the source projects |

## Reporting bugs / requesting features

Use the [issue templates](.github/ISSUE_TEMPLATE). For security issues, see
[SECURITY.md](SECURITY.md) — do not open a public issue.
