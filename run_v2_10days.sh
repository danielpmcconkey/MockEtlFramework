#!/usr/bin/env bash
set -e

cd /workspace/MockEtlFramework

d="2024-10-01"
END="2024-10-10"
while [ "$(date -d "$d" +%s)" -le "$(date -d "$END" +%s)" ]; do
    echo "=== Running all active V2 jobs for $d ==="
    dotnet run --project JobExecutor -- "$d"
    d=$(date -d "$d + 1 day" +%F)
done
echo "=== Done. 10 days complete. ==="
