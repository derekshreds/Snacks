# Luna integration

> **Official service:** Snacks connects to Luna at
> [https://veryluna.com](https://veryluna.com). Luna's source and self-hosting
> environment are private; there is no public self-hosted Luna distribution.
> Custom URLs exist only for the project owner's explicit local integration
> testing.

Snacks can act as an outbound-only Luna connector for enabled Sonarr and Radarr
instances. It does not require port forwarding, a public hostname, or an
inbound tunnel.

Configure Sonarr/Radarr first, then open **Settings → Connections → Luna**:

1. Enable Luna task polling. Production Snacks uses `https://veryluna.com`.
2. Opt into path-free library metadata reads and/or library changes.
3. Sign in. Snacks sends the password to Luna once and never writes it to disk.

The connection can be reviewed or revoked at any time from Luna's
**Settings → Connected apps** section on web or iOS.

## Supported actions

| Capability | Local operation | Returned data |
| --- | --- | --- |
| `radarr.library.read` | Paged Radarr movie list | title, year, public ids, genres, basic status |
| `radarr.catalog.search` | Radarr lookup | up to 20 path-free candidates |
| `radarr.movie.add` | Idempotent add by TMDb id | sanitized added/existing movie |
| `sonarr.library.read` | Paged Sonarr series list | title, year, public ids, genres, episode counts |
| `sonarr.catalog.search` | Sonarr lookup | up to 20 path-free candidates |
| `sonarr.series.add` | Idempotent add by TVDb id | sanitized added/existing series |

Catalog search is available when its Arr integration is enabled. Library lists
require the metadata-read permission; adds require the separate change
permission. Snacks rechecks the live setting for every leased task, so removing
a permission takes effect even before Luna receives the next capability
heartbeat.

Arr base URLs, API keys, root-folder paths, file paths/names, and file sizes
never enter a task result. Add requests choose an accessible local root and
quality profile inside Snacks; optional root/profile ids may select among local
settings without revealing their paths. When metadata reads are enabled, the
filtered task result is sent to Luna and may be included in the AI-provider
request needed to make the recommendation; the full result is not copied into
Luna's durable chat tool-turn history.

Completed remote-task rows are hard-deleted as soon as Luna consumes their
result. If a caller disappears between completion and consumption, a five-minute
safety prune removes the abandoned row. Disconnecting or revoking a connection
also hard-deletes its unfinished tasks.

## Example prompts

Start read-only:

- “Look through my Radarr library and tell me which genres and directors I return to most.”
- “Based on my collection, suggest ten science-fiction movies not already in Radarr. Do not add anything yet.”
- “Find completed mystery series that fit my taste and show what Sonarr is missing.”

When library changes are enabled, make the mutation explicit:

- “Add The Matrix to Radarr, monitor it, and start a search.”
- “Recommend five movies, explain each choice, and wait for my approval before adding anything.”
- “I approve choices two and four. Add only those two to Radarr.”

These prompts also work in Luna voice mode. Luna acknowledges the request while
Snacks is working, then speaks the answer or completion status. Use the same
explicit wording for library changes that you would use in text chat.

Unless a task supplies explicit IDs, Snacks uses the first accessible Arr root
folder and first quality profile. Review those settings before enabling writes.
