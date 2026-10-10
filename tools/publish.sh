#!/bin/sh
set -eu
base=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
rid=${1:-linux-x64}
case "$rid" in linux-x64|win-x64) ;; *) echo 'Expected linux-x64 or win-x64' >&2; exit 1;; esac
dotnet publish "$base/IsoWriter.csproj" -c Release -r "$rid" --self-contained true -p:PublishSingleFile=false -o "$base/dist/$rid"
if [ "$rid" = linux-x64 ]; then
    chmod +x "$base/dist/$rid/BootBridge" "$base/dist/$rid/start-bootbridge.sh"
    tar -czf "$base/dist/BootBridge-linux-x64.tar.gz" -C "$base/dist" linux-x64
fi
