# Unraid Template

`snacks.xml` is a Community Applications template for installing Snacks on Unraid.

## Install (manual, no CA submission required)

1. In the Unraid web UI, go to **Docker** → **Add Container**.
2. In the **Template** dropdown at the top, paste this URL:
   ```
   https://raw.githubusercontent.com/derekshreds/Snacks/master/unraid/snacks.xml
   ```
3. Adjust the **Media Library** path to your share (default `/mnt/user/Media`).
4. Apply. Web UI is at `http://YOUR-UNRAID-IP:6767`.

The template uses **host networking** so the cluster's UDP broadcast discovery can reach the LAN. Bridge mode would silently break clustering.

## Hardware acceleration

- **Intel iGPU / AMD GPU** — `/dev/dri` is passed through by default. Unraid has the `video` and `render` groups, so privileged mode is not needed (unlike QNAP).
- **NVIDIA** — install the *Nvidia-Driver* plugin from CA, then on this container set:
  - **Extra Parameters**: `--runtime=nvidia`
  - Add a variable `NVIDIA_VISIBLE_DEVICES` = `all`
  - Add a variable `NVIDIA_DRIVER_CAPABILITIES` = `compute,video,utility`

## Paths

| Container | Default host path |
|-----------|-------------------|
| `/app/work/uploads` | `/mnt/user/Media` |
| `/app/work/config`  | `/mnt/user/appdata/snacks/config` |
| `/app/work/logs`    | `/mnt/user/appdata/snacks/logs` |
