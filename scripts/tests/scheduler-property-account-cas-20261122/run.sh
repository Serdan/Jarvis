#!/bin/sh
set -eu
HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
GENERATED="$HERE/Generated.NativeReservation.Property.fs"
# The F# generator pins source and original fixture Git blobs, validates the
# accepted immutable ancestor, and refuses to overwrite any generated file.
if [ "$#" -gt 0 ]; then
  dotnet fsi --exec "$HERE/prepare.fsx" "$1"
else
  dotnet fsi --exec "$HERE/prepare.fsx"
fi
trap 'rm -f "$GENERATED"' EXIT HUP INT TERM
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet restore "$HERE/PropertyHarness.fsproj" --ignore-failed-sources
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet test "$HERE/PropertyHarness.fsproj" \
  --configuration Release --no-restore \
  --filter "FullyQualifiedName~SchedulerAccountCasProperty" \
  --logger "trx;LogFileName=scheduler-account-cas-property.trx" \
  --results-directory "$HERE/evidence" \
  --logger "console;verbosity=normal"
