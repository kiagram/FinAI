# FinAI (Lean) — local setup

Everything installs under `$HOME`; nothing required an admin password.

## Activate

    source ~/FinAI/env.sh

This is required for every capability below — it sets `DOTNET_ROOT`, puts the
.NET 10 SDK on PATH, and sets `PYTHONNET_PYDLL` (mandatory: without it Lean's
Python support fails with `BadPythonDllException`).

`env.sh` is not in the repository: it hard-codes the paths of one machine's
toolchain, so it is written per machine rather than checked in. The "Versions"
section at the end lists what it points at. Docker needs no equivalent — see
[Docker](#docker).

## Capabilities

| Capability | Command | Status |
|---|---|---|
| Build | `dotnet build QuantConnect.Lean.sln -c Release` | 0 errors |
| Web app | see [Web app](#web-app) | works |
| Backtest (Python) | `cd Launcher/bin/Release && dotnet QuantConnect.Lean.Launcher.dll` | works |
| Backtest (C#) | set `algorithm-language: CSharp` in Launcher/config.json | works |
| HTML report | see below | works |
| Research notebooks | `cd Launcher/bin/Release && jupyter lab` | works |
| Parameter optimization | `cd Optimizer.Launcher/bin/Release && dotnet QuantConnect.Optimizer.Launcher.dll` | works |

### Web app

The browser front end for backtests: pick an algorithm, set its parameters, run
it, and read the statistics and equity curve back.

    cd Web/bin/Release
    ASPNETCORE_URLS=http://127.0.0.1:5099 dotnet QuantConnect.FinAI.Web.dll

`Web/` references no LEAN project. It runs each backtest as a child
`QuantConnect.Lean.Launcher` process against a generated per-job config and
reads the results off disk, which is what lets it treat the exit-134 crash
below as a non-event: **success is decided by the `*-summary.json` file, never
by the exit code.**

Verified against the numbers already in this document — `ema-cross` with
`ema-fast:20, ema-slow:100` reports Sharpe 27.907, the same value the optimizer
converges on.

#### Algorithms are a fixed allow-list

`Web/catalog.json` names the algorithms the service will run, and the API takes
an algorithm *id*, never a path. Accepting an `algorithm-location` from the
network would be remote code execution, so a new algorithm means a catalog
entry and a redeploy. Parameters are likewise only accepted if the entry
declares them, and only inside the declared range.

Add an algorithm by appending to the catalog; entries whose `location` does not
resolve to a file are dropped at startup with a warning rather than offered and
then failing.

#### Configuration

Every setting is bound from the `FinAI` section, so each is also a
`FinAI__<Name>` environment variable. Paths are relative to the repository root.

| Variable | Default | Notes |
|---|---|---|
| `FinAI__AccessToken` | *(empty)* | When set, `/api` requires it as a Bearer or `X-FinAI-Token` header. **Set this before exposing the service.** |
| `FinAI__MaxConcurrency` | `2` | Backtests running at once. |
| `FinAI__MaxQueueDepth` | `32` | Submissions beyond this get 503 rather than an unbounded wait. |
| `FinAI__Timeout` | `00:10:00` | A run exceeding this is killed. |
| `FinAI__LauncherDirectory` | `Launcher/bin/Release` | Must contain a built launcher; the service refuses to start otherwise. |
| `FinAI__DataFolder` | `Data` | |
| `FinAI__ResultsRoot` | `Web/results` | One directory per job: config, engine log, LEAN's result files. |

#### API

| Endpoint | Purpose |
|---|---|
| `GET /healthz` | Unauthenticated liveness check. |
| `GET /api/algorithms` | The catalog, with each algorithm's declared parameters. |
| `POST /api/backtests` | `{"algorithmId": "...", "parameters": {...}}` → 202 with a job id. |
| `GET /api/backtests` | Recent runs, newest first. |
| `GET /api/backtests/{id}` | Status, statistics, equity curve. |
| `GET /api/backtests/{id}/log` | Tail of the engine log. |

### HTML report

    cd Report/bin/Release
    dotnet QuantConnect.Report.dll \
      --backtest-data-source-file ../../../Launcher/bin/Release/BasicTemplateFrameworkAlgorithm.json \
      --report-destination report.html --strategy-name "My Strategy"

### Research notebooks

Open `Launcher/bin/Release/FinAI_Research.ipynb`, select the
**FinAI (Lean Research)** kernel. Notebooks start with `%run start.py`, which
loads the Lean assemblies and gives you `QuantBook()`.

The kernel has the required env vars baked into its kernelspec, so notebooks
work without sourcing env.sh.

## Docker

Container parity with everything above, and the way to run FinAI on a machine
that has not been through the `env.sh` setup. Everything lives in `docker/`.

    cd docker
    docker compose build            # ~5 min; 4.8 GB image

Every service below bind-mounts `Data/` and `Algorithm.Python/` from this
checkout, so all of them depend on the Docker daemon being able to see it.
Check that first — see [When the mounts arrive
empty](#when-the-mounts-arrive-empty), because the failure does not look like a
mount problem.

| Capability | Command | Status |
|---|---|---|
| Web app | `docker compose up web` → http://127.0.0.1:8080 | works |
| Backtest (Python) | `docker compose run --rm backtest` | works, exits 134 |
| Backtest (C#) | `docker compose run --rm backtest --algorithm-language CSharp --algorithm-location QuantConnect.Algorithm.CSharp.dll` | works, exits 0 |
| HTML report | `docker compose run --rm report` | works |
| Research notebooks | `docker compose up research` → http://127.0.0.1:8888 | works |
| Parameter optimization | `docker compose run --rm optimize` | works, Failed:0 across 5 runs |

Verified against the bundled sample data: both backtests reproduce the host
numbers exactly (3 orders, 1.655% net profit, Sharpe 8.472), the optimizer
picks `ema-fast:20, ema-slow:100` at Sharpe 27.907, and `QuantBook()` returns
real SPY bars for October 2013.

### When the mounts arrive empty

**Symptom.** `docker compose build` and `up` both succeed, the container starts
and reports healthy, and then the engine behaves as though the repository were
empty: no algorithms in the catalog, `Data/` missing, backtests failing for
reasons that read like application bugs. The web app is loudest about it — it
refuses to boot with *"No catalog entry ... resolved to a file"* — but every
service is affected.

**Cause.** A Docker daemon that runs inside a VM only sees the host paths that
VM was configured to mount. Colima mounts what is listed in
`~/.colima/default/colima.yaml`, and this checkout is not necessarily among
them. When the path is missing Docker does not refuse the mount: it *creates*
the directory inside the VM and bind-mounts that, so an empty directory is
mounted over each one and nothing errors.

**The tell.** Ask the VM what it can see, rather than asking the container:

    docker context show                                  # which daemon is this?
    colima ssh -- ls ~/FinAI                             # real tree, or bare mount points?

If that lists only `Data`, `Algorithm.Python` and `docker` — the mount points
themselves, with nothing inside — the VM cannot see the checkout. Running the
same `ls` against a directory the VM *does* mount returns the real tree, which
makes the contrast unambiguous.

**Fixes.** Either add the checkout to `mounts:` in `~/.colima/default/colima.yaml`
and `colima restart`, or run the services natively per the sections above.
Note that `colima restart` stops every other container on that daemon, so it is
not a free action if the machine is hosting anything else.

Docker Desktop shares `/Users` by default and does not have this problem, but
installing or launching it does not change which daemon the CLI talks to —
`docker context show` decides that, not which app is running. Linux hosts mount
the filesystem directly and are unaffected, which is why this only bites
locally.

### What the container does and does not need

The base image `quantconnect/lean:foundation` already ships .NET 10, Python
3.11, pythonnet and JupyterLab with `PYTHONNET_PYDLL` preset, so there is no
`env.sh` equivalent inside the container and no `BadPythonDllException` to work
around. It has an arm64 manifest, so it runs natively on Apple silicon.

Still no QuantConnect account: `job-user-id` and `api-access-token` stay empty
and every handler resolves to a local-disk implementation. Pulling the base
image is a Docker Hub pull, not an API call.

### Layout

- `Data/`, `Algorithm.Python/` and `Launcher/config.json` are bind-mounted, so
  editing an algorithm or switching `algorithm-type-name` needs no rebuild.
  C# algorithms are compiled into the image, so changing those does. This is
  also what makes the whole stack sensitive to the VM's mount list — see
  [When the mounts arrive empty](#when-the-mounts-arrive-empty).
- Results land on the host in `docker/results/`; the web app keeps its per-job
  directories in `docker/results/web/`.
- The `web` service binds to `127.0.0.1` unless `FINAI_BIND` says otherwise, and
  reads `FINAI_ACCESS_TOKEN` for the API gate. Set both deliberately — the
  service spends minutes of CPU per request, so an open one is a free compute
  faucet.
- Notebooks live on the host in `docker/notebooks/`. The entrypoint symlinks
  `start.py` there and writes a `config.json` pointing `composer-dll-directory`
  at the build output, so `%run start.py` works exactly as it does locally.
- `docker/optimizer.config.json` is the container-path version of the optimizer
  config that otherwise only exists in `Optimizer.Launcher/bin/Release/`.

### `init: true` is load-bearing

Do not remove it from `compose.yml`. The exit-134 teardown crash below raises
`SIGABRT`, and the kernel does not apply default signal actions to PID 1 — so
without a real init the container does not exit 134, it hangs in a futex wait
forever and `docker compose run` never returns.

## Known issue: exit code 134

Python backtests abort during teardown with
`InvalidOperationException: GIL must always be released`, from a pythonnet
finalizer in `PythonInitializer.Shutdown()`.

It fires *after* results are computed and written, so nothing is lost.
Verified: not caused by env config, not fixed by pinning pythonnet 2.0.55,
and C# backtests exit 0. The optimizer is unaffected (it reads result files,
not exit codes: Failed:0 across 6 runs).

Reproduces identically in Docker, so it is not a macOS or local-env artifact.
There it needs `init: true` to surface as an exit code at all — see above.

If you script backtests, gate on output rather than exit status:

    test -f Launcher/bin/Release/<Algo>-summary.json && echo PASS
    test -f docker/results/<Algo>-summary.json && echo PASS   # container

## Not set up (needs your credentials)

- **External market data** — `DownloaderDataProvider/` pulls from vendors that
  require an API key. Bundled `Data/` is small 2013-era sample data only.
- **Live trading brokerages** — require account API keys.

## Versions

.NET SDK 10.0.400 · Python 3.11.16 (uv) · numpy 1.26.4 · pandas 2.3.3 ·
scipy 1.13.1 · scikit-learn 1.6.1 · matplotlib 3.8.4 · QuantConnect.pythonnet 2.0.56
