# Code review — Wondarr 0.1.0 (2026-10-10)

Reviewed commit: `06238c7` (`chore(release): 0.1.0`), the tip of `main`. Review carried out on 2026-10-11.

## 1. Summary

Wondarr's code is careful and well defended. File placement never overwrites. Tag writes go to a temporary copy and are swapped atomically. Every process call uses `ArgumentList`, and process trees are killed on timeout. The Soulseek budget, MusicBrainz and AcoustID gates are single, shared and correct for requests sent at a steady pace. Reference files are never written to, moved, recycled or deleted on any path I traced. Migrations are additive and safe. All test suites are green.

The serious problems sit in the seams around those defences:

- **Failures after a file has already moved.** The recycle bin, compaction, the library mover and the converter can move a file and then fail to record it.
- **HTTP timeouts treated as shutdowns.** .NET reports an `HttpClient` timeout as a `TaskCanceledException`. Several loops read that as a shutdown, so they stop the app, stop the slskd supervisor, or abort a whole search batch.
- **Torrents and lists that are not Wondarr's own.** Wondarr changes torrents it did not add and spends Gazelle freeleech tokens while only searching. Import lists act on a read that came back empty.
- **The login bypass for local addresses.** It is the default, and it hands out the API key through `/initialize.json` to anything that looks local.

**Counts:** Critical 0 · High 11 · Medium 35 · Low 48 (94 findings).

**Top 5 to fix first**

1. **R-001.** Recycling the old file during an upgrade or conversion can throw after the move. The caller then reports "untouched", nothing rolls back, and the bin cleanup deletes the user's only copy 7 days later.
2. **R-008 and R-009.** The local-address bypass serves `/initialize.json`, and with it the API key, to anyone behind a reverse proxy that does not send `X-Forwarded-For`, and to DNS-rebinding pages. Use setup's full proxy-header list and check `Host`.
3. **R-010 and R-011.** A 5-second slskd poll timeout stops the whole app in external mode. In bundled mode it silently ends the slskd supervisor, and after the first minute any single failed poll SIGKILLs a healthy slskd. One fix covers both: treat an `HttpClient` timeout as a failed poll, and require several failures before a restart.
4. **R-005.** Grabbing a Gazelle release that is already in qBittorrent turns off every other file of that torrent. This breaks Wondarr's own bundles and changes the user's seeding torrents.
5. **R-003 and R-004.** A compaction that is interrupted, or that has a song deleted under it, leaves files hidden in `.wondarr-compact/`. Nothing resumes it, and one permanently failing row blocks that library's Compact for good.

**Coverage and depth.** Eleven read-only reviewers each took one area, and I verified every High finding against the code myself. These areas were read in full:

- the import pipeline;
- compaction and the organizer;
- the command executor and search;
- the metadata gates;
- authentication and the HTTP surface;
- slskd and YouTube;
- Torznab, qBittorrent, SABnzbd and notifications;
- references and import lists;
- backup, restore and migrations;
- Docker, the workflows and the merge scripts.

These were skimmed rather than read:

- the front end beyond its data layer and core components;
- the generated API types;
- the phase gate scripts;
- the test suite (sampled for quality; not read in full).

Section 5 lists what was not reached.

## 2. Findings

Each finding gives its severity, status (CONFIRMED means traced end to end through the code, PLAUSIBLE means a strong suspicion not fully proven), area, location, the defect, a concrete failure scenario and a suggested fix. Paths are relative to the repository root.

### High

#### R-001 — The recycle step can leave the user's old file in the bin while the caller reports it untouched
- **Severity:** High · **Status:** CONFIRMED (code path); the trigger depends on file ownership · **Area:** Recycle bin / placement
- **Where:** `src/Wondarr.Core/Organizer/RecycleBin.cs:111-135`, `src/Wondarr.Core/Organizer/FilePlacer.cs:116-148`
- **What is wrong:** `RecycleAsync` moves the file into the bin first and only then calls `_disk.SetLastWriteTimeUtc(recycled, …)`, outside any handler. If that call throws (`UnauthorizedAccessException` or `IOException`), the exception leaves after the move has happened. `FilePlacer` catches it as "Could not recycle … The old file is untouched". `recycled` is still null, so there is no `RollBack`. `ImportService` and `FileConverter` then treat the result as "nothing placed", and the song row keeps pointing at the old path, which is now empty.
- **Failure scenario:**
  1. The library files are owned by another uid than Wondarr's PUID: after a PUID change, after `chown -R plex:` on the music folder, or on an NFS or CIFS mount that refuses `utimensat`. The recycle bin is on the same mount, so the move is a rename that succeeds.
  2. Setting explicit times on Linux needs ownership, so the timestamp call fails with EPERM.
  3. Every upgrade, and every song in a library-wide `ConvertFiles`, now moves the old file into the bin, fails the placement, and leaves `song_file.path` pointing at nothing.
  4. Housekeeping deletes the bin's contents after `import.recycle_bin_cleanup_days` (7 days).
- **Fix:** Make the timestamp best-effort (catch and log it after the move), or stamp the file before moving it. As a backstop, have `FilePlacer` roll back whenever `replaces` no longer exists after a failed recycle.

#### R-002 — Recycle-bin cleanup can delete files of another library or a reference library nested inside the bin
- **Severity:** High · **Status:** PLAUSIBLE (needs a specific configuration) · **Area:** Recycle bin
- **Where:** `src/Wondarr.Core/Organizer/RecycleBin.cs:84-90, 143-231`; `src/Wondarr.Core/Organizer/ImportOptions.cs:129-142`
- **What is wrong:** The "bin must be outside the library" check runs only in `RecycleAsync`, and only against the library being recycled from. The options validator refuses only a filesystem root, exactly `/data`, `/config` and the config directory. `CleanupAsync` deletes every file older than N days anywhere under a marked bin and never checks any library or reference root. Creating a library or reference library inside the bin is not refused.
- **Failure scenario:** The user sets `import.recycle_bin_path: /data/media` and has libraries at `/music` and `/data/media/collection` (a second library, or a reference library holding their own collection). Recycling from `/music` passes the check and writes the marker into `/data/media`. The next Housekeeping run deletes every file in `/data/media/collection` that is more than 7 days old.
- **Fix:** Refuse, in the validator and in library and reference-library create/update, any bin that contains or sits inside a library or reference root. Better still, have the cleanup delete only files the bin itself recorded.

#### R-003 — One compact row that keeps failing blocks that library's compaction for good and hides its file; deleting a song mid-compaction triggers it
- **Severity:** High · **Status:** CONFIRMED · **Area:** Compaction / song delete
- **Where:** `src/Wondarr.Core/Compaction/CompactExecutor.cs:197-244` (rows loaded by library only; resume preferred over planning), `:805-821`; `src/Wondarr.Core/Songs/SongService.cs:416-432`; `src/Wondarr.Core/Songs/SongEditorService.cs:236-265`; `src/Wondarr.Core/Persistence/CompactMoveConfiguration.cs` (`compact_move` has no foreign key to `song`)
- **What is wrong:** Every failure in `PlaceAsync` keeps `StagedPath` set. The next run's resume picks up any Failed row whose staged file still exists, and while anything is pending the run never plans. Nothing ever gives up on a row that cannot succeed. The single and mass song deletes do not check for unfinished compact moves, and the row outlives its song.
- **Failure scenario:**
  1. During a Compact run's Plex wait (up to about 10 minutes), the user deletes song S. Its file has disappeared into `.wondarr-compact/`, so deleting and re-adding looks like a natural fix.
  2. `PlaceAsync` finds no song and marks the row Failed, keeping the staged path.
  3. Every later Compact on that library resumes only that row, fails it again and reports "0 moved, 1 failed". The library can never be compacted again, and S's file stays in the hidden folder with nothing pointing at it.
  - Any permanently failing placement does the same, for example a tag write that always fails. For a song that still exists, its imports also defer every minute indefinitely.
- **Fix:** Refuse song deletion (single and editor) while `CompactMoveRules.IsUnfinished` rows exist for the song, as the library mover already does. Give resume an exit: drop a row whose song is gone (and move its file to the recycle bin), cap retries, and let planning proceed around rows that stay stuck.

#### R-004 — An interrupted compaction is never resumed; staged files stay hidden and the songs vanish from Plex
- **Severity:** High · **Status:** CONFIRMED · **Area:** Compaction / restart
- **Where:** `src/Wondarr.Core/Jobs/CommandExecutor.cs:230-260` (interrupted commands become `Orphaned`, never re-queued); `src/Wondarr.Core/Compaction/CompactExecutor.cs` (the only resume is the next manual run); no health check reads `compact_move`
- **What is wrong:** A container stop during a Compact run leaves its rows Staged. Plex has already scanned the old folders and emptied its trash, so those songs are gone from Plex. On start the command is marked Orphaned and nothing re-queues it or warns. `docs/architecture/LIBRARY_OUTPUT.md` says an interrupted run "resumes from the rows", but only a manual re-run does that.
- **Failure scenario:** A Watchtower image update or a host reboot lands during a Compact run's 10-minute Plex wait. N songs' files stay in `.wondarr-compact/`:
  - `song_file.path` points at files that are gone;
  - the songs disappear from Plexamp;
  - every import or upgrade for them defers every minute ("waiting for compaction") until the user happens to run Compact again.
- **Fix:** At startup, queue `CompactLibrary` for every library with unfinished `compact_move` rows, or at least raise a health warning that names the library.

#### R-005 — A torrent grab of a release already in qBittorrent turns off every other file of that torrent
- **Severity:** High · **Status:** CONFIRMED · **Area:** Torrent grab
- **Where:** `src/Wondarr.Sources.Torznab/Searching/IndexerSourceProvider.Grab.cs:173-215`; `src/Wondarr.Sources.Torznab/Indexers/GazelleIndexer.cs` (never sets `InfoHash`); `src/Wondarr.Sources.Torznab/Searching/ContainerCandidates.cs:169`
- **What is wrong:** The "already in the client, select non-exclusively" path only runs when `release.InfoHash` is known. Gazelle releases never carry one. So `GrabTorrentAsync` downloads the `.torrent`, computes the hash, skips the add if the torrent is already present, and still calls `SelectAsync(exclusive: true)`. That sets every file not in `wanted` to priority 0. The magnet path (`:369-372`) does the same to a stopped torrent that was already there.
- **Failure scenario:**
  - **Bundles:** A Gazelle grab bundles songs A, B and C, and the first grab selects all three. The bundled grab for B (with `AlsoWanted = null`) turns off A and C; the grab for C turns off B. Only C downloads; A and B stall and fail.
  - **The user's own torrent:** A user already seeding the album in qBittorrent has every other file of it set to "do not download". With qBittorrent's "keep unselected files in .unwanted" option on, qBittorrent moves the user's files.
  - The existing bundle test sets `InfoHash` by hand, so it never takes this path.
- **Fix:** After computing the hash, use the non-exclusive path whenever the torrent already exists. Carry the computed info-hash into the bundled candidates. Base exclusive selection on "Wondarr added this torrent just now", not on how the hash was learned.

#### R-006 — Gazelle freeleech tokens are spent by searches, not grabs
- **Severity:** High · **Status:** CONFIRMED in code; that a download with `usetoken=1` spends a token is the Gazelle API's documented behaviour · **Area:** Gazelle / search
- **Where:** `src/Wondarr.Sources.Torznab/Indexers/GazelleIndexer.cs:117, 183, 288-294`; `src/Wondarr.Sources.Torznab/Searching/IndexerSourceProvider.cs:32, 176-180, 353-372`
- **What is wrong:** With "Use freeleech tokens" on, `Parse` appends `&usetoken=1` to every release's `DownloadUrl`. Only the 5 best-seeded torrents get a file list (`FileListsPerSearch = 5`), but the provider reads containers for the top 8 releases (`ContainersPerQuery = 8`). For the others, `ReadContainerAsync` downloads `release.DownloadUrl`, token flag included, just to read the file list.
- **Failure scenario:** A scheduled MissingSearch looks for a song on an album with 6 or more editions. Releases ranked 6 to 8 are downloaded at search time, up to 3 queries per song. Each download spends a token whether or not anything is grabbed, so one night's missing search can use up every token the user has.
- **Fix:** Keep the token flag out of `DownloadUrl` and add it only on the grab path. Alternatively, never download a Gazelle `.torrent` at search time; fetch file lists with `action=torrent` instead.

