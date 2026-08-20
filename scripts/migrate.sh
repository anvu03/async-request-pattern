#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -P "$(dirname "$0")/.." && pwd)

compose() {
    docker compose --project-directory "$ROOT" -f "$ROOT/compose.yaml" "$@"
}

compose up -d --wait sql

compose exec -T sql sh -c '
    exec /opt/mssql-tools18/bin/sqlcmd \
        -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b \
        -Q "IF DB_ID(N'\''OrdersDb'\'') IS NULL CREATE DATABASE [OrdersDb];"
'

set -- "$ROOT"/database/migrations/[0-9]*.sql
if [ ! -f "$1" ]; then
    printf '%s\n' 'No numeric SQL migrations found.' >&2
    exit 1
fi

migrations=$(
    for migration do
        basename "$migration"
    done | LC_ALL=C sort -n
)

old_ifs=$IFS
IFS='
'
for migration in $migrations; do
    printf 'Applying %s\n' "$migration"
    compose exec -T sql sh -c '
        exec /opt/mssql-tools18/bin/sqlcmd \
            -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -I -b \
            -d OrdersDb -i "$1"
    ' sh "/workspace/database/migrations/$migration"
done
IFS=$old_ifs

printf '%s\n' 'Applying local application login'
compose exec -T sql sh -c '
    exec /opt/mssql-tools18/bin/sqlcmd \
        -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -I -b \
        -d OrdersDb -i /workspace/database/local/app_login.sql
'
