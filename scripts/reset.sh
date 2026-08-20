#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -P "$(dirname "$0")/.." && pwd)

if [ "$#" -ne 1 ] || [ "$1" != "--yes" ]; then
    printf '%s\n' 'Usage: scripts/reset.sh --yes' >&2
    printf '%s\n' 'This permanently deletes local SQL Server data.' >&2
    exit 2
fi

docker compose --project-directory "$ROOT" -f "$ROOT/compose.yaml" down --volumes --remove-orphans
"$ROOT/scripts/start-infra.sh"
"$ROOT/scripts/migrate.sh"
