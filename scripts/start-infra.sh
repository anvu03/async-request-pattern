#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -P "$(dirname "$0")/.." && pwd)

compose() {
    docker compose --project-directory "$ROOT" -f "$ROOT/compose.yaml" "$@"
}

compose up -d

gate_id=$(compose ps --all -q emulator-health)
if [ -z "$gate_id" ]; then
    printf '%s\n' 'Service Bus health gate container was not created.' >&2
    exit 1
fi

docker wait "$gate_id" >/dev/null
gate_exit=$(docker inspect --format '{{.State.ExitCode}}' "$gate_id")
if [ "$gate_exit" -ne 0 ]; then
    compose logs emulator emulator-health >&2
    exit "$gate_exit"
fi

printf '%s\n' 'SQL Server and Service Bus emulator are ready.'
