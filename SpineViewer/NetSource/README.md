# GitHub Repo Loader Architecture

This folder contains the lightweight GitHub repo loader fork. It is intentionally kept dependency-free: storage is JSON/files on disk, GitHub access uses `HttpClient`, and credentials use Windows DPAPI.

## Layers

- `GitHubApiClient` is the transport layer. It parses GitHub repo URLs, owns REST/GraphQL/raw download calls, applies PAT authentication, optional proxy settings, and transient retry policy.
- `RepoIndexService` turns a GitHub tree into `SpineBundle` records. It owns full refresh, compare-based incremental refresh, per-bundle commit metadata, and bundle hash generation.
- `BundleSearchService` searches already-built repository indexes. It does not talk to GitHub or the filesystem.
- `BundleDownloadService` downloads indexed bundle files to the local repo store and maintains `downloaded-bundles.json`, which drives the blue/yellow local-state UI.
- `NetSourceDialogViewModel` is the WPF orchestration layer. It is split into partial files so repository state, sync, and download/import workflows can be reviewed separately.

## Storage

The default root is `Resources` under the executable directory.

- `Resources/netcache` stores general network-source cache data and `credentials.dat`.
- `Resources/Spine/repos/{repoId}/trees.json` stores the repository model index.
- `Resources/Spine/repos/{repoId}/downloaded-bundles.json` stores local download state for bundle highlighting.
- `Resources/Spine/repos/{repoId}/downloads/...` stores downloaded model files.

## PAT And Local State

Without a GitHub PAT, the loader can still index repositories and download models, but it does not enable local current/outdated highlighting. That state depends on reliable per-bundle metadata and bundle hashes from the current index; anonymous GitHub API limits make that too fragile for large repositories.

With a PAT, each bundle records a content hash built from the skel, atlas, texture file paths, blob shas, and sizes. Any upstream resource change changes the hash and turns a downloaded bundle into the outdated state.

## Refresh Strategy

Refresh starts by resolving repository default branch and HEAD. If the cached HEAD is unchanged, the existing index is reused. If HEAD changed, `RepoIndexService` first asks the GitHub compare API for changed paths and only rebuilds affected bundle directories. It falls back to a full recursive tree refresh when compare is unavailable, the branch moved in a non-linear way, or the changed file list is too large.

## Texture Selection

Texture files are selected from the same bundle directory. When multiple formats exist, the index prefers texture groups whose stem matches the skel or atlas stem. If several formats have the same stems, all are kept so the original atlas text decides the actual referenced format. The downloader never rewrites atlas content.

## Network Behavior

The preference dialog exposes an optional GitHub proxy. It defaults to off; when enabled, the default endpoint is `127.0.0.1:7890`.

`GitHubApiClient` retries only transient failures: request timeout, common 5xx statuses, and network exceptions. Authentication failures, rate-limit responses, 404, and validation errors are returned immediately so the UI can show the real problem.
