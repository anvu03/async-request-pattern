#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -P "$(dirname "$0")/.." && pwd)

compose() {
    docker compose --project-directory "$ROOT" -f "$ROOT/compose.yaml" "$@"
}

"$ROOT/scripts/start-infra.sh"
"$ROOT/scripts/migrate.sh"
compose --profile apps up -d --build api worker

api_binding=$(compose --profile apps port api 8080)
worker_binding=$(compose --profile apps port worker 8080)
api_port=${api_binding##*:}
worker_port=${worker_binding##*:}

if command -v curl >/dev/null 2>&1; then
    wait_for_url() {
        name=$1
        url=$2
        attempts=90
        while [ "$attempts" -gt 0 ]; do
            if curl --fail --silent --max-time 2 "$url" >/dev/null 2>&1; then
                return 0
            fi
            attempts=$((attempts - 1))
            sleep 2
        done
        printf '%s did not become ready at %s\n' "$name" "$url" >&2
        compose --profile apps logs api worker >&2
        return 1
    }

    wait_for_url 'API liveness' "http://localhost:$api_port/alive"
    wait_for_url 'API readiness' "http://localhost:$api_port/health"
    wait_for_url 'Worker liveness' "http://localhost:$worker_port/alive"
    wait_for_url 'Worker readiness' "http://localhost:$worker_port/health"
else
    compose --profile apps run --rm --no-deps emulator-health \
        --fail --silent --show-error --retry 90 --retry-delay 2 --retry-connrefused --max-time 2 \
        http://api:8080/alive http://api:8080/health \
        http://worker:8080/alive http://worker:8080/health
fi

printf '%s\n' 'SQL Server, Service Bus emulator, API, and worker are ready.'
