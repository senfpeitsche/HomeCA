#!/bin/sh
# Creates the CA and backup keys on first start, then runs HomeCA.
# Both keys are needed to read the data and any backup: copy /config somewhere safe.
set -eu
umask 077

for key in "${Storage__CaKeyPath:-/config/ca.key}" "${Storage__BackupKeyPath:-/config/backup.key}"; do
    if [ ! -s "$key" ]; then
        head -c 32 /dev/urandom > "$key"
        echo "homeca: created $key - store a copy outside this host" >&2
    fi
done

exec dotnet /app/HomeCA.Service.dll "$@"
