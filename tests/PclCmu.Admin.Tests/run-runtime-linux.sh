#!/bin/sh
set -eu
# /tests contains the compiled net9.0 harness, read-only. /tmp and /published
# must be separate disposable mounts; no source writes or external network.
mkdir -p /tmp/runtime
chmod 700 /tmp/runtime
export XDG_RUNTIME_DIR=/tmp/runtime
export CSUMCL_TEST_SECOND_VOLUME=/published
dotnet --list-runtimes
exec dotnet /tests/PclCmu.Admin.Tests.dll "$@"