#### R-007 — Import lists: an empty or cut-short read is treated as the whole list, so Mirror and AddAndUnmonitor act on every song
- **Severity:** High · **Status:** CONFIRMED for YouTube Music, PLAUSIBLE for Deezer · **Area:** Import lists
- **Where:** `src/Wondarr.Sources.YouTube/InnertubePlaylistParser.cs:45-68` (unknown shape returns `([], null)`); `src/Wondarr.Sources.YouTube/InnertubeClient.cs` (`BrowsePlaylistAsync` stops on an empty page); `src/Wondarr.Core/ImportLists/Deezer/DeezerPlaylistProvider.cs:112`; `src/Wondarr.Core/ImportLists/ImportListService.cs:362-373, 671-679`
- **What is wrong:** An unparseable InnerTube response (HTTP 200) yields zero rows and no error, and a continuation page that does not parse ends the read early. The Deezer provider stops at the first page shorter than 100 rows, even when `total` says more remain. `ApplyFetchAsync` marks every known item missing from the read as removed, and `ApplyPolicyAsync` runs straight after. There is no sanity check against the list's previous size.
- **Failure scenario:** YouTube changes its InnerTube layout, as it does regularly. The next hourly sync of a 300-song YouTube Music list on Mirror reads 0 rows. Every list-added song without a file is deleted, and every song with a file is unmonitored. No files are lost, but with R-019 the list never restores itself when the read works again.
- **Fix:** For Mirror and AddAndUnmonitor, treat 0 rows (or a drop of more than about half) against a list that held items as a failed read, and skip the policy. Deezer should stop only on an empty page or at `index >= total`.

#### R-008 — The local-address bypass only checks `X-Forwarded-For`, so a proxy that sends other headers hands the API key to anyone
- **Severity:** High · **Status:** CONFIRMED (code); exposure depends on the user's proxy · **Area:** Authentication
- **Where:** `src/Wondarr.Api/Authentication/UiAuthorizationHandler.cs:30-45`; `src/Wondarr.Api/Extensions/IpAddressExtensions.cs:30-58` (172.16/12 covers Docker bridges); `src/Wondarr.Api/Program.cs:83` (`UseForwardedHeaders()` with no options does nothing); `src/Wondarr.Api/Authentication/InitializeController.cs:29`
- **What is wrong:** Under the default `auth_required: disabledForLocalAddresses`, the request passes the UI policy when it has no `X-Forwarded-For` and its socket address is private. That policy serves the SPA and `/initialize.json`, which carries the API key. First-login setup refuses seven proxy headers (`AuthenticationController.cs:27`: `Forwarded`, `X-Real-IP`, `X-Forwarded-Host`, `X-Forwarded-Proto`, `Via`, `X-Original-For` as well as XFF). Its comment says it uses "exactly the definition" the handler uses; it does not. DECISIONS build session 11 #3 states the bypass's definition ("a request with X-Forwarded-For is never local") but does not weigh proxies that omit XFF.
- **Failure scenario:** nginx in a sibling container with `proxy_pass http://wondarr:1077; proxy_set_header X-Real-IP $remote_addr;` (nginx adds no XFF by default). Every internet request then arrives from 172.18.0.x without XFF, is treated as local, and receives the full UI and the API key. The same happens behind:
  - a proxy that sends only RFC 7239 `Forwarded`;
  - a TCP-level proxy, an SSH tunnel or socat;
  - Docker's userland proxy relaying IPv6 clients from the bridge gateway (PLAUSIBLE).
- **Fix:** Use setup's `ProxyHeaders` list in `UiAuthorizationHandler`, and add a test for each header. See also the question about the default in section 4.

#### R-009 — DNS rebinding lets any web page a LAN user opens read `/initialize.json`, and defeats setup's Origin check
- **Severity:** High · **Status:** CONFIRMED on the server side; needs a browser without local-network-access blocking (Firefox, Safari, older Chrome) · **Area:** Authentication
- **Where:** `src/Wondarr.Api/Authentication/UiAuthorizationHandler.cs:30-45`; `src/Wondarr.Api/Authentication/AuthenticationController.cs:206-216`; `src/Wondarr.Api/Program.cs` (no HostFiltering and no `AllowedHosts`)
- **What is wrong:** Nothing checks the `Host` header. The bypass decides on the socket address alone, and `IsSameOrigin` compares `Origin` with the request's own `Host`. Under rebinding both are the attacker's name, so they always match.
- **Failure scenario:**
  1. A LAN user visits `evil.example`. Its page rebinds the name to 192.168.1.10 and fetches `http://evil.example:1077/initialize.json`.
  2. Wondarr sees a private source address with no XFF and returns the API key. To the browser this is same-origin, so the script can read it.
  3. The script then calls any `/api/v1` endpoint: deleting songs, staging a crafted restore, or pointing `soulseek.shared_folders` at `/config` to share the API key and Soulseek password on the network.
  - On an install with no login yet and `auth_required: enabled`, the same trick passes `POST /login/setup` and claims the instance.
- **Fix:** Validate `Host` for the bypass and for setup. Accept IP literals, `localhost`, the machine's own names and a configurable hostname list. Alternatively, enable HostFiltering with a configurable allow-list.

#### R-010 — External slskd mode: one slow poll stops the whole app
- **Severity:** High · **Status:** CONFIRMED · **Area:** Soulseek / hosted services
- **Where:** `src/Wondarr.Sources.Slskd/SlskdExternalMonitor.cs:55-79, 145`; `src/Wondarr.Sources.Slskd/ServiceCollectionExtensions.cs:52` (5 s `HttpClient.Timeout`); no `BackgroundServiceExceptionBehavior` override anywhere in `src`
- **What is wrong:** When the 5-second timeout runs out, `HttpClient` throws `TaskCanceledException`, which is an `OperationCanceledException`. `PollAsync` catches only `when (exception is not OperationCanceledException)`, and `ExecuteAsync` has no try/catch, so the BackgroundService faults. The host has not been asked to stop, so .NET treats this as a fault, and the default `StopHost` stops the app.
- **Failure scenario:** The user's slskd hangs, is paused, or its host drops packets. The first 30-second poll waits 5 seconds and Wondarr exits. s6 restarts it and it exits again at the next poll, so it crash-loops for as long as slskd stays unresponsive. With R-026, each restart also resets the Soulseek search budget.
- **Fix:** In the poll, catch `OperationCanceledException` when `stoppingToken` is not cancelled and report "unreachable"; wrap the loop body.

#### R-011 — Bundled slskd: a poll timeout silently ends the supervisor, and after the first minute any one failed poll kills slskd
- **Severity:** High · **Status:** CONFIRMED · **Area:** Soulseek / process supervision
- **Where:** `src/Wondarr.Sources.Slskd/SlskdHost.cs:207-210` (shutdown catch), `:333-378` (`startingDeadline` set once), `:410-425`
- **What is wrong:**
  - **(a) Timeouts end the supervisor.** The same 5-second `TaskCanceledException` escapes `PollAsync` and `MonitorAsync`. `ExecuteAsync`'s `catch (OperationCanceledException)` treats it as shutdown and returns. From then on, until Wondarr itself restarts:
    - the status stays "Running, logged in";
    - a later slskd crash is never restarted;
    - the `OnChange` subscription is gone, so a password or share change through the settings API never reaches slskd, although the API answers `restartsSlskd: true`.
  - **(b) Any failed poll counts as a crash.** `startingDeadline` is computed once when monitoring begins and never cleared after the first successful poll. More than 60 seconds after start, any single failed poll — a 503, an `HttpRequestException`, a `SQLITE_BUSY` while reading the slskd secrets — goes straight to `SetCrashed("…within 60 seconds of starting")`, and the process tree is killed.
- **Failure scenario:** slskd is busy with the share rescan Wondarr asks for after imports and answers one `/application` call with a 503. Wondarr SIGKILLs it mid-transfer and restarts it with backoff. If instead it takes more than 5 seconds to answer, supervision ends silently and slskd is never restarted again.
- **Fix:** Treat the deadline as "not yet seen running" only. After the first success, require N consecutive failures before a restart. Handle an `OperationCanceledException` while `stoppingToken` is not cancelled like any other poll failure.

### Medium

#### R-012 — A file can be placed but never recorded (shutdown cancellation, or a failure after the move) in import, compaction, library move and conversion
- **Severity:** Medium · **Status:** CONFIRMED (code path; the timing window is narrow) · **Area:** Import / Compact / mover / converter
- **Where:** `src/Wondarr.Core/Importing/ImportService.cs:732-748, 823-840`; `src/Wondarr.Core/Compaction/CompactExecutor.cs:922-963` (the comment at about 946-956 claims the opposite); `src/Wondarr.Core/Profiles/SongLibraryMover.cs:260-307`; `src/Wondarr.Core/Media/FileConverter.cs:481-510`; `src/Wondarr.Core/Organizer/FilePlacer.cs:194-229`; `src/Wondarr.Core/Importing/QueueTracker.cs:392-393`
- **What is wrong:** There are two ways the file moves while the database does not record it.
  - **Cancellation.** `FilePlacer` deliberately stops honouring the token once it has recycled, but the following `SaveChangesAsync` runs with the same token. Every repair catch excludes `OperationCanceledException`, so a shutdown here leaves the file at its new path and the database unchanged.
  - **A placement result of `Success=false` with `FinalPath` set.** This comes back when a chmod fails (`SetPermissions` on), or when a cross-device copy succeeded but deleting the source did not. Import treats it as "nothing placed". Compact keeps the old path, and the mover's comment says the file "is exactly where it was".
- **Failure scenario:**
  - **Import:** A container stop lands during a FLAC upgrade's placement. On restart, `RecoverAsync` resets the item to Completed and the download is gone ("Downloaded file is missing", next attempt allowed). The next candidate lands as `Song (2).flac` next to an untracked copy, and the song row points at the recycled file.
  - **Compact:** The row stays Staged with no staged file, so it is failed. The song keeps a missing path while its real file sits, untracked, under the new album.
- **Fix:** Use `CancellationToken.None` for every database write after a successful move. Treat a result whose `FinalPath` exists as placed and record it, as `FileConverter`'s fallback already does. On compaction resume, look for a Staged row's file at `ToPath` (and its `(n)` variants).

#### R-013 — A transcoded YouTube download leaves the queue item pointing at the deleted `.opus`
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** Import
- **Where:** `src/Wondarr.Core/Importing/ImportService.cs:397-446` (`File.Delete(downloadPath)` at about 419; `item.DownloadPath` is not updated), `:563-576`, `:1043`
- **What is wrong:** After the default AAC-256 transcode the `.opus` is deleted, but `item.DownloadPath` still names it. The Ogg-rename branch and the compaction deferral do update the path; the transcode branch does not.
- **Failure scenario:**
  - **AcoustID unavailable or rate-limited.** The item goes back to Completed for a retry. The next pass finds `.opus` missing, fails it with "Downloaded file is missing" and grabs the next candidate. A good file is thrown away and left orphaned on every AcoustID outage.
  - **Any YouTube rejection.** The cleanup deletes the already-deleted `.opus`, and the `.m4a` and its per-grab folder are left behind.
- **Fix:** Set `item.DownloadPath = importPath` straight after the successful transcode and delete.

#### R-014 — A reference file that goes missing or is renamed never makes its song wanted again; the song keeps a dead path
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** Reference libraries / wanted
- **Where:** `src/Wondarr.Core/References/ReferenceScanner.cs:270-293`; `src/Wondarr.Core/Wanted/WantedService.cs:47-52`; `src/Wondarr.Core/Searching/MissingSearchCommandHandler.cs:140-150`; `src/Wondarr.Core/References/ReferenceOwnership.cs:162-183`
- **What is wrong:** The scan sets the row to `Missing` but leaves the song's reference `song_file` pointing at the vanished path. Every wanted path (the missing loop, `wanted/missing`, SongSearch) requires `song.File == null`, and the upgrade loop excludes reference files. DECISIONS build session 11 #1 promises that "a `Missing` reference row makes the song wanted again"; that never happens. The tests that claim to cover it seed a Missing row with no `song_file`, a state the scanner never produces.
- **Failure scenario:**
  - **The user deletes a file.** It is never fetched again.
  - **The user renames or moves a file.** The old row goes Missing and the new one is identified as the same song, but `LinkAsync` records "duplicate: the song already has a file". The song points at a non-existent path for good: Plex playlist matching and `.m3u8` export break, and in adopt mode the new row is never adopted.
- **Fix:** When the held reference row goes Missing, release ownership in the same save, or repoint the song when the same song is identified at a new path. Guard this with a grace period or a per-scan threshold so that a half-unmounted share does not trigger mass re-downloads (see section 4).

