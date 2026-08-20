#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -P "$(dirname "$0")/.." && pwd)

"$ROOT/scripts/start-infra.sh"
"$ROOT/scripts/migrate.sh"

: "${RUN_END_TO_END_TESTS:=true}"
: "${E2E_SQL_CONNECTION_STRING:=Server=localhost,1433;Database=OrdersDb;User ID=orders_app_local;Password=LocalOnly_OrdersDb_2026!;Encrypt=True;TrustServerCertificate=True}"
: "${E2E_SERVICE_BUS_CONNECTION_STRING:=Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;}"
: "${E2E_SERVICE_BUS_MANAGEMENT_CONNECTION_STRING:=Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;}"
: "${E2E_SERVICE_BUS_QUEUE_NAME:=orders}"
export RUN_END_TO_END_TESTS E2E_SQL_CONNECTION_STRING
export E2E_SERVICE_BUS_CONNECTION_STRING E2E_SERVICE_BUS_MANAGEMENT_CONNECTION_STRING
export E2E_SERVICE_BUS_QUEUE_NAME

dotnet test --solution "$ROOT/AzureBusService.sln" "$@"
