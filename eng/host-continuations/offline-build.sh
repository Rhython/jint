#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd -- "$SCRIPT_DIR/../.." && pwd)

: "${DOTNET_ROOT:?Set DOTNET_ROOT to the extracted .NET SDK directory.}"
: "${NUGET_PACKAGES:?Set NUGET_PACKAGES to the extracted global packages folder.}"

DOTNET_ROOT=$(realpath "$DOTNET_ROOT")
NUGET_PACKAGES=$(realpath "$NUGET_PACKAGES")
DOTNET="$DOTNET_ROOT/dotnet"
RID=${RID:-linux-x64}
CONFIGURATION=${CONFIGURATION:-Release}
CLEAN=${CLEAN:-1}
RUN_FULL_REGRESSION=${RUN_FULL_REGRESSION:-1}
RUN_AOT=${RUN_AOT:-1}
AUDIT_LOG_DIR=${AUDIT_LOG_DIR:-$REPO_ROOT/artifacts/audit-logs/reproduction}
TEST_TIMEOUT_SECONDS=${TEST_TIMEOUT_SECONDS:-120}
AOT_TIMEOUT_SECONDS=${AOT_TIMEOUT_SECONDS:-300}

if [[ ! -x "$DOTNET" ]]; then
  echo "dotnet executable not found: $DOTNET" >&2
  exit 2
fi
if [[ ! -d "$NUGET_PACKAGES" ]]; then
  echo "NuGet global packages folder not found: $NUGET_PACKAGES" >&2
  exit 2
fi

mkdir -p "$AUDIT_LOG_DIR"
TMP_DIR=$(mktemp -d)
cleanup() { rm -rf "$TMP_DIR"; }
trap cleanup EXIT

xml_escape() {
  local value=$1
  value=${value//&/&amp;}
  value=${value//</&lt;}
  value=${value//>/&gt;}
  value=${value//\"/&quot;}
  value=${value//\'/&apos;}
  printf '%s' "$value"
}

NUGET_CONFIG="$TMP_DIR/NuGet.offline.config"
cat > "$NUGET_CONFIG" <<CONFIG
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /></packageSources>
  <config>
    <add key="globalPackagesFolder" value="$(xml_escape "$NUGET_PACKAGES")" />
  </config>
</configuration>
CONFIG

export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"
export NUGET_PACKAGES
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
export MSBUILDDISABLENODEREUSE=1

COMMON_PROPS=(
  -p:TargetFrameworks=net10.0
  -p:OfflineUseSdkRoslyn=true
  -p:GenerateDocumentationFile=false
  -p:UseSharedCompilation=false
)
AOT_PROPS=(
  "${COMMON_PROPS[@]}"
  -p:OfflineUseServicingPacks=true
)

cd "$REPO_ROOT"

if [[ "$CLEAN" == 1 ]]; then
  rm -rf \
    artifacts/bin \
    artifacts/obj \
    artifacts/publish/Jint.HostContinuations.AotSmoke/reproduction_* \
    Jint/obj Jint.SourceGenerators/obj Jint.Tests/obj \
    Jint.HostContinuations.AotSmoke/obj
fi

"$DOTNET" --info > "$AUDIT_LOG_DIR/dotnet-info.log"

"$DOTNET" restore Jint.Tests/Jint.Tests.csproj \
  --configfile "$NUGET_CONFIG" \
  "${COMMON_PROPS[@]}" \
  2>&1 | tee "$AUDIT_LOG_DIR/restore-tests.log"

"$DOTNET" build Jint.Tests/Jint.Tests.csproj \
  -c "$CONFIGURATION" -f net10.0 --no-restore \
  "${COMMON_PROPS[@]}" \
  2>&1 | tee "$AUDIT_LOG_DIR/build-tests.log"

# Avoid compiler/MSBuild server state leaking into test reproducibility.
"$DOTNET" build-server shutdown >/dev/null 2>&1 || true

timeout "${TEST_TIMEOUT_SECONDS}s" "$DOTNET" test Jint.Tests/Jint.Tests.csproj \
  -c "$CONFIGURATION" -f net10.0 --no-build --no-restore \
  --filter 'FullyQualifiedName~HostContinuationTests' \
  --logger "trx;LogFileName=host-continuations-reproduction.trx" \
  "${COMMON_PROPS[@]}" \
  2>&1 | tee "$AUDIT_LOG_DIR/host-continuations-tests.log"

prepare_tzdir() {
  if [[ -n "${TZDIR:-}" && -d "$TZDIR" ]]; then
    return
  fi

  local python_tz
  python_tz=$(find /opt /usr/local/lib /usr/lib -path '*/site-packages/tzdata/zoneinfo' -type d 2>/dev/null | head -n 1 || true)
  if [[ -n "$python_tz" ]]; then
    export TZDIR="$python_tz"
    return
  fi

  if [[ -e /usr/share/zoneinfo/Europe/Kiev ]]; then
    export TZDIR=/usr/share/zoneinfo
    return
  fi

  if [[ -e /usr/share/zoneinfo/Europe/Kyiv ]]; then
    local tzcopy="$TMP_DIR/zoneinfo"
    cp -a /usr/share/zoneinfo "$tzcopy"
    ln -s Kyiv "$tzcopy/Europe/Kiev"
    export TZDIR="$tzcopy"
  fi
}

if [[ "$RUN_FULL_REGRESSION" == 1 ]]; then
  prepare_tzdir
  timeout "${TEST_TIMEOUT_SECONDS}s" "${DOTNET}" test Jint.Tests/Jint.Tests.csproj \
    -c "$CONFIGURATION" -f net10.0 --no-build --no-restore \
    --logger "trx;LogFileName=full-regression-reproduction.trx" \
    "${COMMON_PROPS[@]}" \
    2>&1 | tee "$AUDIT_LOG_DIR/full-regression.log"
fi

if [[ "$RUN_AOT" == 1 ]]; then
  # Split ZIP archives commonly lose Unix executable bits.
  while IFS= read -r -d '' ilc; do
    chmod +x "$ilc"
  done < <(find "$NUGET_PACKAGES/microsoft.dotnet.ilcompiler" -type f -name ilc -print0 2>/dev/null || true)

  "$DOTNET" restore Jint.HostContinuations.AotSmoke/Jint.HostContinuations.AotSmoke.csproj \
    -r "$RID" --configfile "$NUGET_CONFIG" \
    "${AOT_PROPS[@]}" \
    2>&1 | tee "$AUDIT_LOG_DIR/aot-restore.log"

  timeout "${AOT_TIMEOUT_SECONDS}s" "$DOTNET" publish Jint.HostContinuations.AotSmoke/Jint.HostContinuations.AotSmoke.csproj \
    -c "$CONFIGURATION" -f net10.0 -r "$RID" --no-restore \
    "${AOT_PROPS[@]}" \
    2>&1 | tee "$AUDIT_LOG_DIR/aot-publish.log"

  binary=$(find "$REPO_ROOT/artifacts/publish/Jint.HostContinuations.AotSmoke" \
    -type f -name Jint.HostContinuations.AotSmoke -perm -111 | sort | tail -n 1)
  if [[ -z "$binary" ]]; then
    echo "NativeAOT smoke binary not found." >&2
    exit 3
  fi

  file "$binary" | tee "$AUDIT_LOG_DIR/aot-binary-file.log"
  "$binary" | tee "$AUDIT_LOG_DIR/aot-run.log"
  grep -q '^HOST_CONTINUATION_AOT_OK:sent:payload$' "$AUDIT_LOG_DIR/aot-run.log"
fi

echo "HOST_CONTINUATION_REPRODUCTION_OK"
