# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Read `AGENTS.md` (scope, safety invariants, compatibility pins, workflow) and `GLOSSARY.md` before exploring. This file only adds commands and architecture.

A Jellyfin 12.0.0 server plugin (`net10.0`) that turns existing item metadata into normalized tags like `meta:genre:science-fiction`.

## Commands

Run from the repo root.

```bash
./scripts/build-and-test.sh          # full verification: public-tree check, Node, Python, then C# tests in Release
./scripts/build.sh                   # Release build only
./scripts/check-dashboard.sh         # fast loop for configPage.html: Node dashboard tests + C# PluginTests
./scripts/dev.sh                     # build, install into disposable Jellyfin (port 8099), watch and reload
./scripts/test-release-lifecycle.sh  # packaging/lifecycle changes (needs Docker/OrbStack)
```

Single tests:

```bash
dotnet test Jellyfin.Plugin.MetaTagger.Tests --configuration Release --filter 'FullyQualifiedName~MetaTaggerRunnerTests'
node --test scripts/tests/dashboard.test.mjs
python3 -m unittest discover -s scripts/tests -p 'test_agent_dispatch.py'
```

Proxy tests bind loopback ports and need local socket access.

## Architecture

- **Wiring**: `PluginServiceRegistrator` registers the services as singletons, plus the scheduled tasks (all on `MetaTaggerScheduledTaskBase`) and `LibraryPostScanTask`. `MetaTaggerDashboardController` is the dashboard's REST API.
- **Dashboard**: one embedded `Configuration/configPage.html`, tested without Jellyfin via Node (`scripts/tests/dashboard.test.mjs`) and in a real browser via Playwright (`scripts/browser/`).
- **Jellyfin seam**: `IMetaTaggerHost` / `JellyfinMetaTaggerHost` wraps Jellyfin's library and item APIs. Tests fake it.
- **Runner**: `MetaTaggerRunner` is a large partial class split by concern (`.Generation`, `.History`, `.ItemEvaluation`, `.ItemExecution`, `.ItemRuns`, `.Items`). It owns library-wide and single-item runs, budgets, resumable cursors, and cancellation. `ItemExecutionContractTests` covers the shared item execution path.
- **State**: `MetaTaggerStateStore` persists `meta-tagger-state.json` and `last-run-summary.json`.
