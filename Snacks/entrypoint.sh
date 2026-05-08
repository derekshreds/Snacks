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
    # add the snacks user to whatever group actually owns each render node
    # so hardware acceleration keeps working without dropping back to root.
    # Walk every renderD* — on hybrid GPU laptops (e.g. Pop!_OS hybrid mode)
    # the iGPU and dGPU live on different nodes (renderD128, renderD129) and
    # may belong to different groups; the previous renderD128-only check
    # silently locked us out of the iGPU when it landed on renderD129.
    if compgen -G "/dev/dri/renderD*" >/dev/null; then
        for node in /dev/dri/renderD*; do
            DRI_GID="$(stat -c '%g' "${node}")"
            [[ -z "${DRI_GID}" ]] && continue
            if ! id -G snacks | tr ' ' '\n' | grep -qx "${DRI_GID}"; then
                if ! getent group "${DRI_GID}" >/dev/null 2>&1; then
                    # -o allows duplicate GIDs across nodes that share a group.
                    groupadd -g "${DRI_GID}" -o "snacks-render-${DRI_GID}" 2>/dev/null || true
                fi
                usermod -aG "${DRI_GID}" snacks
            fi
        done
    fi

    chown -R snacks:snacks /app/work || true
    exec gosu snacks dotnet Snacks.dll
fi

exec dotnet Snacks.dll
