#!/bin/bash
# Safe local-only entry point. Never builds, launches, installs or cleans an app.
# Output must not exist; explicit --version and --revision are required.
set -euo pipefail
SCRIPT_DIRECTORY="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)"
exec python3 -B "$SCRIPT_DIRECTORY/create_local_app_bundle.py" "$@"
