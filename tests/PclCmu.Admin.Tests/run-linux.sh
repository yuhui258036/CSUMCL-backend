#!/bin/sh
set -eu
# Run in an isolated SDK container: /source and /packages are read-only mounts,
# /work and /tmp are disposable writable tmpfs. No host ports or network needed.
mkdir -p /work/tests/PclCmu.Admin.Tests
mkdir -p /tmp/runtime
chmod 700 /tmp/runtime
export XDG_RUNTIME_DIR=/tmp/runtime
cp /source/*.cs /source/*.csproj /source/*.js /work/
cp /source/tests/PclCmu.Admin.Tests/*.cs /source/tests/PclCmu.Admin.Tests/*.csproj /work/tests/PclCmu.Admin.Tests/
cat > /work/NuGet.Config <<'EOF'
<configuration><packageSources><clear /></packageSources></configuration>
EOF
cd /work
dotnet run --project tests/PclCmu.Admin.Tests -c Release \
  -p:UseAppHost=false -p:NuGetAudit=false -p:UseSharedCompilation=false -- "$@"
