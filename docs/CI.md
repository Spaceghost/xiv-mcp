# CI

Tests and builds run from one script, `tools/ci/run.sh`, whether in GitHub
Actions, on a self-hosted runner or in a local shell. The workflow only sets up
the .NET SDK, restores the NuGet cache and calls it.

## What runs where

| Trigger | Job | Runs | Output |
| --- | --- | --- | --- |
| push to any branch, pull request from a branch of this repository, manual, started by the repository owner | `self-hosted` in `.github/workflows/ci.yml`, `runs-on: [self-hosted, xivmcp-dalamud, fedora-dalamud]` | `tools/ci/run.sh test build` | artifact `xiv-mcp-<sha>` = `artifacts/` (kept 14 days) |
| a run anyone else starts (collaborators, Dependabot), pull request from a fork, any run inside a fork, or anything while `CI_SELF_HOSTED` is `false` | `hosted` in the same workflow, GitHub's `ubuntu-latest` | `tools/ci/run.sh test build` | artifact `xiv-mcp-<sha>` |
| by hand, on your machine or in the Incus build container | `tools/ci/local.sh [stage...]` | the same stages | `artifacts/` where it ran |

Changes that touch only Markdown or `docs/` do not start CI. A newer push to the
same branch or pull request cancels the older run. `permissions: contents: read`,
no secrets, and every third-party action is pinned by commit SHA. Nothing uses
`pull_request_target`.

A fork pull request cannot reach the self-hosted runner. Three independent
guards keep it off:

1. The job's `if` requires
   `github.event.pull_request.head.repo.full_name == github.repository` and
   `!github.event.repository.fork`, so a fork's event never starts it; the
   `hosted` job runs it on GitHub's runner instead.
2. The runner is minted by `ci-dispatchd` on the fedora build host for one queued
   job and destroyed after it. The dispatcher refuses any pull request run whose
   head repository is not this one, whatever the workflow file says, and the
   container sits on an isolated bridge with no route to the LAN, the tailnet or
   the host.
3. Settings → Actions → General → *Approval for running fork pull request
   workflows* is **require approval for all external contributors**.

There is no deployment environment to approve and no secret on the runner.

**Neither runner has a game installation.** Both jobs run
`tools/fetch-dalamud.sh` for the public reference assemblies; the self-hosted
runner's image (`ci-runner-dalamud`) already carries the current release, and
`dalamud-seed` copies it into `.dalamud` only while it is still the live one.
Without them the Dalamud reference assemblies are missing and `tools/ci/run.sh` says so and skips
`tests/XivMcp.Plugin.Tests`, `src/XivMcp.Plugin` and `src/XivMcp.Umbra` instead
of failing. Give a runner `DALAMUD_LIB_PATH` (XIVLauncher's
`~/.xlcore/dalamud/Hooks/dev`) to cover them.

## `tools/ci/run.sh`

```sh
tools/ci/run.sh test            # both test projects
tools/ci/run.sh build           # dotnet build XivMcp.slnx -c Release
tools/ci/run.sh test build      # what CI runs
tools/ci/run.sh all             # test, build, package
```

| Stage | Does |
| --- | --- |
| `deps` | finds a .NET SDK that satisfies `global.json` (`dotnet --version` in the repository root is the test), reports whether the Dalamud reference assemblies are there, then `dotnet restore XivMcp.slnx` |
| `test` | `dotnet test tests/XivMcp.Core.Tests -c Release` and `tests/XivMcp.Standalone.Tests` (Lumina from NuGet, no Dalamud, runs everywhere), then `tests/XivMcp.Plugin.Tests` unless the Dalamud reference assemblies are missing or `SKIP_PLUGIN_TESTS=1`. Writes `artifacts/test-results/<project>.trx`, a `<project>.log` and a plain-text `summary.txt` |
| `build` | `dotnet build XivMcp.slnx -c Release`, then `xiv-mcp-catalog check`: the generated `docs/tools.json`, `docs/TOOLS.md` and README table must match the built plugin (skipped without Dalamud). Without the Dalamud reference assemblies the three Dalamud projects have unresolvable reference paths, so the stage builds `src/XivMcp.Core`, `src/XivMcp.DevHost`, `tools/catalog` and `tests/XivMcp.Core.Tests` and names what it left out |
| `package` | nothing yet: this repository has no `tools/package.sh`. `tools/install-dev.sh` stages a dev plugin for the local game, which is not a release artifact. The stage says so and does nothing; it runs `tools/package.sh` once one exists |
| `all` | `test`, `build`, `package` |

Environment (nothing else configures it):

