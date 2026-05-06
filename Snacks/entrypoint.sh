#!/bin/bash
# Snacks container entrypoint.
#
# When PUID/PGID are set, run dotnet as a non-root user owning /app/work
# at those IDs — this lets the container match the NAS owner so files
# written to a shared mount have the correct uid/gid (vital for SMB/NFS
# shares where Snacks isn't the only writer).
#
# When PUID is unset, exec dotnet directly as root — the original behavior,
# preserved so existing deployments don't change semantics on upgrade.

set -e

if [[ -n "${PUID}" ]]; then
    if ! id -u snacks >/dev/null 2>&1; then
        echo "snacks user is missing from the image; falling back to root"
        exec dotnet Snacks.dll
    fi

    usermod  -o -u "${PUID}" snacks
    if [[ -n "${PGID}" ]]; then
        groupmod -o -g "${PGID}" snacks
    fi

    # The render group's GID varies across NAS distributions (Synology,
    # QNAP, Unraid all assign different IDs). If /dev/dri is mounted in,
    # add the snacks user to whatever group actually owns it so hardware
    # acceleration keeps working without dropping back to root.
    if [[ -e /dev/dri/renderD128 ]]; then
        DRI_GID="$(stat -c '%g' /dev/dri/renderD128)"
        if [[ -n "${DRI_GID}" ]] && ! id -G snacks | tr ' ' '\n' | grep -qx "${DRI_GID}"; then
            getent group "${DRI_GID}" >/dev/null 2>&1 || groupadd -g "${DRI_GID}" -o snacks-render
            usermod -aG "${DRI_GID}" snacks
        fi
    fi

    chown -R snacks:snacks /app/work || true
    exec gosu snacks dotnet Snacks.dll
fi

exec dotnet Snacks.dll
