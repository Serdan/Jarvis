#!/bin/sh
set -eu
HERE=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
dotnet fsi --exec "$HERE/prepare.fsx"
dotnet test "$HERE/AutomationNativeAccountAuthority20261123.fsproj" -c Release --filter 'FullyQualifiedName~AutomationNativeAccountAuthorityTests|FullyQualifiedName~NativeSchedulerRosterPolicyTests|FullyQualifiedName~NativeSchedulerAccountReservationTests|FullyQualifiedName~AccountCapacityLedgerTests' --logger 'console;verbosity=normal' "$@"
