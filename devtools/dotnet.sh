#!/bin/sh
# Run a dotnet command in the SDK container on the test host, as uid 1000, on the synced project.
# Usage: devtools/dotnet.sh build -c Release
#        devtools/dotnet.sh test -c Release
# Extra docker flags can be passed in DOCKER_FLAGS (e.g. "-v /host/dir:/fixtures:ro -e SQE_FIXTURE_DIR=/fixtures").
. "$(dirname "$0")/env.sh"
exec $SQE_HOST_EXEC docker run --rm --user 1000:1000 \
  -e HOME=/tmp -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e NUGET_PACKAGES=/nuget \
  -v $SQE_DIR:/src -v $SQE_TEST_ROOT/src/.nuget:/nuget \
  $DOCKER_FLAGS -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet "$@"