#### R-015 — Choosing an album during an unfinished compaction is silently overwritten, and the song stays pinned to the wrong album
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** Album pinning / compaction
- **Where:** `src/Wondarr.Core/Songs/SongService.cs:458-524` (no unfinished-move check); `src/Wondarr.Core/Compaction/CompactExecutor.cs:745-786, 823-836` (`Apply` copies `Proposed` without checking `Pinned`)
- **Failure scenario:**
  1. Song S is staged by a Compact run.
  2. The user picks album X, which saves X with `pinned = true`.
  3. `PlaceAsync` then applies the plan's album Y and files and tags S under Y, with `pinned` still true.
  - The user's choice is lost, and no later Compact will correct it, because the song is now pinned.
- **Fix:** Refuse `SetAlbumContextAsync` while the song has an unfinished move, or skip a row whose context became pinned after the plan was made.

#### R-016 — Cancelling a torrent item removes the torrent with its data on reported progress alone, including torrents Wondarr did not add
- **Severity:** Medium · **Status:** CONFIRMED that it changes and removes pre-existing torrents; PLAUSIBLE that it deletes data · **Area:** Torrent cancel / data safety
- **Where:** `src/Wondarr.Sources.Torznab/Searching/IndexerSourceProvider.Grab.cs:385-411`
- **What is wrong:** The grab handle does not record whether Wondarr added the torrent. `CancelTorrentAsync` sets the song's file to priority 0, then calls `RemoveAsync(deleteFiles: true)` when no other item uses the torrent and every file reports progress ≤ 0. It does not look at the torrent's state. DECISIONS build session 8 #5 says Wondarr never deletes torrent data.
- **Failure scenario:**
  1. A song is grabbed through the fast path from a torrent that was already in the client: the user's own, or one left by an earlier grab whose song has since been imported.
  2. qBittorrent restarts and re-checks it, so progress reads 0.
  3. A queue removal, or the 30-minute remote-queue timeout, calls cancel, and Wondarr deletes data it does not own.
- **Fix:** Store "added by Wondarr" in the handle. Remove only those torrents, never while they are checking or fetching metadata, and never change priorities on a torrent Wondarr did not add.

