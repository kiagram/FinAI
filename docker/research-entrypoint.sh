#!/usr/bin/env bash
# Starts JupyterLab against a host-mounted notebook directory, wired up so
# `%run start.py` / QuantBook() behave exactly as they do in SETUP.md.
set -euo pipefail

LEAN_BIN=/FinAI/Launcher/bin/Release
NOTEBOOKS=/FinAI/Notebooks

mkdir -p "$NOTEBOOKS"

# start.py locates QuantConnect.Lean.Launcher.runtimeconfig.json via
# realpath(__file__), so symlinking it into the notebook directory is the
# supported way to keep `%run start.py` working from outside bin/Release.
ln -sf "$LEAN_BIN/start.py" "$NOTEBOOKS/start.py"

# Initializer.Start() reads config.json from the kernel's working directory,
# which is the notebook directory, not bin/Release.
if [ ! -f "$NOTEBOOKS/config.json" ]; then
  cat > "$NOTEBOOKS/config.json" <<JSON
{
  "composer-dll-directory": "$LEAN_BIN",
  "data-folder": "/FinAI/Data/",
  "algorithm-language": "Python",
  "messaging-handler": "QuantConnect.Messaging.Messaging",
  "job-queue-handler": "QuantConnect.Queues.JobQueue",
  "api-handler": "QuantConnect.Api.Api",
  "map-file-provider": "QuantConnect.Data.Auxiliary.LocalDiskMapFileProvider",
  "factor-file-provider": "QuantConnect.Data.Auxiliary.LocalDiskFactorFileProvider",
  "data-provider": "QuantConnect.Lean.Engine.DataFeeds.DefaultDataProvider",
  "object-store": "QuantConnect.Lean.Engine.Storage.LocalObjectStore"
}
JSON
fi

# No token: compose publishes this on 127.0.0.1 only.
exec jupyter lab --ip=0.0.0.0 --port=8888 --no-browser --allow-root \
  --notebook-dir="$NOTEBOOKS" --ServerApp.token='' --ServerApp.password=''