| Variable | Default | Meaning |
| --- | --- | --- |
| `DOTNET` | `dotnet` on `PATH` if it satisfies `global.json`, else `~/.dotnet/dotnet` | the .NET 10 SDK. `global.json` pins 10.0.401 with `rollForward: latestFeature`, so 10.0.4xx works and 10.0.1xx does not |
| `DALAMUD_LIB_PATH` | `~/.xlcore/dalamud/Hooks/dev` | Dalamud reference assemblies (the directory with `Dalamud.dll`). Missing: the plugin tests and the Dalamud projects are skipped, as `SKIP_PLUGIN_TESTS=1` |
| `UMBRA_LIB_PATH` | the newest `~/.xlcore/installedPlugins/Umbra/<version>` | Umbra reference assemblies for `src/XivMcp.Umbra` |
| `SKIP_PLUGIN_TESTS` | | `1` skips `tests/XivMcp.Plugin.Tests` even where Dalamud is present |
| `CI_CACHE_DIR` | `${XDG_CACHE_HOME:-~/.cache}/xiv-mcp-ci` | cache root; `NUGET_PACKAGES` is `$CI_CACHE_DIR/nuget`, which is what the workflow caches |
| `XIVMCP_ARTIFACTS` | `<repo>/artifacts` | MSBuild output path (`Directory.Build.props`). CI keeps it inside the checkout — it is git-ignored — so the artifact upload can reach it; a plain `dotnet build` still writes `../xiv-mcp-build/artifacts` |
| `CI_TRX` | `1` | `0` turns off the `.trx` reports; the plain-text summary is written either way |

Nothing in CI needs a token, a secret or network access beyond NuGet.

## Choosing the runner

Set repository variables (Settings → Secrets and variables → Actions →
Variables); no YAML change is needed:

| Variable | Effect |
| --- | --- |
| `CI_SELF_HOSTED` unset or anything but `false` | this repository's own runs use the `self-hosted` job; fork pull requests use `hosted` |
| `CI_SELF_HOSTED` = `false` | every run uses the `hosted` job (the build host is down, say) |
| `CI_SELF_HOSTED_RUNS_ON` | the `self-hosted` job's labels as a JSON array, default `["self-hosted","xivmcp-dalamud","fedora-dalamud"]` |
| `CI_RUNS_ON` unset | the `hosted` job runs on GitHub's `ubuntu-latest` |
| `CI_RUNS_ON` = `"ubuntu-24.04"` | a specific GitHub-hosted image |

```sh
gh variable set CI_SELF_HOSTED --body false   # GitHub-hosted only
gh variable delete CI_SELF_HOSTED             # back to the build host
gh variable set CI_RUNS_ON --body '"ubuntu-24.04"'
```

A self-hosted runner executes whatever a workflow in this repository asks for.
The labels are the build host's: `xivmcp-dalamud` is this repository's
registration with `ci-dispatchd`, and `fedora-dalamud` picks the
`ci-runner-dalamud` image. A job only waits for a runner while its labels match
a registration, so without one the job queues; set `CI_SELF_HOSTED` to `false`
then.

## Running it locally

```sh
tools/ci/local.sh                 # = tools/ci/local.sh test build
tools/ci/local.sh test
CI_LOCAL_REMOTE=0 tools/ci/local.sh test build    # force this machine
```

A machine that runs the game has little memory to spare for a build, so
`local.sh` can run the stages in an Incus container instead: set `INCUS_REMOTE`
(and `CI_CONTAINER`, default `xiv-mcp-build`) and it pushes the checkout — `git
ls-files --cached --others --exclude-standard`, through `tar` — runs the same
`tools/ci/run.sh` stages there and streams the output back. Nothing is copied
back and nothing is installed into the game. With no `INCUS_REMOTE` it runs here.
`CI_LOCAL_REMOTE=0`, `INCUS`, `CI_CONTAINER` and `CI_REMOTE_DIR` are the only
knobs. A container without reference assemblies skips the Dalamud projects and
the plugin tests exactly as a GitHub-hosted runner without them does; point
`DALAMUD_LIB_PATH` at reference assemblies inside it (or run on a machine with
`~/.xlcore/dalamud/Hooks/dev`) to cover them.

## What is and is not verified

Actually run:

* `shellcheck -x tools/*.sh tools/ci/*.sh` — clean.
* `actionlint` over `.github/workflows/` — clean.
* `tools/ci/run.sh --help` exits 0; an unknown stage and no stage at all exit 2.
* `tools/ci/run.sh all` in a .NET 10.0.401 container with the Dalamud and Umbra
  reference assemblies mounted in, i.e. the full path including the Dalamud
  projects: `tests/XivMcp.Core.Tests` **222 passed, 0 failed**,
  `tests/XivMcp.Plugin.Tests` **345 passed, 0 failed**, `dotnet build
  XivMcp.slnx -c Release` clean with **0 warnings** under the quality gate, and
  `tools/package.sh` wrote `latest.zip` and `pluginmaster.json`.
* The same run with no Umbra assemblies reachable: both test projects still pass,
  and the build leaves `src/XivMcp.Umbra` out with a message instead of failing —
  Umbra ships as a Dalamud plugin, so a hosted runner cannot fetch it.
* `tools/check-manifest.py` exits 0.

Not verified:

* Nothing here has run on GitHub: the workflows, the NuGet cache, the artifact
  upload, the runner labels, the `CI_RUNS_ON` / `CI_SELF_HOSTED` variables, the
  nightly fuzz schedule and the release workflow are only checked by
  `actionlint`. The first run on GitHub is their test.
* `tools/fetch-dalamud.sh` downloads from goatcorp's distribution; that download
  has not been exercised here (the reference assemblies came from a local
  XIVLauncher install), so the hosted job's Dalamud step is unproven.
* No part of CI loads the plugin in FINAL FANTASY XIV, and nothing below the
  build has been observed running in the game.
