#!/bin/sh
set -eu
base=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
if [ "$(uname -s)" != Linux ] || [ "$(uname -m)" != x86_64 ]; then
    echo "This package requires Linux x86_64." >&2
    exit 1
fi
chmod u+x "$base/BootBridge"
exec "$base/BootBridge" "$@"