#### R-017 — qBittorrent's "keep incomplete torrents in" folder breaks staging
- **Severity:** Medium · **Status:** CONFIRMED in code (relies on qBittorrent's documented `download_path` behaviour) · **Area:** Torrent staging
- **Where:** `src/Wondarr.Sources.Torznab/Searching/IndexerSourceProvider.Grab.cs:96-101, 325-330, 584-587`; `src/Wondarr.Sources.Torznab/Clients/QBittorrentClient.cs:88-89`
- **What is wrong:** When a file reaches 100 %, staging looks for it only under `save_path`. qBittorrent keeps files in `download_path` until the whole torrent completes and then moves them (state `moving`). The missing file becomes a permanent `Failed`.
- **Failure scenario:** In a bundle, song A finishes while B is still downloading. A fails with "the client reports the file at …, which is not there", and the queue moves to the next candidate. qBittorrent finishes A anyway, and the result is never used.
- **Fix:** Treat a missing file as a wait (`RemotelyQueued`) while the torrent is incomplete or moving, and resolve the path from `download_path` or `content_path` while it is incomplete.

#### R-018 — "Adopt now" can run beside a scan's own adoption step and copy every file twice
- **Severity:** Medium · **Status:** CONFIRMED (no gate or lock on this path) · **Area:** Adoption
- **Where:** `src/Wondarr.Core/References/ReferenceAdoptCommandHandler.cs:52-77`; `src/Wondarr.Core/References/ReferenceLibraryScanCommandHandler.cs:26, 64-66` (the per-library gate is private to the scan); `src/Wondarr.Core/References/ReferenceAdopter.cs:117-283`
- **What is wrong:** `ReferenceAdopt` and `ReferenceLibraryScan` have different names, so queue dedupe does not stop them running together. The adopter takes neither the per-library gate nor `ISongFileLock`.
- **Failure scenario:** The user clicks "Scan now" and then "Adopt now". Both walk the same Identified rows, and every remaining file is copied twice (the second copy with a collision suffix). The last save wins `song_file`, so the other copy stays in the managed library untracked: a duplicate in Plex, never upgraded or cleaned up.
- **Fix:** Have `ReferenceAdopt` take the same per-library gate, and hold `ISongFileLock` around organize-and-record in the adopter.

#### R-019 — A song that Mirror deleted is never re-added when its item comes back; songs that AddAndUnmonitor unmonitored are never re-monitored
- **Severity:** Medium (High together with R-007) · **Status:** CONFIRMED · **Area:** Import lists
- **Where:** `src/Wondarr.Core/ImportLists/ImportListService.cs:644-655`; `src/Wondarr.Core/ImportLists/PasteListService.cs:283`; `src/Wondarr.Core/Persistence/DomainModelConfiguration.cs:261-264` (`SongId` is set to NULL on delete)
- **What is wrong:** When an item reappears, only `RemovedAt` is cleared. The item stays `Added` with `SongId = null`, and `ProcessAsync` resolves only `Pending` items.
- **Failure scenario:** A song is removed from a Spotify playlist and put back a week later, or R-007 happens. The song is gone from the library for good, and the list still counts it as added.
- **Fix:** When a known item returns with `SongId == null`, reset it to `Pending`. When its song was unmonitored by the policy, monitor it again.

#### R-020 — Two syncs of the same list can run at once
- **Severity:** Medium · **Status:** CONFIRMED that nothing prevents it; the overlap is PLAUSIBLE · **Area:** Import lists / Plex playlists
- **Where:** `src/Wondarr.Core/ImportLists/ImportListSyncCommandHandler.cs:54-84`; `src/Wondarr.Core/ImportLists/ImportListService.cs:390-406, 614-666`; `src/Wondarr.Core/ImportLists/PlaylistWriter.cs:286-311`
- **What is wrong:** "Sync now" (with a body) and the hourly run (without one) are different commands. A long first sync sets `LastSyncedAt` only at the end, so the hourly run treats the list as due and syncs it again in parallel.
- **Failure scenario:**
  - The second run loaded the list before the first saved `PlexPlaylistKey`, so it creates a second Plex playlist with the same name; the first is never reconciled again.
  - Overlapping fetches insert duplicate `import_list_item` rows (there is no unique index on list and external id). Those duplicates are never marked removed, so the songs stay in the playlist for good and the removal policy never applies to them.
- **Fix:** Add a per-list gate that yields its executor place, and a unique index on `(import_list_id, external_id)`.

#### R-021 — A source longer than 10,000 items has its tail treated as removed
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** Import lists
- **Where:** `src/Wondarr.Core/ImportLists/ImportListService.cs:638-642, 671-679`; provider caps in `DeezerPlaylistProvider.cs:23`, `LastFmLovedProvider.cs:24`, `ListenBrainzLovedProvider.cs:24`, `InnertubeClient.cs` (`MaxPlaylistPages`)
- **Failure scenario:** Loved-tracks lists come newest first. Each new love pushes the oldest item past position 10,000, and under Mirror or AddAndUnmonitor that song is unmonitored or deleted while it is still in the source.
- **Fix:** When the read was cut short, do not mark removals or apply the policy, and have providers report when they truncate.

#### R-022 — One identity whose Deezer id collides with a stored song fails a whole batch add, every time
- **Severity:** Medium · **Status:** PLAUSIBLE (code chain traced; the trigger depends on the data) · **Area:** Song add / import lists / references
- **Where:** `src/Wondarr.Core/Songs/SongService.cs:1195-1222` (stored songs are looked up by Deezer id only when the identity has no MusicBrainz id), `:873`; `src/Wondarr.Core/Identity/IdentityResolver.cs:338`; `src/Wondarr.Core/ImportLists/PasteListService.cs:283-318`; `src/Wondarr.Core/References/ReferenceIdentifier.cs:311-316`; `src/Wondarr.Core/References/ReferenceLibraryScanCommandHandler.cs:95-136`
- **What is wrong:** A Deezer item resolves to a MusicBrainz identity that also carries the Deezer id. A stored Deezer-only song with that id is missed, the insert breaks the unique index, and `AddIdentitiesAsync` throws for the whole batch.
  - In reference identification, one failing chunk aborts the rest of the run.
  - Adoption then runs in the same scope without clearing the change tracker, so the failed chunk is re-sent by the adopter's first save.
- **Failure scenario:** A track sits in two Deezer playlists. List 1 added it as Deezer-only. MusicBrainz later learns its ISRC, and list 2's batch add then throws on every sync. All of list 2's items stay Pending, and each hour they are all re-resolved at MusicBrainz's 1 request per second.
- **Fix:** Match stored songs on either key. Isolate failures per item or per chunk. Clear the tracker after a failed identification.

#### R-023 — Search providers treat their own `HttpClient` timeouts as cancellation and abort the batch
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** Soulseek / YouTube / Torznab search
- **Where:**
  - `src/Wondarr.Sources.Slskd/SoulseekSourceProvider.cs:147`; `src/Wondarr.Sources.Slskd/SlskdSearchRunner.cs:133, 148-150`
  - `src/Wondarr.Sources.YouTube/YouTubeSourceProvider.cs:258`
  - `src/Wondarr.Sources.Torznab/Indexers/IndexerHttp.cs:70`; `GazelleIndexer.cs:396`; `ProwlarrIndexer.cs:183`; `Searching/IndexerSourceProvider.cs:282-285, 333-344`
  - `src/Wondarr.Core/Searching/SongSearchService.cs:284-296, 993-1001`; `MissingSearchCommandHandler.cs` (rethrows)
- **What is wrong:** Each of these throws `TaskCanceledException` while the caller's token is not cancelled:
  - slskd's 15-second request timeout and 10-second read timeout;
  - InnerTube's 20-second timeout;
  - the indexers' 30-second timeout.
  
  The providers rethrow every `OperationCanceledException`, so the consequences are:
  - the remaining queries and tiers for the song are skipped;
  - a MissingSearch records the whole batch as failed ("A task was canceled.");
  - a slow Prowlarr search returns `[]` with no warning;
  - a timeout during a grab marks the run Cancelled.
- **Failure scenario:** A MissingSearch over 200 songs. One `GET /searches/{id}` stalls for 15 seconds on song 3, and songs 4 to 200 wait for the next scheduled run.
- **Fix:** Rethrow only when the caller's token is cancelled; otherwise treat it as a failed query, as the share rescanner, `SabnzbdProxy` and `QBittorrentProxy` already do.

#### R-024 — Gazelle's 5-requests-per-10-seconds limiter does not count resilience retries
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** External limits
- **Where:** `src/Wondarr.Sources.Torznab/Indexers/GazelleIndexer.cs:384-400`; `src/Wondarr.Sources.Torznab/ServiceCollectionExtensions.cs:49-67`
- **What is wrong:** One turn is taken per call, but the named `indexer` client's Polly handler retries 5xx, 408 and 429 up to twice, outside the limiter.
- **Failure scenario:** RED returns a Cloudflare 502. One search (a browse plus 5 file lists) becomes up to about 18 requests in roughly 15 seconds, against a tracker that disables API keys for abuse.
- **Fix:** Give Gazelle its own client with no retry handler, or put the limiter inside the retry pipeline.

#### R-025 — InnerTube searches ignore 429 and `Retry-After`
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** External limits (AGENTS.md: honour `Retry-After` everywhere)
- **Where:** `src/Wondarr.Sources.YouTube/InnertubeClient.cs:153-173`; `src/Wondarr.Sources.YouTube/YouTubeSourceProvider.cs:251-266`
- **Failure scenario:** YouTube starts answering 429. A MissingSearch batch keeps sending 2 to 3 InnerTube searches per song back to back, with no pacing anywhere, which invites a bot check or an IP block on the user's connection.
- **Fix:** Read `Retry-After` on 429 and 503, pause the YouTube source until then with a shared not-before time, and skip the song's remaining queries.

#### R-026 — The Soulseek search budget lives only in memory, so a restart resets the 30-per-4-minutes window
- **Severity:** Medium · **Status:** PLAUSIBLE · **Area:** External limits
- **Where:** `src/Wondarr.Sources.Slskd/SoulseekSearchBudget.cs:58-63`; `src/Wondarr.Sources.Slskd/ServiceCollectionExtensions.cs:75`
- **Failure scenario:** R-010's crash loop gives each process lifetime a fresh window. That is about 12 searches per 60-second lifetime plus a 10-second restart, roughly 40 searches per 4 minutes on one account.
- **Fix:** Persist submission times (a settings row or a small file in `/config`) and seed the window from them at start.

#### R-027 — If Wondarr crashes, slskd is orphaned and its replacement fights it
- **Severity:** Medium · **Status:** PLAUSIBLE · **Area:** Process handling
- **Where:** `src/Wondarr.Sources.Slskd/ProcessLauncher.cs:9-35`; `src/Wondarr.Sources.Slskd/SlskdHost.cs:214-238`; `docker/root/etc/s6-overlay/s6-rc.d/svc-wondarr/run`
- **What is wrong:** slskd is killed only in `StopAsync`. An unhandled crash or an OOM kill leaves it running under s6, still logged in and holding its ports.
- **Failure scenario:** The restarted Wondarr starts a second slskd, which fails to bind. Meanwhile polls reach the orphan, which has the same API key, so the status flaps between Running and Crashed. Settings restarts kill only the new child, so the orphan never picks up changes.
- **Fix:** Write a pid file under `/config/slskd` and kill a live leftover before launching, or run slskd as its own s6 longrun.

#### R-028 — A `Retry-After` cooldown does not bind requests already waiting at the gate
- **Severity:** Medium · **Status:** CONFIRMED (mechanism) · **Area:** Metadata rate limits
- **Where:** `src/Wondarr.Core/Metadata/Http/RequestSpacingGate.cs:39-61, 104-112`
- **What is wrong:** `WaitAsync` fixes each caller's slot when it is called. A later `DelayUntil` moves `_cooldownUntil`, but waiters already queued never re-check it. DECISIONS build session 11 #5 says the cooldown is one "that every caller honours".
- **Failure scenario:** Four MusicBrainz requests are queued at t+1 to t+4. The request at t gets a 503 with `Retry-After: 60`. The queued three still go out at t+1, t+2 and t+3, inside the cooldown.
- **Fix:** In `WaitUntilAsync`, re-read `_cooldownUntil` under the lock after each delay and move the slot past it.

#### R-029 — Waiting out a long cooldown in the background ends at the default 100-second `HttpClient` timeout
- **Severity:** Medium · **Status:** CONFIRMED (mechanism); the impact depends on the `Retry-After` hosts send · **Area:** Metadata rate limits / error handling
- **Where:** `src/Wondarr.Core/Metadata/MetadataServiceCollectionExtensions.cs:133-293` (no timeout set for MusicBrainz, CAA, Deezer or iTunes); `src/Wondarr.Core/Metadata/Http/RetryAfterHeader.cs:7` (cooldown capped at 300 s); `src/Wondarr.Core/ImportLists/PasteListService.cs:522-529`
- **What is wrong:** The 100-second default timeout wraps the whole handler chain: the gate wait, Polly's `Retry-After` delay (uncapped) and every retry.
- **Failure scenario:** MusicBrainz answers 503 with `Retry-After: 180`. For the next 3 minutes every background MusicBrainz call fails after 100 seconds with a timeout. `PasteListService` catches only `MetadataProviderException` and `HttpRequestException`, so the whole list run aborts.
- **Fix:** Give the metadata clients a timeout above `RetryAfterHeader.MaxWait`, or cap the cooldown below the timeout, and set Polly's `MaxDelay` to the same cap.

#### R-030 — A cover host that cannot be reached fails the whole song, album or list add
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** Metadata / error handling
- **Where:** `src/Wondarr.Core/Metadata/CoverArt/CoverArtResolver.cs:256-284`; callers `src/Wondarr.Core/Songs/SongService.cs:506, 625, 857, 1073-1144`
- **What is wrong:** The class promises that "a missing cover is a normal outcome", but only `MetadataProviderException` is caught. These all propagate:
  - `HttpRequestException` (DNS failure, connection refused, TLS error);
  - the timeout's `TaskCanceledException`;
  - `JsonException`.
  
  `ProviderKeys.IsProviderFailure` (`src/Wondarr.Core/Metadata/PartialSearch.cs:56`) already classifies all three, but the resolver does not use it.
- **Failure scenario:** itunes.apple.com is blocked by Pi-hole, or coverartarchive.org is unreachable. Every single add, album add, paste or import list run and "change album" throws before its transaction, so nothing is added.
- **Fix:** Catch with `ProviderKeys.IsProviderFailure(exception, cancellationToken)` in both helpers.

#### R-031 — `metadata_cache` grows without bound
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** Metadata cache / housekeeping
- **Where:** `src/Wondarr.Core/Metadata/MetadataCache.cs:141-153` (`PurgeExpiredAsync` has no caller outside tests); `src/Wondarr.Core/Housekeeping/HousekeepingCommandHandler.cs`
- **Failure scenario:** Cache keys include free text, ISRCs, Lucene queries and release-browse pages, so most keys are unique. Expired rows are overwritten only when the exact key is requested again. With release payloads of tens to hundreds of KB, a library of a few thousand songs adds hundreds of MB to `wondarr.db`, and it keeps growing. That also inflates every backup.
- **Fix:** Call `PurgeExpiredAsync` from Housekeeping, batched like the search-run delete.

#### R-032 — The missing-search backoff never reaches its weekly step
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** Search orchestration
- **Where:** `src/Wondarr.Core/Searching/MissingSearchCommandHandler.cs:162-173`; `src/Wondarr.Core/Searching/SearchBackoff.cs:150-178`
- **What is wrong:** Only runs from the last 168 hours are loaded, but the backoff step is the count of consecutive fruitless runs. Dropping older runs lowers the count.
- **Failure scenario:** With the defaults `[1,6,24,72,168]`, a song that is never found fails at 0, 6, 12, 36 and 108 hours. At 180 hours only 3 failures fall inside the window, so it is searched again then, 72 hours early. From there it settles at once every 24 to 72 hours, never weekly. Hopeless songs keep taking batch places and Soulseek budget. The existing test seeds runs one hour apart and does not catch this.
- **Fix:** Load runs back to the newest successful one (or store the consecutive-failure count) rather than using a time horizon.

#### R-033 — A failed status write leaves a command Queued or Started until restart, and dedupe then swallows every later enqueue of it
- **Severity:** Medium · **Status:** CONFIRMED (needs a database write failure) · **Area:** Command queue / executor
- **Where:** `src/Wondarr.Core/Jobs/CommandExecutor.cs:104-121, 132-141, 196`; `src/Wondarr.Core/Jobs/CommandQueue.cs:68-80, 93-100`
- **Failure scenario:**
  1. `MarkStartedAsync` for the scheduled MissingSearch throws `SQLITE_BUSY`, for example while Housekeeping's `VACUUM` holds the write lock.
  2. The row stays Queued and is never re-dispatched.
  3. Every 6-hour tick gets the stale row back from dedupe, so missing search silently stops until a restart. The same applies to Heartbeat, Backup and the others.
  - The existing test creates exactly this state and never checks that the command can run again.
- **Fix:** Retry the status write, then mark the row Failed. Alternatively, have dedupe ignore rows the executor does not hold in memory.

#### R-034 — Search-on-add for an album or list is merged into a MissingSearch that is already running, so the new songs are not searched
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** Queue dedupe / search on add
- **Where:** `src/Wondarr.Core/Songs/SongService.cs:977-985`; `src/Wondarr.Core/Jobs/CommandQueue.cs:68-80`; `src/Wondarr.Core/Searching/MissingSearchCommandHandler.cs:62`
- **Failure scenario:** The scheduled MissingSearch has spent hours waiting for download slots. The user adds a 12-track album, which queues `MissingSearch` with no body. Dedupe returns the running command, which chose its batch before these songs existed. Nothing searches them for up to 6 hours, contrary to DECISIONS build session 6 #3.
- **Fix:** Give the search-on-add command a body naming the song ids, or have the running loop re-select once its batch is done.

#### R-035 — The process-wide `SlotGate` is held across `GrabAsync`, which for YouTube runs the whole yt-dlp download
- **Severity:** Medium · **Status:** PLAUSIBLE · **Area:** Search concurrency
- **Where:** `src/Wondarr.Core/Searching/SongSearchService.cs:752-773, 991`; `src/Wondarr.Sources.YouTube/YouTubeSourceProvider.cs:191-197`; `src/Wondarr.Sources.YouTube/YtDlpOptions.cs:15`; `src/Wondarr.Core/Importing/QueueOptions.cs:19`
- **What is wrong:** There are two problems.
  - **The gate serialises grabs.** Every automatic grab waits on the static gate while a YouTube grab sleeps 10 to 20 seconds and downloads, for up to 600 seconds.
  - **The start timeout matches the download timeout.** The queue item has no handle during that time, and QueueTracker's 10-minute start timeout equals yt-dlp's 600-second timeout.
- **Failure scenario:** A slow YouTube grab holds up all automatic grabs process-wide, and can be failed as "Peer did not respond" while yt-dlp is still writing the file. The tracker then grabs the next candidate.
- **Fix:** Hold the gate only for "check the slot and insert the queue item". Exempt handle-less YouTube items from the start timeout, or make that timeout clearly longer than yt-dlp's.

#### R-036 — Changing the password or username does not end existing sessions
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** Authentication
- **Where:** `src/Wondarr.Api/Authentication/AuthenticationBuilderExtensions.cs:38-52` (7-day sliding cookie, no `OnValidatePrincipal`); `src/Wondarr.Api/Authentication/AuthenticationController.cs:243`; `src/Wondarr.Core/Authentication/CredentialStore.cs:63`
- **Failure scenario:** An owner who suspects a leaked login changes the password. The attacker's cookie keeps working indefinitely, because expiry slides, and keeps reading the API key through `/initialize.json`.
- **Fix:** Put a credential stamp in the ticket and reject it in `OnValidatePrincipal` when it no longer matches the stored credentials.

#### R-037 — `POST /login` is unthrottled and runs PBKDF2-SHA512 at 210,000 iterations
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** Authentication
- **Where:** `src/Wondarr.Api/Authentication/AuthenticationController.cs:73-83`; `src/Wondarr.Core/Authentication/CredentialStore.cs:14, 124`
- **Failure scenario:** On an exposed instance, about 20 bogus logins per second pin every core of a NAS and stall imports and the API, and password guessing proceeds without limit.
- **Fix:** Add ASP.NET Core rate limiting on `/login`: a per-IP window plus a global concurrency cap on hashing.

#### R-038 — Connection tests send stored secrets to a URL the caller supplies
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** Security / secrets
- **Where:** `src/Wondarr.Api/Soulseek/SoulseekController.cs:100-130`; the same pattern in the indexer, download-client and notification test and update paths (`NotificationSecrets.Merge`, `IndexerService.ResolveSettingsAsync`, `DownloadClientService.ResolveSettingsAsync`); the Plex token through `PUT /api/v1/plex/server`
- **What is wrong:** Secrets are masked on read so that an API-key holder cannot see them. But a test call with a new URL and an empty or masked secret sends the stored value to that URL.
- **Failure scenario:** `POST /api/v1/soulseek/test {"url":"http://attacker:5030/","apiKey":""}` delivers the stored slskd key. In the same way, a tracker API key, qBittorrent credentials or the Plex token can be read by anyone who holds Wondarr's key. With R-008 or R-009, that is anyone who gets through the local bypass.
- **Fix:** Use the stored secret only when the target host matches the stored row's host; otherwise require the secret in the request.

#### R-039 — Nothing stops Wondarr from running on a database whose schema is newer than the build
- **Severity:** Medium · **Status:** PLAUSIBLE (the harm depends on future migrations) · **Area:** Backup / persistence
- **Where:** `src/Wondarr.Core/Backup/BackupService.cs:308-343` (checks only that `__EFMigrationsHistory` exists); `src/Wondarr.Core/Persistence/DatabaseMigrator.cs:27`; `src/Wondarr.Core/HealthCheck/DatabaseHealthCheck.cs:191`
- **Failure scenario:**
  1. A 0.2 backup is restored into 0.1, or production is moved from `:0.2` back to `:0.1` after 0.2 migrated it.
  2. The restore succeeds and the app starts.
  3. Queries fail at runtime with "no such column", while Health reports the database as OK.
  4. The earlier database survives only as `*.pre-restore`, which the next restore overwrites.
- **Fix:** At staging, refuse a backup that has migrations this build does not know, naming its version. At startup, raise a health error when the live database has unknown migrations.

#### R-040 — The release workflow pushes `:0.1` and `:latest` before any test or smoke run
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** CI / release
- **Where:** `.github/workflows/release.yml:61-74` (`push: true` with the full tag list), `:78-105` (smoke runs afterwards against the pushed digest); the workflow has no `dotnet test` job
- **Failure scenario:** `v0.1.1` is tagged on a commit whose arm64 image does not start, or whose tests are red. `:0.1.1`, `:0.1` and `:latest` already point at it before the smoke job fails. Production follows `:0.1` (DECISIONS 2026-10-10 #7), so the next pull installs the broken image, and the in-app update badge stays silent because `github-release` was skipped.
- **Fix:** Push by digest or `sha-*` only. Promote the moving tags with `docker buildx imagetools create` in a job that needs both the smoke matrix and a test job.

#### R-041 — Health pushes over SignalR always throw, so the header health badge never updates live
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** Front end
- **Where:** `frontend/src/app/signalr.ts:52-53`; `src/Wondarr.Api/SignalR/SignalRBroadcaster.cs:35-36`; `src/Wondarr.Api/SignalR/SignalRMessage.cs:11-18`; `frontend/src/api/types.ts:28-31`
- **What is wrong:** The server sends `{name, body: {action, resource}}`, and the client passes `message.body` to `asHealthEntries`, which throws on anything that is not an array. The SignalR client only logs the error. `signalr.test.ts` passes the resource directly as `body`, a shape the server never sends.
- **Failure scenario:** slskd dies while the UI is open, and the badge keeps showing a green 0 until a reload. The Match nav badge has the same problem: it is invalidated only by the UI's own mutations, so a scheduled scan's new entries never show.
- **Fix:** Read `body.resource`, test against the real message shape, and give the header queries a modest `refetchInterval` as a fallback.

#### R-042 — Live updates stop for good after a restart longer than about 42 seconds, or when the first connect fails
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** Front end
- **Where:** `frontend/src/app/signalr.ts:79-87`
- **What is wrong:** `withAutomaticReconnect()` gives up after retrying at 0, 2, 10 and 30 seconds. There is no `onclose` handler, and a failed initial `start()` is swallowed.
- **Failure scenario:** After an image update (pull, recreate, migrate), an open tab's queue progress, import updates and update badge freeze silently until a manual reload.
- **Fix:** Use a never-ending capped retry policy, retry the initial start, invalidate all queries on reconnect, and optionally show a "live updates disconnected" indicator.

#### R-043 — The UI is unusable at phone width
- **Severity:** Medium · **Status:** CONFIRMED (Mantine 9.6.3 navbar variables) · **Area:** Front end
- **Where:** `frontend/src/components/AppLayout.tsx:91`
- **What is wrong:** The navbar has `breakpoint: 'sm'` but no `collapsed` state and no burger. Below 48em Mantine makes the navbar full-width with a zero main offset, so it covers every page and cannot be closed.
- **Fix:** Add a `Burger` with `navbar.collapsed.mobile` state, and close the navbar on navigation.

#### R-044 — `safe-merge.sh` can land a red merge through its "flaky" re-run
- **Severity:** Medium · **Status:** CONFIRMED in the script's logic; PLAUSIBLE that xUnit prints `[FAIL]` lines at this verbosity (the script's own comment says this path has fired) · **Area:** Scripts
- **Where:** `scripts/safe-merge.sh:52-68`
- **What is wrong:**
  - The failed-test regex `^\[xUnit\.net …\] +[A-Za-z0-9_.]+ \[FAIL\]` cannot match a theory case such as `Ns.Cls.Method(input: "x") [FAIL]` or a nested class `Outer+Inner.M`.
  - `grep -c 'Passed!' -lt 3` hard-codes three test projects.
- **Failure scenario:** A run has one flaky Fact and one genuinely broken Theory case. Only the Fact is re-run; it passes, "FLAKY" is printed, and the merge is committed with a failing theory.
- **Fix:** Accept a re-run only when the captured names account for the summary's `Failed:` totals (or parse a TRX log), match theory names up to `(`, and count the projects from the solution.

#### R-045 — ffmpeg and ffprobe come from a mutable Docker Hub tag
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** Docker / supply chain
- **Where:** `docker/Dockerfile:75-76` (compare the stage comment at `:63-64`)
- **What is wrong:** Every other third-party artifact is sha256-verified, but `COPY --from=mwader/static-ffmpeg:9.0.2` trusts a tag that can be re-pushed.
- **Failure scenario:** A re-pushed tag ships a different binary into every release image without notice. That binary processes every file downloaded from Soulseek strangers.
- **Fix:** Pin `mwader/static-ffmpeg:9.0.2@sha256:…` and record the digest in `docs/architecture/DEPLOYMENT.md`.

#### R-046 — The documented `%aunique` album-folder disambiguation does not exist, and ASCII folding cannot be turned on
- **Severity:** Medium · **Status:** CONFIRMED · **Area:** Naming / docs contradict code
- **Where:** `docs/architecture/LIBRARY_OUTPUT.md` §7.1; `src/Wondarr.Core/Organizer/NamingTemplate.cs:46`; `src/Wondarr.Core/Importing/LibraryOrganizer.cs:358-367` (always `new NamingOptions(Extension: …)`, so `AsciiFold` is never set)
- **Failure scenario:** Under the Plexamp preset `{Album Artist Name}/{Album Title}/…`, Weezer's self-titled albums (or an artist with a real release called "Singles" next to the pseudo-album) render the same folder. The files disagree on album id and date, so Plex merges or fragments the albums, and the first album's `cover.jpg` wins.
- **Fix:** When another album key already occupies the rendered folder, append the year or release-group disambiguation; otherwise correct the doc. Wire `AsciiFold` to a library setting, or remove it from the doc.

### Low

#### R-047 — A failed import still saves the organizer's album-context alignment
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Import
- **Where:** `src/Wondarr.Core/Importing/LibraryOrganizer.cs:203, 398-405`; `src/Wondarr.Core/Importing/ImportService.cs:714-748, 1094`
- **What is wrong:** `AlignWithFolderAsync` edits the tracked `AlbumContext` before tagging. Its doc says the import clears the tracker on failure, but `FailAsync` saves the same context.
- **Failure scenario:** A tagging or placement failure persists the song's re-filing under another folder's album values.
- **Fix:** Call `ChangeTracker.Clear()` (or reload the item) before `FailAsync` on these branches.

#### R-048 — A media-tool timeout is blamed on the file: it is blocklisted and deleted
- **Severity:** Low · **Status:** PLAUSIBLE · **Area:** Verification
- **Where:** `src/Wondarr.Core/Verification/DownloadVerifier.cs:135-138, 166-172`; `src/Wondarr.Core/Media/MediaProbe.cs:67-70, 98-101`; `src/Wondarr.Core/Media/Fingerprinter.cs:131-137, 154-157`
- **What is wrong:** An ffprobe, ffmpeg or fpcalc timeout becomes "Not decodable" or "Fingerprint failed", which the verifier treats as a rejection.
- **Failure scenario:** On a NAS loaded by a Plex transcode, the full decode check of a long FLAC passes 120 seconds. The good file is blocklisted for the song for good.
- **Fix:** Carry `TimedOut` through the results and map it to Deferred.

#### R-049 — APE, WavPack and WMA can be imported but not tagged, which causes a re-grab loop
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Import / tagging
- **Where:** `src/Wondarr.Core/Importing/ImportService.cs:90-93`; `src/Wondarr.Core/Decisions/DecisionEngine.cs:84-87`; `src/Wondarr.Core/Tagging/TagWriter.cs:621-628`
- **What is wrong:** These formats are accepted for import, but the tag writer throws `NotSupportedException` for them. DECISIONS build session 3 #8 says a tagging failure is never blocklisted.
- **Failure scenario:** Under a custom profile that allows these formats, the same best candidate is grabbed, downloaded and failed on every backoff, indefinitely.
- **Fix:** Refuse unsupported containers before tagging, or route them through the lossless conversion rule.

#### R-050 — An upgrade does not move the old file's lyrics sidecars
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Import / converter drift
- **Where:** `src/Wondarr.Core/Importing/ImportService.cs:690-712` (compare `src/Wondarr.Core/Media/FileConverter.cs:455, 644-674`)
- **What is wrong:** The converter moves sidecars when a file's name changes; the import's upgrade path does not.
- **Failure scenario:** The rendered name changes on upgrade (a quality token, or a collision suffix). The old `.lrc` is left next to nothing.
- **Fix:** Reuse `MoveSidecars` after a successful upgrade placement.

#### R-051 — The transcode and decode-check ffmpeg runs inherit stdin and have no `-n`
- **Severity:** Low · **Status:** PLAUSIBLE · **Area:** Process handling
- **Where:** `src/Wondarr.Core/Media/AudioTranscoder.cs:166`; `src/Wondarr.Core/Media/MediaProbe.cs:35`; `src/Wondarr.Core/Media/Fingerprinter.cs:118-126`
- **What is wrong:** These calls omit `-nostdin`, which the spectral, ReplayGain and cover calls already pass.
- **Failure scenario:** With a TTY attached and an existing target, ffmpeg prompts "Overwrite? [y/N]" and blocks until the 120-second kill.
- **Fix:** Add `-nostdin` everywhere, and `-n` on the transcoder.

#### R-052 — Concurrent manual backups or restores can produce a torn backup or a mismatched restore
- **Severity:** Low · **Status:** CONFIRMED (needs two requests at once; the UI button disables itself while pending) · **Area:** Backup
- **Where:** `src/Wondarr.Core/Backup/BackupService.cs:72-85, 104, 116-120, 288-301`; `src/Wondarr.Api/SystemInfo/BackupController.cs:56-58`
- **What is wrong:** The temporary database has a fixed name (`wondarr.db.tmp`), zip names have one-second resolution, the final move overwrites, and nothing serialises `CreateAsync` or `Stage()`.
- **Failure scenario:** Two overlapping `POST /api/v1/system/backup` calls on Linux. A zips B's half-written database and reports success; that backup fails its integrity check at restore time. Two overlapping restores pair one backup's database with the other's `config.yml`.
- **Fix:** Use a process-wide semaphore (or route backups through the command queue), GUID temporary names, and refuse to overwrite an existing zip.

#### R-053 — A crash between the two moves of an applied restore leaves the restored database with a fresh default `config.yml`
- **Severity:** Low · **Status:** CONFIRMED (a millisecond window) · **Area:** Backup
- **Where:** `src/Wondarr.Core/Backup/PendingRestore.cs:37-41, 59-60`; `src/Wondarr.Api/Program.cs:25-31`
- **What is wrong:** The next start acts only when both staged files exist.
- **Failure scenario:** After a crash between the two moves, a default `config.yml` with a new API key is generated. The *arr integrations break, and the user's real config sits unused in `restore/`.
- **Fix:** Write a journal file before the first move, or finish a half-applied restore at startup.

#### R-054 — Files holding secrets are created world-readable outside `ConfigFileWriter`
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Configuration / Docker
- **Where:** `src/Wondarr.Core/Configuration/ConfigFileInitializer.cs:113-117` (first-run `config.yml`, also non-atomic); `src/Wondarr.Core/Backup/BackupService.cs:102, 300` (backup zips, restored `config.yml`); `src/Wondarr.Sources.Slskd/SlskdHost.cs:579-589` (`slskd.yml` temp file chmodded after writing); `docker/root/etc/s6-overlay/s6-rc.d/svc-wondarr/run` (`umask ${UMASK:-002}`)
- **Failure scenario:** Any host account that can read the `/config` bind mount can read `backups/*.zip`, and with them the API key, the Soulseek password and the indexer keys. A crash between write and chmod leaves a world-readable `slskd.yml.<guid>.tmp`.
- **Fix:** Create these files with `UnixCreateMode = 0600`, write the first `config.yml` through `ConfigFileWriter`, and sweep stale `*.tmp` files at start.

#### R-055 — Log redaction misses a secret that contains `"` or `\`
- **Severity:** Low · **Status:** PLAUSIBLE · **Area:** Logging
- **Where:** `src/Wondarr.Core/Logging/RedactingTextFormatter.cs:21-27`; `src/Wondarr.Core/Logging/ISecretRegistry.cs:55-66`
- **What is wrong:** Redaction replaces the raw secret in output that has already been rendered as JSON, where `"` and `\` are escaped, so the raw form no longer matches.
- **Failure scenario:** A password such as `pa\ss"word` that appears in an exception message is written to `/config/logs/*.json` unredacted.
- **Fix:** Also register the JSON-escaped form of each secret.

#### R-056 — The pre-restore WAL is renamed so SQLite will not find it
- **Severity:** Low · **Status:** PLAUSIBLE · **Area:** Backup / docs
- **Where:** `src/Wondarr.Core/Backup/PendingRestore.cs:80-100`; `docs/architecture/DEPLOYMENT.md:79`
- **What is wrong:** `wondarr.db-wal` becomes `wondarr.db-wal.pre-restore`, but SQLite would look for `wondarr.db.pre-restore-wal`.
- **Failure scenario:** A user undoes a restore by renaming `wondarr.db.pre-restore` back. Writes that were not yet checkpointed are lost.
- **Fix:** Checkpoint (`wal_checkpoint(TRUNCATE)`) before setting the old database aside, and document how to undo a restore.

#### R-057 — Leftover temporary files are never swept, and the uploaded database entry is extracted with no size cap
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Backup
- **Where:** `src/Wondarr.Core/Backup/BackupService.cs:77, 218-219`
- **What is wrong:** Interrupted uploads, restores and backups leave `restore-upload-*.tmp` (up to 512 MB), `restore-database-*.tmp` and `*.zip.tmp` behind, and nothing cleans them up.
- **Failure scenario:** An authenticated zip bomb fills the `/config` volume shared with the live database.
- **Fix:** Sweep these patterns at start or in Housekeeping, and cap the bytes extracted.

#### R-058 — The login cookie lacks the Secure flag behind a TLS-terminating proxy
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Authentication
- **Where:** `src/Wondarr.Api/Authentication/AuthenticationBuilderExtensions.cs:38-47`; `src/Wondarr.Api/Program.cs:83`
- **What is wrong:** Forwarded headers are not processed, so the request scheme is always `http`, and the cookie is issued without `Secure`.
- **Failure scenario:** A plain `http://` hit on the public name, before the proxy's redirect, sends the session cookie in clear text.
- **Fix:** Add an option to always set `Secure`, or process `X-Forwarded-Proto` from known proxies only.

#### R-059 — No frame protection on the SPA
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Hardening
- **Where:** `src/Wondarr.Api` (no `X-Frame-Options` or CSP `frame-ancestors`)
- **Failure scenario:** Under the local bypass, any page a LAN user opens can frame the UI and clickjack bulk delete.
- **Fix:** Send `X-Frame-Options: DENY` (or `frame-ancestors 'none'`) on HTML responses.

#### R-060 — Bulk resolve breaks score ties in favour of undated recordings, the opposite of the UI ranking
- **Severity:** Low · **Status:** CONFIRMED (ordering); PLAUSIBLE (wrong pick) · **Area:** Identity
- **Where:** `src/Wondarr.Core/Identity/IdentityResolver.cs:640-645, 1278-1284` (compare `:939-947`)
- **What is wrong:** `RankAcceptable` and `ChooseByIsrc` sort `FirstReleaseDate ?? ""`, which puts recordings with no date first. Ties are common because scores clamp at 100.
- **Failure scenario:** A pasted line without a Deezer reference stores a standalone snippet's recording id instead of the album recording.
- **Fix:** Sort a missing date last, as `Rank` does. (This is separate from the already-known compilation ranking issue.)

#### R-061 — Provider responses are cached before they are known to parse
- **Severity:** Low · **Status:** PLAUSIBLE · **Area:** Metadata cache
- **Where:** `src/Wondarr.Core/Metadata/MusicBrainz/MusicBrainzClient.cs:252-256`; `src/Wondarr.Core/Metadata/Deezer/DeezerClient.cs:320-326, 353-357`; `src/Wondarr.Core/Metadata/ITunes/ITunesClient.cs:194-198`
- **What is wrong:** Any 2xx body is written to the cache first and deserialised afterwards.
- **Failure scenario:** A captive portal or an error page answers 200 with HTML. The release lookup throws `JsonException` for 30 days.
- **Fix:** Parse before `SetAsync`, and do not cache a body that fails.

#### R-062 — The Soulseek query ladder and banned-term wildcarding differ from MATCHING_ENGINE §6.4
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Docs contradict code
- **Where:** `docs/architecture/MATCHING_ENGINE.md` §6.4; `src/Wondarr.Sources.Slskd/SoulseekQueryBuilder.cs`
- **What is wrong:** There is no wildcarding of server-banned terms (`*inkin`), and the code builds no `title artist` or feat-stripped queries.
- **Failure scenario:** An artist on the server's excluded-phrases list is never found, and each attempt still spends 3 to 4 budget slots.
- **Fix:** Implement the wildcarding, or amend §6.4 together with a DECISIONS entry.

#### R-063 — slskd is always SIGKILLed, never asked to stop
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Process handling
- **Where:** `src/Wondarr.Sources.Slskd/SlskdHost.cs:503`; `src/Wondarr.Sources.Slskd/ProcessLauncher.cs:65-74`
- **Failure scenario:** On every settings restart and every shutdown, slskd cannot log out or flush its transfer state.
- **Fix:** Send SIGTERM, wait a few seconds, then kill the tree.

#### R-064 — A cancelled Soulseek search can still run in slskd after its budget lease is released
- **Severity:** Low · **Status:** PLAUSIBLE · **Area:** External limits
- **Where:** `src/Wondarr.Sources.Slskd/SlskdSearchRunner.cs:133-170, 200-235`
- **What is wrong:** On cancel, the search is deleted without being stopped first, and a delete that fails is only logged. In both cases the lease is released.
- **Failure scenario:** A third concurrent search can start while slskd is still running the cancelled one. (Soulseek.NET queues anything beyond 2, which limits the impact.)
- **Fix:** Stop the search before deleting it, and hold the lease until a re-poll shows it gone.

#### R-065 — yt-dlp exit code 101 is reported as cancellation
- **Severity:** Low · **Status:** CONFIRMED · **Area:** YouTube
- **Where:** `src/Wondarr.Sources.YouTube/YtDlpRunner.cs:195-198, 277-280`
- **Failure scenario:** Through R-023's path, a break rule aborts the caller's whole batch.
- **Fix:** Classify exit 101 as a `YtDlpException`.

#### R-066 — Draining process output after a kill has no time limit
- **Severity:** Low · **Status:** PLAUSIBLE · **Area:** Process handling
- **Where:** `src/Wondarr.Core/Media/ProcessRunner.cs` (`KillTree`, then `DrainAsync`)
- **Failure scenario:** A grandchild that escaped the tree kill keeps the pipe open. `DownloadAsync` never returns, and the YouTube semaphore (capacity 1) is held for good.
- **Fix:** Bound the drain with `WaitAsync`.

#### R-067 — Executor places can all be held by commands that only wait, and the take-back ignores the slot-wait deadline
- **Severity:** Low · **Status:** PLAUSIBLE · **Area:** Executor concurrency
- **Where:** `src/Wondarr.Core/Jobs/CommandExecutor.cs:20-23, 402-415`; `src/Wondarr.Core/Searching/MissingSearchCommandHandler.cs:236-276`; `src/Wondarr.Core/Searching/UpgradeSearchCommandHandler.cs:253-293`; `src/Wondarr.Core/Searching/SongSearchService.cs:699-707`
- **What is wrong:** The batch loops hold their place through unbounded slot waits. A yielded search takes its place back with no timeout. There is no deadlock: download slots free up on their own.
- **Failure scenario:** MissingSearch and UpgradeSearch are waiting for slots while a long ReferenceLibraryScan holds the third place. A user's "Search now" cannot start for hours.
- **Fix:** Let the batch loops yield while they wait, and re-check the deadline after the take-back.

#### R-068 — Command names are deduped case-sensitively but resolved case-insensitively
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Command queue
- **Where:** `src/Wondarr.Core/Jobs/CommandQueue.cs:69`; `src/Wondarr.Core/Jobs/CommandHandlerResolver.cs:194-195`; `src/Wondarr.Api/Commands/CommandController.cs:52-57`
- **Failure scenario:** `{"name":"missingsearch"}` while `MissingSearch` is running starts a second concurrent run over the same songs, and its result never updates the `job` row.
- **Fix:** Canonicalise the name to the handler's `Name` before dedupe.

#### R-069 — Cancelling a command can race the executor starting it
- **Severity:** Low · **Status:** PLAUSIBLE · **Area:** Command queue
- **Where:** `src/Wondarr.Core/Jobs/CommandQueue.cs:150-175`; `src/Wondarr.Core/Jobs/CommandExecutor.cs:269-289`
- **What is wrong:** `CancelAsync` and `MarkStartedAsync` run under different locks, so both can see the row as Queued.
- **Failure scenario:** The command runs while its row says Cancelled, its result is never recorded, and a duplicate enqueue is allowed.
- **Fix:** Use conditional updates (`WHERE status = 'Queued'`).

#### R-070 — The decision engine contradicts MATCHING_ENGINE §6.3
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Decision engine / docs
- **Where:** `src/Wondarr.Core/Searching/SongSearchService.cs:1243` (`SourceTier` is always 1); `src/Wondarr.Core/Decisions/DecisionEngine.cs:42-48, 544-582, 727-782`
- **What is wrong:**
  - YouTube and torrent/usenet candidates get +100 source preference instead of 60 or 30.
  - The ATV, OMV and UGC adjustments are not implemented, nor is the 400-point hard identity hit.
  - A perfect name match can score 400, so the held-identity ceiling's rationale in DECISIONS build session 7 #11 is wrong.
- **Failure scenario:** Interactive search ranks a YouTube or torrent candidate as highly as a Soulseek one of the same quality, and `min_score` judges them alike.
- **Fix:** Implement the missing terms, or amend §6.3.

#### R-071 — `job.next_run_at` disagrees with the Quartz trigger
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Scheduler
- **Where:** `src/Wondarr.Core/Jobs/JobTableUpdater.cs:350-351`; `src/Wondarr.Core/Jobs/ScheduledTaskService.cs:71-78`
- **Failure scenario:** After "Run now", Tasks shows the next run as end + interval, but Quartz still fires on its original schedule.
- **Fix:** Read the next fire time from the trigger.

#### R-072 — ConvertFiles and MoveSongs read the song before taking the per-song lock
- **Severity:** Low · **Status:** CONFIRMED (the effects are minor) · **Area:** Concurrency
- **Where:** `src/Wondarr.Core/Media/FileConverter.cs:286` vs `:307`; `src/Wondarr.Core/Profiles/SongLibraryMover.cs:116` vs `:150` (`ReplayGainApplier` does this correctly)
- **Failure scenario:** An upgrade lands at the same path while the converter waits for the lock. The file is then converted under the old source's rule, or tagged with stale ReplayGain values.
- **Fix:** Re-read the song inside the lock.

#### R-073 — The reference-owned rule is not re-checked at grab time or in bundling
- **Severity:** Low · **Status:** PLAUSIBLE · **Area:** Search (DECISIONS build session 11 #1)
- **Where:** `src/Wondarr.Core/Searching/SongSearchService.cs:267-269` vs `:646-773, 794-894`; `src/Wondarr.Core/Searching/ContainerBundler.cs:144-150`
- **Failure scenario:** A SongSearch waits up to 30 minutes for a slot while a reference scan links the song meanwhile, and it still grabs. Separately, a bundle pulls in a song owned through a reference row.
- **Fix:** Re-check ownership in `TryGrabNowAsync` and `GrabBestAsync`, and apply the Identified-row filter in the bundler.

#### R-074 — Unresolved import-list items are never retried, although the code says they are
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Import lists
- **Where:** `src/Wondarr.Core/ImportLists/ImportListService.cs:649-654`; `src/Wondarr.Core/ImportLists/PasteListService.cs:283`
- **Failure scenario:** An item whose ISRC MusicBrainz learns later stays unresolved until the user resolves it by hand.
- **Fix:** Reset `Unresolved` items whose `Raw` changed to `Pending`.

#### R-075 — A reference scan waiting on the per-library gate holds an executor place for the whole other scan
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Concurrency
- **Where:** `src/Wondarr.Core/References/ReferenceLibraryScanCommandHandler.cs:66`
- **Failure scenario:** "Scan now" during a first identification that takes days blocks one of the three places for that long.
- **Fix:** Yield the place while waiting, or answer "already scanning".

#### R-076 — A YouTube 429 or 5xx bypasses the list sync's failure path
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Import lists
- **Where:** `src/Wondarr.Core/ImportLists/ImportListService.cs:351-360` (`InnertubeException` is not caught)
- **Failure scenario:** The list's failure is never recorded, so it is retried every hour, against the documented "a failed read still counts as a sync".
- **Fix:** Catch `InnertubeException`, or map it in the provider.

#### R-077 — On Linux, a reference file name containing `\` gets the wrong absolute path
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Reference libraries
- **Where:** `src/Wondarr.Core/References/ReferenceScanner.cs:541-542`; `src/Wondarr.Core/References/ReferenceOwnership.cs:40-41`
- **Failure scenario:** For a file named `AC\DC.mp3`, fingerprinting and adoption look for `AC/DC.mp3` and fail on every run.
- **Fix:** Swap separators only where the platform's separator is `\`.

#### R-078 — A usenet grab reuses a job that was already trimmed for another song
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Usenet grab
- **Where:** `src/Wondarr.Sources.Torznab/Searching/IndexerSourceProvider.Grab.cs:423-429, 454-479, 537-544`
- **Failure scenario:** Song C reuses the job that was trimmed down to song A's file, then fails with "The song is not in the downloaded post".
- **Fix:** On reuse, check `get_files` for C's file, and add a fresh job when it was trimmed.

#### R-079 — Finding an existing usenet job by substring fails for guids that the JSON encoder escapes
- **Severity:** Low · **Status:** PLAUSIBLE · **Area:** Usenet bundling
- **Where:** `src/Wondarr.Sources.Torznab/Searching/IndexerSourceProvider.Grab.cs:643-675`; `src/Wondarr.Sources.Torznab/Searching/ContainerGrab.cs:34-40`
- **What is wrong:** The raw guid is searched for as a substring of the serialised JSON handle, where characters such as `&` and `+` are escaped.
- **Failure scenario:** A guid containing `&` or `+` never matches, so a bundled partner adds a second copy of the post.
- **Fix:** Compare on the deserialised value, or store the release id in a column.

#### R-080 — A hybrid magnet whose first `xt` is `btmh` is refused
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Magnet parsing
- **Where:** `src/Wondarr.Sources.Torznab/Clients/MagnetLink.cs:45-49`
- **Failure scenario:** `magnet:?xt=urn:btmh:…&xt=urn:btih:…` fails with "no info-hash Wondarr can use".
- **Fix:** Skip past `btmh` entries and look for `btih`.

#### R-081 — Failed grabs leave leftovers in the download clients
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Cleanup
- **Where:** `src/Wondarr.Sources.Torznab/Searching/IndexerSourceProvider.Grab.cs:204-215, 436-448, 541-543`; `src/Wondarr.Core/Importing/QueueTracker.cs:329-335`; `QBittorrentProxy._sessions`
- **Failure scenario:**
  - A selection that fails leaves the torrent stopped in qBittorrent.
  - A failed resume leaves the SABnzbd job paused.
  - A failed item never deletes its usenet job.
  - The qBittorrent session cache grows without limit.
- **Fix:** Remove what was just added (with its data only when Wondarr added it and nothing downloaded), clean up usenet jobs on a Failed status, and bound the session cache.

#### R-082 — Passkey-bearing download URLs are stored in the database, although a code comment says they are not
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Secrets
- **Where:** `src/Wondarr.Sources.Torznab/Searching/ReleasePushHandler.cs:297-306`; `src/Wondarr.Core/Searching/SongSearchService.cs:1089, 1334`; `src/Wondarr.Core/Sources/Candidate.cs:197-200`
- **What is wrong:** `ContainerRelease.DownloadUrl` and `MagnetUrl` are serialised into `candidate.normalised`. These include links from autobrr pushes and Torznab links carrying `apikey`.
- **Failure scenario:** The rows are kept for 30 days and travel in every backup, carrying the passkeys with them. The API does not expose them.
- **Fix:** Store an indexer id plus guid and re-resolve the URL at grab time, or correct the comment.

#### R-083 — Plex: the account token reaches the selected server, and probes send a token before checking which server answered
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Plex
- **Where:** `src/Wondarr.Core/Plex/PlexConnectionService.cs:298, 360-362, 401`
- **What is wrong:** `SelectServerAsync` sends the plex.tv account token rather than the server's own token. `ProbeAsync` sends the server token to every listed connection, including Docker bridge addresses, before it checks the machine identifier.
- **Failure scenario:** For a server shared by a friend, one request carries the user's master plex.tv token to the friend's server. A different host answering on 172.17.0.1 receives the server token.
- **Fix:** Probe `/identity` without a token first, and send a token only once the machine identifier matches.

#### R-084 — Storing a new Plex account token keeps a stale server token, which is still preferred
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Plex
- **Where:** `src/Wondarr.Core/Plex/PlexConnectionService.cs:219, 243, 574-575`
- **Failure scenario:** After "sign out of all devices" on plex.tv, a `PUT /api/v1/plex/token` (from the API or a script) keeps using the revoked server token, and every call gets 401.
- **Fix:** Clear `ServerToken` whenever a new account token is stored.

#### R-085 — The Plex health warning outlives its cause
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Plex
- **Where:** `src/Wondarr.Core/Plex/PlexLibraryUpdater.cs:488-493, 610-619`; `src/Wondarr.Core/Plex/PlexHealthCheck.cs:35-50`
- **Failure scenario:** "Plex refused to scan" stays on Health for days after the user re-signs in, until an import happens to trigger a scan.
- **Fix:** Clear `LastError` when the connection or token changes.

#### R-086 — Moves and conversions run as commands leave song lists stale
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Front end caches
- **Where:** `frontend/src/api/songs.ts:271, 927, 985`; `frontend/src/components/song/MoveSongModal.tsx:20-34`; `frontend/src/components/song/ConvertSongModal.tsx:89-109`; `frontend/src/pages/library/MassEditorBar.tsx:30-47`
- **What is wrong:** The song queries are invalidated when the API answers 202, before the command has run.
- **Failure scenario:** The mass editor reports "Moved 20 songs", but the table filtered to the old library still lists them.
- **Fix:** Invalidate the song queries when the polled command finishes, as `ConvertLibraryModal.tsx:100-104` does.

#### R-087 — A failed plain "Remove" in the queue is silent
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Front end
- **Where:** `frontend/src/pages/QueuePage.tsx:305, 341-347`
- **What is wrong:** The error renders only inside the confirm-removal modal, which is closed for a plain Remove.
- **Failure scenario:** Remove fails with a 409 or 500, and nothing is shown to the user.
- **Fix:** Show `remove.error` above the table, or as a notification.

#### R-088 — Dialogs carry the previous song's error or command into the next one
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Front end
- **Where:** `frontend/src/components/song/DeleteSongModal.tsx:18`; `frontend/src/components/song/MoveSongModal.tsx:19-21`; `frontend/src/pages/settings/LibrarySettingsPage.tsx:597`
- **Failure scenario:** Song A's 409 refusal shows when the user opens Delete for song B.
- **Fix:** Reset the mutation when the dialog opens, or key the modal by song id.

#### R-089 — Paged tables show the "empty" state and lose their pager when the page runs past the end
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Front end
- **Where:** `frontend/src/components/PagedTable.tsx:63-69, 124-128`
- **Failure scenario:** The user deletes every song on page 2 and sees "Nothing in the library matches these filters", with no pager to go back.
- **Fix:** When the rows are empty and `page > 1`, clamp to the last page.

#### R-090 — The init script's chown follows symlinks as root
- **Severity:** Low · **Status:** PLAUSIBLE · **Area:** Docker
- **Where:** `docker/root/etc/s6-overlay/scripts/init-wondarr-user:39`
- **What is wrong:** `find` checks the symlink's own owner, but `chown` without `-h` changes the target.
- **Failure scenario:** After a PUID change, a symlink under `/config` makes root chown its target outside `/config`.
- **Fix:** Use `chown -h`.

#### R-091 — Actions are pinned by tag, and `packages: write` is granted workflow-wide
- **Severity:** Low · **Status:** CONFIRMED · **Area:** CI
- **Where:** `.github/workflows/release.yml:12-14` and every `uses:` in the four workflows
- **What is wrong:** Third-party `docker/*` actions pinned by tag receive `GITHUB_TOKEN` with `packages: write`, and the smoke job inherits that permission.
- **Failure scenario:** A re-tagged action version runs with permission to publish images.
- **Fix:** Pin actions by SHA (with Dependabot), scope `packages: write` to the image job, and set `persist-credentials: false`.

#### R-092 — A backport tag moves `:latest` backwards
- **Severity:** Low · **Status:** CONFIRMED · **Area:** CI
- **Where:** `.github/workflows/release.yml:52-59` (`latest=auto`)
- **Failure scenario:** Tagging `v0.1.5` after `v0.2.0` exists points `:latest` at 0.1.5.
- **Fix:** Compute "is the highest release" in a step, and set `latest` from it.

#### R-093 — `check-notice.py` has blind spots (no violation today)
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Licensing tooling
- **Where:** `scripts/check-notice.py:12-14, 34`
- **What is wrong:** The script has four gaps:
  - The header match is case-sensitive and anchored to `// Ported from X (`. `src/Wondarr.Sources.YouTube/YouTubeSourceProvider.cs` ("The search strategy is ported from spotDL") is not detected; it passes only because its NOTICE row was added by hand.
  - It scans only `src/` and `frontend/src/`.
  - It reads only the first 2,000 characters of each file.
  - It never checks NOTICE rows against files that no longer exist.
- **Fix:** Match `(ported|adapted|derived) from` case-insensitively, widen the scanned roots, and add the NOTICE → file check.

#### R-094 — The WSL wrapper's cleanup trap never runs
- **Severity:** Low · **Status:** CONFIRMED · **Area:** Scripts
- **Where:** `scripts/wsl-safe-merge.sh:9, 26`
- **What is wrong:** `exec` replaces the shell, so the `EXIT` trap never fires.
- **Failure scenario:** Every run leaks a shim directory in `/tmp`.
- **Fix:** Drop the `exec`, or clean up inside `safe-merge.sh`.

**One more caller of an already-known issue.** Mirror's delete (`src/Wondarr.Core/ImportLists/ImportListService.cs:714-753`) decides on a snapshot and calls `SongService.DeleteAsync`, which has no busy check. So a grab or import finishing in between is cascade-deleted with the song. This is the known "single song delete vs in-flight downloads" problem, reached from a new, unattended caller.

## 3. Test-suite observations

### Runs (2026-10-11, commit `06238c7`, Windows dev box, shared test lock taken for each suite, `--blame-hang-timeout 4m`)

| Step | Result |
|---|---|
| `dotnet build -warnaserror` | Succeeded, 0 warnings |
| `Wondarr.Core.Tests` | 1807 passed, 0 failed, 3 skipped, 1 m 34 s |
| `Wondarr.Sources.Tests` | 916 passed, 0 failed, 8 skipped, 36 s |
| `Wondarr.Api.Tests` | 360 passed, 0 failed, 0 skipped, 1 m 16 s |
| `npm run lint` / `npm run typecheck` | Clean |
| `vitest run --maxWorkers=2` | 38 files, 293 tests passed, 165 s |
| `python scripts/check-notice.py` | Passed |

- **No hangs.** The blame collector reported no hang. The known flaky Api tests did not fail in this run.
- **The skipped tests are all opt-in live tests.** The three `MediaToolsLiveTests` skip when `ffprobe` and `fpcalc` are not on `PATH`. The `ci.yml` backend job installs neither, so the real ffprobe and fpcalc are exercised only by the image smoke test, never by `dotnet test`.
- **The slowest tests take 10–16 s each**, for example `SlskdHostTests.An_unexpected_exit_is_restarted_after_five_seconds_and_the_next_one_after_fifteen` (16 s), `ImportCompactionGuardTests`, `ReleaseApiTests`. The slskd harness drives a fake clock but steps it with real `Task.Delay(100)` calls. That adds wall time and makes the outcome depend on machine load.

### Tests that can pass with the bug present, or fail on a slow machine

- **Real-time sleeps decide timing assertions.**
  - Locations: `tests/Wondarr.Core.Tests/Notifications/NotificationDispatcherTests.cs:103, 141-147, 200, 224`; `tests/Wondarr.Core.Tests/Plex/PlexLibraryUpdaterTests.cs:72-73, 106-108, 130` (`Settle()` is `Task.Delay(5)`, `:496`); `tests/Wondarr.Core.Tests/Searching/SlotWaitTests.cs:222-225`.
  - **Negative checks pass with the bug present.** "Not sent yet" or "not scanned yet", checked after 5 ms, passes even if the quiet period is missing.
  - **Positive checks can flake.** `ContainSingle()` checks rely on a background scan finishing within a few 5 ms settles, so they can fail under load.
  - These are Core tests, separate from the known Api flakes.
- **Unbounded awaits.** `tests/Wondarr.Core.Tests/Importing/ImportCompactionGuardTests.cs:141, 146` and `tests/Wondarr.Sources.Tests/Slskd/SoulseekSearchBudgetTests.cs:64` await without `.WaitAsync(timeout)`. A deadlock regression would hang the whole run instead of failing one test.
- **A silent pass.** `tests/Wondarr.Core.Tests/References/ReferenceScannerTests.cs:355-364` returns early when symlinks cannot be created. On Windows without Developer Mode it reports a pass while never testing the symlink-loop guard. It should report as skipped.
- **Tests that seed states production never produces:**
  - The reference "went missing" tests (`MissingSearchCommandHandlerTests.Missing_search_picks_a_song_again_when_its_reference_file_went_missing`, `SongSearchServiceTests.A_song_whose_reference_file_went_missing_is_searched_for_again`) seed no `song_file`, which hides R-014.
  - `frontend/src/app/signalr.test.ts` sends a message shape the server never sends, which hides R-041.
  - The Gazelle bundle test sets `InfoHash` by hand, which hides R-005.
  - The Gazelle client tests bypass the resilience pipeline, which hides R-024.
  - `RequestSpacingCooldownTests` only tests a request sent after the cooldown, which hides R-028.
- **Tests touch real default paths outside the temp directory.**
  - `tests/Wondarr.Core.Tests/Importing/ImportTestHost.cs:181` registers the real `DiskOperations`, with the seeded library root `/data/music`. The organizer writes `cover.jpg.<guid>.partial` and lyrics sidecars beside the "placed" path. Tests pass only because `D:\data\music` does not exist on this machine, so the write throws and is swallowed.
  - This dev box does have `D:\data\downloads\slskd` (created 2026-09-29), so some test or local run has already created a default path on the drive root.
  - Point the seeded roots at the per-test temp folder.

### Risky paths with no test

- **Import and recycle:**
  - The recycle step throws after the move (R-001).
  - The recycle bin contains a library or reference root (R-002).
  - A YouTube transcode followed by a deferral or a rejection (R-013).
  - Cancellation or a chmod failure between placement and the database record (R-012).
- **Compaction:**
  - A song deleted while its move is staged; a resumed row that fails again (R-003).
  - Restart with unfinished rows (R-004).
  - `PUT albumcontext` during a move (R-015).
- **Torrents:**
  - A Gazelle grab of a torrent that is already in the client (R-005).
  - A token URL fetched at search time (R-006).
  - Cancel on a torrent Wondarr did not add (R-016).
  - `download_path` or the `moving` state (R-017).
- **Import lists and references:**
  - 0 rows or a short page under Mirror (R-007).
  - A Mirror-deleted item that comes back (R-019).
  - Concurrent syncs (R-020).
  - A Deezer-id collision in a batch add (R-022).
- **Authentication:**
  - `X-Real-IP`, `Forwarded` and `Via` on `/initialize.json` (R-008).
  - A foreign `Host` header (R-009).
  - A cookie issued before a password change (R-036).
  - Login throttling (R-037).
  - Test endpoints called with a changed host (R-038).
- **slskd:**
  - An `HttpClient` timeout, as opposed to caller cancellation, in the external monitor, `SlskdHost.PollAsync`, the search runner and the InnerTube search (R-010, R-011, R-023).
  - One failed poll after more than 60 s of running (R-011).
  - An orphaned slskd at start (R-027).
- **Executor and search:**
  - Backoff with runs spread over more than 168 h (R-032).
  - Re-enqueueing after a failed status write (R-033).
  - Search on add while MissingSearch runs (R-034).
- **Backup:**
  - Concurrent backup or restore (R-052).
  - Restoring a newer schema (R-039).
  - A crash between the two restore moves (R-053).
- **Migrations:** Only the Phase 0 upgrade is seeded with real data. `AddImportListSync`'s `UPDATE import_list_item SET position = id` has no data test.
- **Release workflow:** Nothing checks that the moving tags are published only after smoke and unit tests (R-040).

## 4. Questions for the owner

These are recorded decisions I would challenge, and ambiguities I could not settle from the docs.

**Authentication and secrets**

1. **The `disabledForLocalAddresses` default (ADR-0010; DECISIONS build session 11 #3).** It is the *arr default, but it is what turns R-008 and R-009 into "gets the API key". Should the default become `enabled` once a login exists, or should the app at least warn when the remote address is a Docker bridge gateway?
2. **`?apikey=` in download URLs (DECISIONS build session 6 #8).** Because the API does not take the login cookie, backup and log downloads put the key into browser history and proxy access logs. A short-lived signed download token would avoid that.
3. **Masked secrets on a host change (build session 8 #1, Phase 5).** Should a stored secret have to be entered again when an indexer's, client's, notification's or slskd's host changes (R-038)?
4. **Unmasked secrets for an unregistered type.** When an indexer type or list provider is no longer registered, stored secrets come back unmasked (`IndexerResources.cs:96-98`, `ImportListResources.cs:202`). Is that intended?
5. **Paths that are too broad.** `soulseek.shared_folders` and `downloads_dir` accept `/` and `/config`, and a library root of `/` or `/config` passes validation. Should those be refused?
6. **File modes.** Database files and backups are 0664 under `UMASK=002`, while `config.yml` is 0600. Which stance do you want?

**Plex and albums**

7. **Compact empties the whole section's trash (DECISIONS build session 4 #12).** It also purges unrelated trashed items, for example the tracks of a temporarily unmounted second location, along with their play counts and playlist entries. It does this even when the scan was never seen to finish. Should Wondarr check Plex's `autoEmptyTrash` setting and the section's other locations first, or at least say this in the Compact dialog?
8. **A pinned album choice never reaches disk (build session 4 #11).** For a song that already has a file, `PUT albumcontext` changes only the database. Compact never re-plans pinned songs, so the file keeps its old folder and tags. Should the PUT re-file the song?
9. **Deezer-only album adds are not pinned** (`AlbumService.cs:270`), while build session 7 #1 says an added album is pinned. Is that intended for albums with no MusicBrainz release?
10. **Sidecars are matched by file stem alone.** `X.flac` and `X.mp3` share `X.lrc`, and whichever song moves first takes it. Is that acceptable?

**References and import lists**

11. **Missing reference files (build session 11 #1).** Implementing "Missing makes the song wanted again" literally would trigger mass re-downloads when a share is half unmounted. Do you want a grace period, a per-scan threshold, and repointing when a file moves (R-014)?
12. **Deleting a reference library** makes every song owned only through it wanted, and searches them at once. Should that ask for confirmation, or offer to unmonitor instead?
13. **Returning items.** Should an item that comes back to a list re-monitor a song that AddAndUnmonitor or Mirror unmonitored (R-019)?
14. **User choices reset by a changed file.** A changed modification time resets a Skipped or Adopted reference row to Pending, overriding the user's choice. Is that intended?
15. **First reference scan versus import lists.** A first reference scan can take days, and meanwhile import lists add and search songs the user owns in files not yet identified. Should search on add wait while a reference library has pending rows?

**Search, decisions and upgrades**

16. **Tagging failures (build session 3 #8).** "A tagging failure is never blocklisted" makes a deterministic "format not supported" failure loop forever (R-049). Should structural failures blocklist?
17. **A rejected AcoustID key.** An `InvalidKey` from AcoustID defers every import indefinitely, and the songs count as in flight, so they are never searched. Should it fail after N deferrals, or raise a health issue?
18. **`RecoverAsync`** resets every `Importing` item to Completed without checking whether its file already landed (R-012). Should it check first?
19. **Free text in bulk.** Free-text bulk resolve (build session 2 #1) has no similarity floor against what was typed. A line such as "Hallelujah" resolves with full confidence to Deezer's first hit. Is auto-resolving free text in bulk intended?
20. **"Search now" above the cutoff.** `QualityProfile.IsUpgrade` ignores the cutoff, so "Search now" on a song already at MP3-320 still grabs FLAC. Lidarr stops at the cutoff. Which do you want?
21. **Push runs and backoff.** `SearchBackoff` counts Push runs, so frequent autobrr pushes that are rejected can push a song's Soulseek search out by up to a week. Is that intended?
22. **Executor size.** With `MaxConcurrency = 3` and two batch loops that can hold places for hours, should the loops yield like SongSearch (R-067)?
23. **The reasoning in build session 7 #11** is factually wrong: a perfect name match scores 400 (R-070). The behaviour is fine, but the reasoning should be corrected.
24. **No headroom at the rate limits.** MusicBrainz is spaced at exactly 1.000 s and AcoustID at exactly 333 ms. Network jitter can put two MusicBrainz arrivals inside one server-side second. Was a small margin (for example 1.05 s and 350 ms) deliberately rejected?

**Torrents and usenet**

25. **Build session 8 #6** removes the torrent with `deleteFiles: true` when nothing was downloaded. Should that be limited to torrents Wondarr added itself (R-016)?
26. **Build session 8 #5** says a usenet job is deleted once every song is "imported or failed". The code deletes it once every file is staged, before the import. Please confirm that is the intent.
27. **Minimum seeders.** Prowlarr results are not filtered by minimum seeders, while Torznab results are. Is that deliberate?

**Licensing and process**

28. **InnerTube attribution.** `InnertubeClient`, `InnertubeResponseParser`, `InnertubePlaylistParser` and `InnertubeModels` follow ytmusicapi (MIT) closely: the same params constants, paths and user agent. Build session 5 #2 records the protocol facts but not whether this counts as a port. Attribution is cheap.
29. **Sockseek's "bracket check"** is cited in `SoulseekFilenameParser.cs:27` and `Candidate.cs:62`. I found no copied code, only a re-implemented behaviour, which ADR-0002 allows. I mention it so the citation is a conscious choice.
30. **Backups around upgrades.** There is no automatic backup before migrations on upgrade, so a bad migration on a `:0.1` pull leaves only the weekly backup. Should `DatabaseMigrator` take an online copy when migrations are pending? Separately, should a restore keep more than one generation of `*.pre-restore`?
31. **VACUUM** skips only when another command is running. Writers outside the queue (QueueTracker imports, the Plex updater) can still hit "database is locked" during a long VACUUM. Is that acceptable?
32. **Flaky-test policy.** "A test that failed under load but passes alone is flaky" is built into `safe-merge.sh` but is not in DECISIONS. Is it policy?
33. **Shutdown grace.** s6-overlay's default grace period is 3 s and `docker stop` gives 10 s. Should `S6_SERVICES_GRACETIME`, `S6_KILL_GRACETIME` and the compose `stop_grace_period` be raised so an import in progress can finish?
34. **Concurrent Soulseek downloads.** `docs/architecture/DEPLOYMENT.md:48` says "≤ 3 concurrent downloads". Nothing in the Slskd project enforces it; Core's download slots may be the intended limit. Please confirm which number the doc means.
35. **Switching slskd mode.** Until the restart that a bundled ↔ external switch requires, the bundled supervisor and the external monitor both write `SlskdStatus`. Should a switch stop the bundled child at once?

## 5. Areas not reviewed

- **Not reviewed:**
  - `website/` (the Starlight docs site) and `unraid/`.
  - `tools/FakeSlskd`, which was checked only for licensing.
  - `scripts/worker.py`, `worker_agent.py` and `openrouter_value.py`: paused tooling, out of scope. No secret-leak check was done on them.
- **Read only partly:**
  - `docs/` beyond the binding decisions and the architecture specs.
  - The phase gate scripts: `phase2-gate.py` was read in full, the others skimmed for exit-code handling.
- **Generated code, skimmed:** `frontend/src/api/schema.d.ts` and `types.ts`.
- **Back end, not reached in depth:**
  - **Tagging:** `TagReader`, `TagSet`, `TagSetBuilder`, `CoverImage`.
  - **Naming:** the `NamingValues` builder, and the set-cover part of `AlbumPolicyEngine` (only its first 120 lines were read).
  - **YouTube:** `InnertubeResponseParser` and `InnertubePlaylistParser` internals, `YouTubeCandidateMapper`, `YtDlpErrorTaxonomy`.
  - **slskd:** `SlskdLogWatcher`.
  - **Torznab:** `TorznabRssParser`, `NewznabRssParser`, `NewznabQuery`, `ReleaseTitleParser`, `ReleaseTargets`.
  - **Notifications:** the Webhook, Discord and Apprise payload builders.
  - **Import lists:** the Last.fm and ListenBrainz list providers, `DeezerArtistTopProvider`.
  - **API controllers, bodies only skimmed:** `DownloadClientController`, `NotificationController`, `CustomFilter`, `Tag`, `History`, `Wanted`, `Blocklist`, `Health`, `QualityProfile`, `ReferenceLibraryController`, `SongConvertController`.
  - **Persistence:** the EF model snapshot line by line (I relied on the migration tests and EF's pending-changes check), and the cascade rules in `DomainModelConfiguration` beyond `search_run`, `candidate` and `queue_item`.
- **Front end, not reached in depth:** `InteractiveSearchModal`, `AddAlbumTab`, `ImportListsSettingsPage`, `SongHeader`, `FileTab`.
- **Tests, sampled rather than read:**
  - Bodies not read: most of `Wondarr.Api.Tests`, the Torznab and YouTube tests in `Wondarr.Sources.Tests`, `BackupServiceTests`, `QueueTrackerTests`, `ImportServiceTests`.
  - These were found by grep sweeps: sleeps, catch blocks, environment variables, ports and statics.
