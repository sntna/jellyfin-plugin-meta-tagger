#!/usr/bin/env python3
"""Create and exercise only a labelled, generated Jellyfin acceptance server."""

import argparse
import datetime as dt
import hashlib
import json
import os
import pathlib
import re
import secrets
import shutil
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
PLUGIN_ID = "4851185f-7284-4cad-9eeb-2c73576bb214"
LABEL = "org.meta-tagger.disposable"
REUSABLE_CONTAINER = "jellyfin-plugin-meta-tagger"


def docker(*args, capture=True):
    context = os.environ.get("META_TAGGER_DOCKER_CONTEXT", "orbstack")
    command = ["docker"]
    if context:
        command.extend(["--context", context])
    return subprocess.run([*command, *args], check=True,
                          text=True, capture_output=capture).stdout


def docker_host_args():
    return [] if os.environ.get("META_TAGGER_DOCKER_CONTEXT", "orbstack") else ["--add-host", "host.docker.internal:host-gateway"]


class Server:
    def __init__(self, directory):
        self.directory = pathlib.Path(directory).resolve()
        self.meta = json.loads((self.directory / "disposable.json").read_text())
        if self.meta["purpose"] != LABEL:
            raise RuntimeError("This directory is not a generated disposable test environment")
        self.version = self.meta.get("serverVersion", "12.0.0")
        if self.version not in {"10.11.10", "12.0.0"}:
            raise RuntimeError("Only the two verified acceptance server versions are supported")
        self.base = self.meta["url"]
        if urllib.parse.urlparse(self.base).hostname != "127.0.0.1":
            raise RuntimeError("Test environment must use the loopback address")
        inspected = json.loads(docker("inspect", self.meta["container"]))[0]
        if inspected["Config"]["Labels"].get(LABEL) != self.meta["id"]:
            raise RuntimeError("Container does not have this test environment's unique label")
        bindings = inspected["HostConfig"]["PortBindings"]["8096/tcp"]
        if not bindings or not all(b["HostIp"] == "127.0.0.1" for b in bindings):
            raise RuntimeError("Container's server port is not bound exclusively to loopback")
        if not any(int(b["HostPort"]) == urllib.parse.urlparse(self.base).port for b in bindings):
            raise RuntimeError("Test URL does not match the disposable container's published port")
        if not any(m["Source"] == str(self.directory / "config") and m["Destination"] == "/config"
                   for m in inspected["Mounts"]):
            raise RuntimeError("Container's configuration is not this test environment's generated directory")
        self.token = None
        self.user_id = None

    def api(self, path, body=None, method=None, auth=True, raw=False):
        headers = {"Content-Type": "application/json",
                   "Authorization": 'MediaBrowser Client="Meta Tagger Acceptance", Device="Disposable tests", DeviceId="meta-tagger-acceptance", Version="0.1.0"'}
        if auth and self.token:
            headers["Authorization"] += f', Token="{self.token}"'
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(self.base + path, data=data, headers=headers,
                                         method=method or ("POST" if body is not None else "GET"))
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                value = response.read()
                if raw:
                    return value.decode()
                return json.loads(value) if value else None
        except urllib.error.HTTPError as error:
            detail = error.read().decode(errors="replace")[:1000]
            raise RuntimeError(f"{request.method} {path}: HTTP {error.code}: {detail}") from None

    def wait_ready(self):
        for _ in range(120):
            try:
                info = self.api("/System/Info/Public", auth=False)
                if "Version" not in info:
                    time.sleep(1)
                    continue
                if info["Version"] != self.version:
                    raise ValueError(f"This test environment requires Jellyfin {self.version}")
                return info
            except (OSError, RuntimeError):
                time.sleep(1)
        raise RuntimeError("Disposable Jellyfin did not start within two minutes")

    def login(self):
        credentials = json.loads((self.directory / "credentials.json").read_text())
        result = self.api("/Users/AuthenticateByName", {
            "Username": credentials["username"], "Pw": credentials["password"]}, auth=False)
        self.token = result["AccessToken"]
        self.user_id = result["User"]["Id"]

    def evidence(self, name, value):
        output = self.directory / "evidence"
        output.mkdir(exist_ok=True)
        (output / (name + ".json")).write_text(json.dumps(value, indent=2) + "\n")

    def items(self):
        result = self.api("/Items?Recursive=true&IncludeItemTypes=Movie,Series,Episode&Fields=Tags,Genres,Studios,ProductionYear,OfficialRating,CustomRating,Path,ProviderIds,LockedFields,ProductionLocations")
        return {item["Id"]: item for item in result["Items"]}

    def tags(self):
        return {key: sorted(item.get("Tags", [])) for key, item in self.items().items()}

    def filter_tags(self):
        # V12 Filters2 never fills Tags. Legacy Filters does, when scoped to a library.
        catalogs = {}
        for library in self.api("/Library/VirtualFolders"):
            query = urllib.parse.urlencode({"userId": self.user_id, "parentId": library["ItemId"],
                                          "includeItemTypes": "Movie,Series,Episode"})
            endpoint = "/Items/Filters?" + query
            catalogs[library["Name"]] = {"endpoint": endpoint, "tags": sorted(self.api(endpoint)["Tags"])}
        return catalogs

    def configure(self, **changes):
        path = f"/Plugins/{PLUGIN_ID}/Configuration"
        configuration = self.api(path)
        configuration.update(changes)
        self.api(path, configuration)
        return self.api(path)

    def task(self, key):
        task, before = self.start_task(key)
        return self.finish_task(task, before)

    def start_task(self, key):
        task = next(t for t in self.api("/ScheduledTasks") if t["Key"] == key)
        before = task.get("LastExecutionResult")
        self.api(f"/ScheduledTasks/Running/{task['Id']}", method="POST")
        return task, before

    def finish_task(self, task, before):
        for _ in range(240):
            current = self.api(f"/ScheduledTasks/{task['Id']}")
            result = current.get("LastExecutionResult")
            if current["State"] == "Idle" and result and result != before:
                assert result["Status"] == "Completed", current
                return self.api("/MetaTagger/Preview")["Summary"]
            time.sleep(.25)
        raise RuntimeError(f"Task {task['Key']} did not complete within 60 seconds")

    def update_item(self, item_id, **changes):
        item = self.api(f"/Users/{self.user_id}/Items/{item_id}")
        item.update(changes)
        self.api(f"/Items/{item_id}", item)
        return self.api(f"/Users/{self.user_id}/Items/{item_id}")

    def restart(self):
        docker("restart", self.meta["container"])
        self.wait_ready()
        self.login()

    def denied(self, path, body=None, method=None, status=401, auth=True):
        try:
            self.api(path, body, method, auth)
        except RuntimeError as error:
            assert f"HTTP {status}:" in str(error), str(error)
            return status
        raise AssertionError(f"Expected HTTP {status} from {path}")


def nfo(path, root, **fields):
    element = ET.Element(root)
    for key, values in fields.items():
        for value in values if isinstance(values, list) else [values]:
            ET.SubElement(element, key).text = str(value)
    ET.indent(element)
    path.write_bytes(ET.tostring(element, encoding="utf-8", xml_declaration=True))


def find_reusable_server():
    names = docker("ps", "-a", "--format", "{{.Names}}").splitlines()
    if REUSABLE_CONTAINER not in names:
        return None
    inspected = json.loads(docker("inspect", REUSABLE_CONTAINER))[0]
    config = next((m["Source"] for m in inspected["Mounts"] if m["Destination"] == "/config"), None)
    if config is None:
        raise RuntimeError("Reusable container has no generated configuration mount")
    directory = pathlib.Path(config).resolve().parent
    if not directory.is_relative_to((ROOT / ".jellyfin-test").resolve()):
        raise RuntimeError("Reusable container is not owned by this repository")
    server = Server(directory)
    if server.meta["container"] != REUSABLE_CONTAINER or server.version != "12.0.0":
        raise RuntimeError("Reusable container metadata or Jellyfin version does not match")
    return server


def create(args):
    if getattr(args, "server_version", "12.0.0") != "12.0.0":
        raise RuntimeError("The reusable server requires Jellyfin 12. Use test-upgrade for an isolated migration check.")
    server = find_reusable_server()
    if server is not None:
        port = urllib.parse.urlparse(server.meta["url"]).port
        if args.port != port:
            raise RuntimeError(f"Reusable Jellyfin already uses port {port}; use --port {port}.")
        docker("update", "--restart", "unless-stopped", REUSABLE_CONTAINER)
        docker("start", REUSABLE_CONTAINER)
        server.wait_ready()
        print(f"Reusing {server.directory}\nURL: {server.base}\nCredentials: {server.directory / 'credentials.json'}")
        return server.directory
    return create_generated(args, REUSABLE_CONTAINER)


def create_generated(args, container_name=None):
    identity = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%d-%H%M%S-") + secrets.token_hex(3)
    directory = ROOT / ".jellyfin-test" / ("v12-" + identity)
    directory.mkdir(parents=True, exist_ok=False)
    for child in ("config", "cache", "media", "evidence"):
        (directory / child).mkdir()
    version = getattr(args, "server_version", "12.0.0")
    image_tag = "12.0" if version == "12.0.0" else version
    meta = {"purpose": LABEL, "id": identity, "container": container_name or "meta-tagger-v12-" + identity,
            "url": f"http://127.0.0.1:{args.port}", "image": "jellyfin/jellyfin:" + image_tag,
            "serverVersion": version}
    (directory / "disposable.json").write_text(json.dumps(meta, indent=2) + "\n")
    credentials = {"url": meta["url"], "username": "tagger-admin", "password": secrets.token_urlsafe(24)}
    secret_path = directory / "credentials.json"
    secret_path.write_text(json.dumps(credentials, indent=2) + "\n")
    secret_path.chmod(0o600)
    docker("run", "--rm", "--network", "none", "--entrypoint", "/usr/lib/jellyfin-ffmpeg/ffmpeg",
           "-v", f"{directory / 'media'}:/media", meta["image"], "-hide_banner", "-loglevel", "error",
           "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24", "-f", "lavfi", "-i", "sine=frequency=440",
           "-t", "3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart",
           "/media/generated-sample.mp4")
    media = directory / "media"
    movie = media / "Movies" / "Paper Satellites (2024)"
    movie.mkdir(parents=True)
    shutil.copyfile(media / "generated-sample.mp4", movie / "Paper Satellites (2024).mp4")
    nfo(movie / "movie.nfo", "movie", title="Paper Satellites", year=2024,
        plot="Generated local test movie. A research crew maps paper satellites.",
        genre=["Science Fiction", "Adventure"], mpaa="PG", studio="Workshop Pictures",
        country="United States", tag=["friendship", "handpicked", "manual:collection:weekend", "custom:genre:unowned"])
    for library, name, genre, rating, country, studio in [
        ("Series", "Harbor Workshop", "Documentary", "TV-G", "Canada", "Harbor Studio"),
        ("Anime", "Moonlit Parcel", "Animation", "TV-PG", "Japan", "Paper Moon Animation")]:
        series = media / library / name
        season = series / "Season 01"
        season.mkdir(parents=True)
        nfo(series / "tvshow.nfo", "tvshow", title=name, year=2025, genre=genre, mpaa=rating,
            studio=studio, country=country, tag=["creative", "manual:collection:series"])
        for number, title in [(1, "First Delivery"), (2, "The Quiet Workshop")]:
            stem = f"{name} - S01E{number:02} - {title}"
            shutil.copyfile(media / "generated-sample.mp4", season / (stem + ".mp4"))
            nfo(season / (stem + ".nfo"), "episodedetails", title=title, season=1, episode=number,
                aired=f"2025-01-{number:02}", plot="Generated local acceptance-test episode.",
                tag=["teamwork", "manual:collection:episode"])
    (media / "generated-sample.mp4").unlink()
    docker("run", "-d", "--name", meta["container"], "--restart", "unless-stopped", *docker_host_args(),
           "--label", f"{LABEL}={identity}",
           "-p", f"127.0.0.1:{args.port}:8096", "-v", f"{directory / 'config'}:/config",
           "-v", f"{directory / 'cache'}:/cache", "-v", f"{media}:/media:ro", meta["image"])
    server = Server(directory)
    server.evidence("image", json.loads(docker("image", "inspect", meta["image"])))
    setup(argparse.Namespace(directory=directory))
    return directory


def setup(args):
    server = Server(args.directory)
    directory = server.directory
    secret_path = directory / "credentials.json"
    credentials = json.loads(secret_path.read_text())
    server.evidence("server-initial", server.wait_ready())
    server.api("/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US",
                                        "PreferredMetadataLanguage": "en"}, auth=False)
    server.api("/Startup/User", auth=False)
    server.api("/Startup/User", {"Name": credentials["username"], "Password": credentials["password"]}, auth=False)
    server.api("/Startup/RemoteAccess", {"EnableRemoteAccess": False, "EnableAutomaticPortMapping": False}, auth=False)
    server.api("/Startup/Complete", method="POST", auth=False)
    server.login()
    providers = ["TheMovieDb", "The Open Movie Database", "TheTVDB", "TMDb", "OMDb", "AniDB", "AniList"]
    for name, collection in [("Movies", "movies"), ("Series", "tvshows"), ("Anime", "tvshows")]:
        options = {"EnableRealtimeMonitor": False, "EnableChapterImageExtraction": False,
                   "ExtractChapterImagesDuringLibraryScan": False, "EnableInternetProviders": False,
                   "SaveLocalMetadata": False, "EnableAutomaticSeriesGrouping": False,
                   "PathInfos": [{"Path": "/media/" + name}], "MetadataSavers": [],
                   "TypeOptions": [{"Type": kind, "MetadataFetchers": [], "ImageFetchers": [],
                                    "DisabledMetadataFetchers": providers, "DisabledImageFetchers": providers}
                                   for kind in ["Movie", "Series", "Season", "Episode"]]}
        server.api("/Library/VirtualFolders?" + urllib.parse.urlencode({"name": name,
                   "collectionType": collection, "refreshLibrary": "false"}), {"LibraryOptions": options})
    server.api("/Library/Refresh", method="POST")
    for _ in range(120):
        items = server.api("/Items?Recursive=true&IncludeItemTypes=Movie,Series,Episode&Fields=Tags,Genres,Studios,ProductionYear,OfficialRating,CustomRating,Path,ProviderIds,LockedFields")
        if len(items["Items"]) == 7:
            break
        time.sleep(1)
    assert len(items["Items"]) == 7, items
    server.evidence("seed-items", items)
    server.evidence("libraries", server.api("/Library/VirtualFolders"))
    print(f"Created {directory}\nURL: {server.base}\nCredentials: {secret_path}")


def install(args):
    server = Server(args.directory)
    dll = pathlib.Path(args.dll).resolve()
    assert dll.name == "Jellyfin.Plugin.MetaTagger.dll" and dll.is_file()
    docker("stop", server.meta["container"])
    destination = server.directory / "config/plugins/Meta Tagger_0.1.0.0"
    destination.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(dll, destination / dll.name)
    shutil.copyfile(ROOT / "docs/brand/assets/plugin.png", destination / "meta-tagger.png")
    metadata = {"category": "General", "guid": PLUGIN_ID, "name": "Meta Tagger",
                "description": "Generates normalized tags from library metadata.", "owner": "Local development",
                "overview": "Disposable acceptance-test build", "version": "0.1.0.0", "targetAbi": server.version + ".0",
                "status": "Active", "autoUpdate": False, "assemblies": [dll.name], "imagePath": "meta-tagger.png"}
    (destination / "meta.json").write_text(json.dumps(metadata, indent=2) + "\n")
    server.evidence("installed-artifact", {"sha256": hashlib.sha256(dll.read_bytes()).hexdigest(),
                                           "metadata": metadata})
    docker("start", server.meta["container"])
    server.wait_ready()
    server.login()
    plugins = server.api("/Plugins")
    server.evidence("plugins", plugins)
    match = next(p for p in plugins if p["Id"].lower().replace("-", "") == PLUGIN_ID.replace("-", ""))
    assert match["Status"] == "Active", match
    assert match["Version"] == "0.1.0.0", match
    html = server.api("/web/ConfigurationPage?name=Meta%20Tagger", raw=True)
    assert "MetaTaggerConfigPage" in html and len(html) > 1000
    (server.directory / "evidence/dashboard-installed.html").write_text(html)
    server.evidence("tasks", server.api("/ScheduledTasks"))
    print(f"Plugin 0.1.0.0 loaded and active on Jellyfin {server.version}; dashboard HTML returned successfully")


def test_basic(args):
    server = Server(args.directory)
    server.wait_ready()
    server.login()
    results = []

    def passed(name, detail):
        results.append({"scenario": name, "passed": True, "detail": detail})
        server.evidence("basic-results", results)
        print("PASS", name, flush=True)

    initial = server.items()
    original_tags = server.tags()
    movie = next(x["Id"] for x in initial.values() if x["Type"] == "Movie")
    anime = next(x["Id"] for x in initial.values() if x["Name"] == "Moonlit Parcel")
    anime_episode = next(x["Id"] for x in initial.values() if x.get("SeriesName") == "Moonlit Parcel" and x["IndexNumber"] == 1)
    episodes = [x["Id"] for x in initial.values() if x["Type"] == "Episode"]
    config = server.api(f"/Plugins/{PLUGIN_ID}/Configuration")
    assert config["PreviewOnly"] is True
    passed("fresh install defaults to preview", {"PreviewOnly": True, "items": len(initial)})
    server.configure(GeneratedTagPrefix="custom", EnableExistingTagsAsKeywords=True,
                     IncludeParentSeriesMetadataOnEpisodes=True, StaleTagMode="Keep", PreviewOnly=True,
                     EnableProductionCountries=True, EnableProviderIds=True, QuietLogging=True)
    server.update_item(movie, ProviderIds={"Acceptance": "synthetic-movie-001"}, CustomRating="PG-13")
    summary = server.task("MetaTaggerPreviewTags")
    preview = server.api("/MetaTagger/Preview")
    server.evidence("initial-preview", preview)
    assert server.tags() == original_tags
    assert summary["WritesApplied"] == 0 and summary["EstimatedWrites"] == 7
    assert preview["Status"] == "Ready" and len(preview["Changes"]) == 7
    passed("preview reports seven item changes without changing stored tags", summary)
    summary = server.task("MetaTaggerApplyTags")
    tagged = server.tags()
    expected = {"custom:genre:science-fiction", "custom:genre:adventure", "custom:rating:pg-13",
                "custom:keyword:friendship", "custom:studio:workshop-pictures", "custom:country:united-states",
                "custom:provider:acceptance", "custom:year:2024"}
    assert expected.issubset(tagged[movie]), tagged[movie]
    assert "custom:rating:pg" not in tagged[movie]
    assert {"custom:genre:animation", "custom:rating:tv-pg", "custom:studio:paper-moon-animation"}.issubset(tagged[anime_episode]), tagged[anime_episode]
    assert all(set(original_tags[key]).issubset(value) for key, value in tagged.items())
    assert summary["WritesApplied"] == 7
    assert_no_recursive_keywords(tagged)
    server.evidence("initial-applied-items", server.items())
    passed("custom generated tags, CustomRating override, and episode parent metadata", tagged)
    passed("manual, ordinary, and unowned generated-lookalike tags survive apply", original_tags)
    summary = server.task("MetaTaggerApplyTags")
    assert server.tags() == tagged and summary["WritesApplied"] == 0
    assert summary["ItemsSkippedUnchanged"] == 7
    assert_no_recursive_keywords(server.tags())
    passed("repeat apply is idempotent", summary)
    server.restart()
    assert server.tags() == tagged
    summary = server.task("MetaTaggerApplyTags")
    assert summary["WritesApplied"] == 0 and summary["ItemsSkippedUnchanged"] == 7
    passed("tags and ledger survive server restart", summary)
    server.configure(StaleTagMode="Remove")
    server.update_item(movie, Genres=["Mystery"], CustomRating="", OfficialRating="R")
    summary = server.task("MetaTaggerPreviewTags")
    assert server.tags() == tagged
    preview = server.api("/MetaTagger/Preview")
    change = next(c for c in preview["Changes"] if c["ItemId"] == movie)
    assert {"custom:genre:science-fiction", "custom:rating:pg-13"}.issubset(change["RemovedTags"])
    passed("changed metadata previews owned stale tag removal", change)
    server.task("MetaTaggerApplyTags")
    changed = server.tags()
    assert "custom:genre:mystery" in changed[movie] and "custom:rating:r" in changed[movie]
    assert not {"custom:genre:science-fiction", "custom:genre:adventure", "custom:rating:pg-13"}.intersection(changed[movie])
    assert "custom:genre:unowned" in changed[movie]
    passed("stale removal removes only owned tags", changed[movie])
    for key, lock in zip(episodes[:3], [{"LockData": True}, {"LockedFields": ["Tags"]},
                                      {"Tags": [*changed[episodes[2]], "manual:tagger:skip"]}]):
        server.update_item(key, **lock)
    locked_tags = server.tags()
    server.configure(GeneratedTagPrefix="review")
    summary = server.task("MetaTaggerApplyTags")
    after_locks = server.tags()
    assert all(after_locks[key] == locked_tags[key] for key in episodes[:3])
    assert summary["ItemsSkippedLocked"] == 2 and summary["ItemsSkippedManual"] == 1, summary
    passed("item lock, Tags lock, and manual skip stop writes", summary)
    assert "review:genre:mystery" in after_locks[movie]
    assert "custom:genre:mystery" not in after_locks[movie]
    assert "custom:genre:unowned" in after_locks[movie]
    assert_no_recursive_keywords(after_locks)
    passed("prefix rename removes ledger-owned old-prefix tags only", after_locks[movie])
    for key in episodes[:3]:
        item = server.items()[key]
        server.update_item(key, LockData=False, LockedFields=[],
                           Tags=[t for t in item["Tags"] if t != "manual:tagger:skip"])
    server.configure(GeneratedTagPrefix="custom")
    server.task("MetaTaggerApplyTags")
    assert_no_recursive_keywords(server.tags())
    before_budget = server.tags()
    server.configure(GeneratedTagPrefix="budget", MaxWritesPerRun=1)
    summary = server.task("MetaTaggerApplyTags")
    after_budget = server.tags()
    assert sum(before_budget[k] != after_budget[k] for k in before_budget) == 1
    assert summary["WritesApplied"] == 1 and summary["BudgetLimitReached"] is True
    passed("write budget limits actual mutations to one item", summary)
    server.configure(GeneratedTagPrefix="custom", MaxWritesPerRun=0)
    server.task("MetaTaggerApplyTags")
    assert_no_recursive_keywords(server.tags())
    server.configure(StaleTagMode="Preview")
    server.update_item(movie, Genres=["Science Fiction", "Adventure"], OfficialRating="PG", CustomRating="PG-13")
    before_preview_stale = server.tags()
    server.task("MetaTaggerPreviewTags")
    preview = server.api("/MetaTagger/Preview")
    assert server.tags() == before_preview_stale
    change = next(c for c in preview["Changes"] if c["ItemId"] == movie)
    assert "custom:genre:mystery" in change["PreviewRemovedTags"] and not change["RemovedTags"]
    passed("stale preview mode reports proposed removals without scheduling removal", change)
    server.evidence("basic-final-items", server.items())


def assert_no_recursive_keywords(tags):
    nested = {key: [tag for tag in values if re.search(
        r":keyword:(custom|review|budget|race)-(genre|country|rating|studio|provider|year|keyword)-", tag)
        and not tag.endswith(":keyword:custom-genre-unowned")]
              for key, values in tags.items()}
    nested = {key: values for key, values in nested.items() if values}
    assert not nested, f"Generated tags were mirrored recursively as keywords: {nested}"


def reset_fixtures(args):
    """Restore only the synthetic fixture DTOs recorded by this server's create phase."""
    server = Server(args.directory)
    server.wait_ready()
    server.login()
    seed = json.loads((server.directory / "evidence/seed-items.json").read_text())["Items"]
    assert {x["Id"] for x in seed} == set(server.items())
    for item in seed:
        assert item["Path"].startswith("/media/")
        server.update_item(item["Id"], Name=item["Name"], Tags=item["Tags"], Genres=item["Genres"],
                           OfficialRating=item.get("OfficialRating"), CustomRating=None,
                           ProviderIds=item.get("ProviderIds", {}), LockData=False, LockedFields=[])
    server.configure(IsEnabled=True, GeneratedTagPrefix="custom", ManualTagPrefix="manual", TagSeparator=":",
                     PreviewOnly=True, StaleTagMode="Remove", IncludeParentSeriesMetadataOnEpisodes=True,
                     EnableGenres=True, EnableParentalRating=True, EnableExistingTagsAsKeywords=True,
                     EnableStudios=True, EnableProductionCountries=True, EnableProviderIds=True,
                     EnableProductionYear=True, MaxKeywordTagsPerItem=0, ExcludedKeywordPrefixes="",
                     DefaultRunMode="Incremental", RunAfterLibraryScan=False,
                     IncludeMovies=True, IncludeSeries=True, IncludeEpisodes=True, IncludeVideos=True,
                     MaxItemsPerRun=0, MaxWritesPerRun=0, MaxRunMinutes=0, WriteDelayMilliseconds=0,
                     ForceFullScanOnNextRun=False, RebuildTrackingLedgerOnNextRun=False,
                     ClaimExistingGeneratedTagsForCleanup=False, ClaimExistingGeneratedTagsOnNextRun=False)
    preview = server.api("/MetaTagger/Cleanup/Preview", {"ItemId": None})
    if preview.get("Token"):
        server.api("/MetaTagger/Cleanup/Apply", {"Token": preview["Token"]})
    assert server.tags() == {x["Id"]: sorted(x["Tags"]) for x in seed}
    print("Restored the seven generated fixture items", flush=True)


def test_cleanup(args):
    server = Server(args.directory)
    server.wait_ready()
    server.login()
    results = []

    def passed(name, detail):
        results.append({"scenario": name, "passed": True, "detail": detail})
        server.evidence("cleanup-results", results)
        print("PASS", name, flush=True)

    server.configure(IsEnabled=True, GeneratedTagPrefix="custom", PreviewOnly=True,
                     StaleTagMode="Remove", MaxItemsPerRun=0, MaxWritesPerRun=0,
                     IncludeMovies=True, IncludeSeries=True, IncludeEpisodes=True, IncludeVideos=True)
    server.task("MetaTaggerApplyTags")
    original = server.tags()
    items = server.items()
    original_catalogs = assert_filter_catalogs_match_items(server)
    movie = next(k for k, x in items.items() if x["Type"] == "Movie")
    episodes = [k for k, x in items.items() if x["Type"] == "Episode"]
    endpoints = [("/MetaTagger/Preview", None),
                 ("/MetaTagger/Cleanup/Items?searchTerm=Paper", None),
                 ("/MetaTagger/Cleanup/Preview", {"ItemId": movie}),
                 ("/MetaTagger/Cleanup/Apply", {"Token": "not-an-approved-preview"})]
    for path, body in endpoints:
        server.denied(path, body, auth=False)
    assert server.tags() == original
    passed("unauthenticated callers cannot preview, search, or apply cleanup", {"endpoints": 4, "status": 401})
    username = "tagger-viewer-" + secrets.token_hex(3)
    password = secrets.token_urlsafe(24)
    viewer = server.api("/Users/New", {"Name": username, "Password": password})
    assert viewer["Policy"]["IsAdministrator"] is False
    authentication = server.api("/Users/AuthenticateByName", {"Username": username, "Pw": password}, auth=False)
    admin_token = server.token
    server.token = authentication["AccessToken"]
    for path, body in endpoints:
        server.denied(path, body, status=403)
    server.token = admin_token
    assert server.tags() == original
    passed("ordinary viewers cannot preview, search, or apply cleanup", {"endpoints": 4, "status": 403})
    policy = viewer["Policy"]
    policy["BlockedTags"] = ["custom:genre:animation"]
    server.api(f"/Users/{viewer['Id']}/Policy", policy)
    server.token = authentication["AccessToken"]
    visible = server.api(f"/Items?UserId={viewer['Id']}&Recursive=true&IncludeItemTypes=Movie,Series,Episode")["Items"]
    server.token = admin_token
    blocked = {k for k, x in items.items() if x.get("SeriesName") == "Moonlit Parcel" or x["Name"] == "Moonlit Parcel"}
    assert movie in {x["Id"] for x in visible}
    assert blocked.isdisjoint({x["Id"] for x in visible}), visible
    passed("Jellyfin user tag restrictions hide the anime series and its episodes", {"blockedItemIds": sorted(blocked), "visibleItemIds": [x["Id"] for x in visible]})
    matches = server.api("/MetaTagger/Cleanup/Items?searchTerm=Paper")
    assert movie in {x["ItemId"].replace("-", "") for x in matches}, matches
    assert server.api("/MetaTagger/Cleanup/Items?searchTerm=P") == []
    preview = server.api("/MetaTagger/Cleanup/Preview", {"ItemId": movie})
    assert server.tags() == original and len(preview["Changes"]) == 1
    removals = set(preview["Changes"][0]["RemovedTags"])
    assert removals and "custom:genre:unowned" not in removals
    server.evidence("single-cleanup-preview", preview)
    summary = server.api("/MetaTagger/Cleanup/Apply", {"Token": preview["Token"]})
    cleared = server.tags()
    assert set(cleared[movie]) == set(original[movie]) - removals
    assert all(cleared[k] == original[k] for k in original if k != movie)
    assert summary["WritesApplied"] == 1
    passed("single-item preview and clear remove exactly approved owned tags", {"summary": summary, "remainingTags": cleared[movie]})
    server.denied("/MetaTagger/Cleanup/Apply", {"Token": preview["Token"]}, status=409)
    assert server.tags() == cleared
    empty = server.api("/MetaTagger/Cleanup/Preview", {"ItemId": movie})
    assert empty.get("Token") is None and empty["Changes"] == []
    passed("cleanup tokens are single-use and already-cleared items have no changes", {"replayedTokenStatus": 409, "emptyPreview": empty})
    server.task("MetaTaggerApplyTags")
    original = server.tags()
    preview = server.api("/MetaTagger/Cleanup/Preview", {"ItemId": movie})
    server.configure(QuietLogging=True)
    server.denied("/MetaTagger/Cleanup/Apply", {"Token": preview["Token"]}, status=409)
    assert server.tags() == original
    preview = server.api("/MetaTagger/Cleanup/Preview", {"ItemId": movie})
    server.task("MetaTaggerPreviewTags")
    server.denied("/MetaTagger/Cleanup/Apply", {"Token": preview["Token"]}, status=409)
    assert server.tags() == original
    passed("settings changes and intervening tag runs invalidate cleanup approval", {"status": 409})
    preview = server.api("/MetaTagger/Cleanup/Preview", {"ItemId": movie})
    server.update_item(movie, Tags=[*original[movie], "manual:added:after-preview", "new-user-tag"])
    summary = server.api("/MetaTagger/Cleanup/Apply", {"Token": preview["Token"]})
    assert {"manual:added:after-preview", "new-user-tag", "custom:genre:unowned"}.issubset(server.tags()[movie])
    assert set(server.tags()[movie]) == (set(original[movie]) - set(preview["Changes"][0]["RemovedTags"])) | {"manual:added:after-preview", "new-user-tag"}
    passed("manual and ordinary tags added after preview survive cleanup", summary)
    server.update_item(movie, Tags=[t for t in server.tags()[movie] if t not in ["manual:added:after-preview", "new-user-tag"]])
    server.task("MetaTaggerApplyTags")
    original = server.tags()
    server.update_item(episodes[0], LockData=True)
    server.update_item(episodes[1], LockedFields=["Tags"])
    server.update_item(episodes[2], Tags=[*original[episodes[2]], "manual:tagger:skip"])
    original = server.tags()
    preview = server.api("/MetaTagger/Cleanup/Preview", {"ItemId": None})
    assert server.tags() == original
    assert preview["Summary"]["ItemsSkippedLocked"] == 2 and preview["Summary"]["ItemsSkippedManual"] == 1
    changes = {c["ItemId"]: set(c["RemovedTags"]) for c in preview["Changes"]}
    assert len(changes) == 4
    summary = server.api("/MetaTagger/Cleanup/Apply", {"Token": preview["Token"]})
    after = server.tags()
    assert all(set(after[k]) == set(original[k]) - changes.get(k, set()) for k in original)
    assert all(after[k] == original[k] for k in episodes[:3])
    server.evidence("library-cleanup-with-locks", {"preview": preview, "summary": summary, "storedTags": after})
    passed("library cleanup respects item locks, Tags locks, and manual skip", summary)
    for key in episodes[:3]:
        server.update_item(key, LockData=False, LockedFields=[], Tags=[t for t in after[key] if t != "manual:tagger:skip"])
    server.configure(IsEnabled=False, IncludeMovies=False, IncludeSeries=False, IncludeEpisodes=False, IncludeVideos=False)
    before = server.tags()
    preview = server.api("/MetaTagger/Cleanup/Preview", {"ItemId": None})
    assert server.tags() == before and len(preview["Changes"]) == 3
    summary = server.api("/MetaTagger/Cleanup/Apply", {"Token": preview["Token"]})
    after = server.tags()
    assert "custom:genre:unowned" in after[movie]
    assert not any(tag.startswith("custom:") for key, tags in after.items() for tag in tags if tag != "custom:genre:unowned")
    assert all(any(tag.startswith("manual:") for tag in tags) for tags in after.values())
    final_catalogs = assert_filter_catalogs_match_items(server)
    assert all(set(original_catalogs[name]["tags"]) - set(catalog["tags"])
               for name, catalog in final_catalogs.items())
    server.evidence("cleanup-tag-filter-catalogs", {"before": original_catalogs, "after": final_catalogs})
    passed("cleanup remains available with generation and media types disabled", {"summary": summary, "storedTags": after})
    empty = server.api("/MetaTagger/Cleanup/Preview", {"ItemId": None})
    assert empty.get("Token") is None and empty["Changes"] == []
    passed("repeated library cleanup is a no-op", empty)
    server.configure(IsEnabled=True, IncludeMovies=True, IncludeSeries=True, IncludeEpisodes=True, IncludeVideos=True)
    server.task("MetaTaggerApplyTags")
    server.evidence("cleanup-final-items", server.items())


def assert_filter_catalogs_match_items(server):
    items = server.items()
    catalogs = server.filter_tags()
    for name, catalog in catalogs.items():
        library_path = "/media/" + name + "/"
        expected = {tag for item in items.values() if item["Path"].startswith(library_path)
                    for tag in item["Tags"]}
        assert set(catalog["tags"]) == expected, {"library": name, "expected": sorted(expected), "actual": catalog["tags"]}
    return catalogs


def test_cleanup_filters(args):
    """Run only the library-clear catalog subcase, then restore normal generated tags."""
    server = Server(args.directory)
    server.wait_ready()
    server.login()
    original = server.tags()
    before = assert_filter_catalogs_match_items(server)
    assert all(any(tag.startswith("custom:") for tag in catalog["tags"]) for catalog in before.values())
    preview = server.api("/MetaTagger/Cleanup/Preview", {"ItemId": None})
    assert len(preview["Changes"]) == 7 and server.tags() == original
    removals = {change["ItemId"]: set(change["RemovedTags"]) for change in preview["Changes"]}
    summary = server.api("/MetaTagger/Cleanup/Apply", {"Token": preview["Token"]})
    after_tags = server.tags()
    assert all(set(after_tags[key]) == set(tags) - removals[key] for key, tags in original.items())
    after = assert_filter_catalogs_match_items(server)
    removed_options = {}
    for name, catalog in after.items():
        removed_options[name] = sorted(set(before[name]["tags"]) - set(catalog["tags"]))
        assert removed_options[name]
        assert any(tag.startswith("manual:") for tag in catalog["tags"])
        assert not any(tag.startswith("custom:") and tag != "custom:genre:unowned" for tag in catalog["tags"])
    assert "custom:genre:unowned" in after["Movies"]["tags"]
    server.evidence("cleanup-tag-filter-catalogs", {"before": before, "after": after,
                                                   "removedOptions": removed_options, "summary": summary})
    restored_summary = server.task("MetaTaggerApplyTags")
    assert server.tags() == original
    restored = assert_filter_catalogs_match_items(server)
    assert restored == before
    server.evidence("cleanup-tag-filter-restored", {"summary": restored_summary, "catalogs": restored})
    print("PASS library clear removes owned tags from all three populated filter catalogs; manual/unowned options remain and regeneration restores original options", flush=True)


def test_lifecycle(args):
    server = Server(args.directory)
    server.wait_ready()
    server.login()
    original = server.tags()
    ledger = server.directory / "config/plugins/Jellyfin.Plugin.MetaTagger/meta-tagger-state.json"
    ownership = {key: sorted(value["lastAppliedTags"]) for key, value in json.loads(ledger.read_text())["items"].items()}
    preview = server.api("/MetaTagger/Cleanup/Preview", {"ItemId": None})
    assert preview["Token"]
    server.api(f"/Plugins/{PLUGIN_ID}/0.1.0.0/Disable", method="POST")
    server.restart()
    plugins = server.api("/Plugins")
    plugin = next(p for p in plugins if p["Name"] == "Meta Tagger")
    assert plugin["Status"] == "Disabled", plugin
    assert server.tags() == original
    server.denied("/MetaTagger/Preview", status=404)
    server.evidence("lifecycle-disabled", plugin)
    print("PASS plugin disable persists across restart without changing tags", flush=True)
    server.api(f"/Plugins/{PLUGIN_ID}/0.1.0.0/Enable", method="POST")
    server.restart()
    plugin = next(p for p in server.api("/Plugins") if p["Name"] == "Meta Tagger")
    assert plugin["Status"] == "Active" and plugin["Version"] == "0.1.0.0"
    assert server.tags() == original
    server.denied("/MetaTagger/Cleanup/Apply", {"Token": preview["Token"]}, status=409)
    summary = server.task("MetaTaggerApplyTags")
    assert summary["WritesApplied"] == 0 and server.tags() == original
    assert {key: sorted(value["lastAppliedTags"]) for key, value in json.loads(ledger.read_text())["items"].items()} == ownership
    server.evidence("lifecycle-results", {"plugin": plugin, "summary": summary, "storedTags": original,
                                           "restartInvalidatedCleanupToken": True})
    print("PASS re-enable restores plugin with intact ledger; restart invalidates cleanup token", flush=True)


def wait_package_installation(server, version):
    plugin_directory = server.directory / "config/plugins" / f"Meta Tagger_{version}"
    for _ in range(120):
        if (plugin_directory / "Jellyfin.Plugin.MetaTagger.dll").is_file() and (plugin_directory / "meta.json").is_file():
            return
        time.sleep(.5)
    raise RuntimeError("Plugin package installation did not finish within 60 seconds")


def install_catalog_package(server, manifest_url, version):
    repositories = [entry for entry in (server.api("/Repositories") or [])
                    if entry.get("Name") != "Meta Tagger Acceptance"]
    repositories.append({"Name": "Meta Tagger Acceptance", "Url": manifest_url, "Enabled": True})
    server.api("/Repositories", repositories)
    query = urllib.parse.urlencode({"AssemblyGuid": PLUGIN_ID, "version": version,
                                    "repositoryUrl": manifest_url})
    server.api("/Packages/Installed/Meta%20Tagger?" + query, method="POST")
    wait_package_installation(server, version)


def test_release_lifecycle(args):
    server = Server(args.directory) if args.directory else find_reusable_server()
    if server is None:
        raise RuntimeError("Create the generated Jellyfin server before running the release lifecycle test")
    server.wait_ready()
    server.login()
    server.restart()
    package_path = pathlib.Path(args.package).resolve()
    assert package_path.is_file()
    with zipfile.ZipFile(package_path) as package:
        assert package.namelist() == ["Jellyfin.Plugin.MetaTagger.dll", "meta-tagger.png"]
        assert package.read("meta-tagger.png") == (ROOT / "docs/brand/assets/plugin.png").read_bytes()
    package_hash = hashlib.sha256(package_path.read_bytes()).hexdigest()

    installed_ids = {plugin["Id"].lower().replace("-", "") for plugin in server.api("/Plugins")}
    removed_existing = False
    if PLUGIN_ID.replace("-", "") in installed_ids:
        server.api(f"/Plugins/{PLUGIN_ID}", method="DELETE")
        removed_existing = True
    if removed_existing:
        server.restart()

    # Jellyfin retains plugin settings after uninstall. Start this fresh-install
    # check with defaults even when the generated acceptance server is reused.
    (server.directory / "config/plugins/configurations/Jellyfin.Plugin.MetaTagger.xml").unlink(missing_ok=True)

    initial_version = args.previous_version if args.previous_manifest_url else args.version
    initial_manifest = args.previous_manifest_url or args.manifest_url
    install_catalog_package(server, initial_manifest, initial_version)
    server.restart()
    plugin = next(p for p in server.api("/Plugins") if p["Name"] == "Meta Tagger")
    assert plugin["Status"] == "Active" and plugin["Version"] == initial_version, plugin
    initial_configuration = server.api(f"/Plugins/{PLUGIN_ID}/Configuration")
    assert initial_configuration["PreviewOnly"] is True
    server.configure(GeneratedTagPrefix="upgrade", QuietLogging=True, PreviewOnly=True)
    configured = server.api(f"/Plugins/{PLUGIN_ID}/Configuration")

    if args.previous_manifest_url:
        install_catalog_package(server, args.manifest_url, args.version)
        server.restart()
        plugin = next(p for p in server.api("/Plugins") if p["Name"] == "Meta Tagger")
        assert plugin["Status"] == "Active" and plugin["Version"] == args.version, plugin
        after_upgrade = server.api(f"/Plugins/{PLUGIN_ID}/Configuration")
        assert after_upgrade["GeneratedTagPrefix"] == "upgrade"
        assert after_upgrade["QuietLogging"] is True
    else:
        after_upgrade = configured

    server.api(f"/Plugins/{PLUGIN_ID}/{args.version}/Disable", method="POST")
    server.restart()
    plugin = next(p for p in server.api("/Plugins") if p["Name"] == "Meta Tagger")
    assert plugin["Status"] == "Disabled", plugin
    server.api(f"/Plugins/{PLUGIN_ID}/{args.version}/Enable", method="POST")
    server.restart()
    after_enable = server.api(f"/Plugins/{PLUGIN_ID}/Configuration")
    assert after_enable["GeneratedTagPrefix"] == "upgrade"
    assert after_enable["QuietLogging"] is True

    tags_before_uninstall = server.tags()
    server.api(f"/Plugins/{PLUGIN_ID}", method="DELETE")
    server.restart()
    assert not any(p["Id"].lower().replace("-", "") == PLUGIN_ID.replace("-", "")
                   for p in server.api("/Plugins"))
    assert server.tags() == tags_before_uninstall

    install_catalog_package(server, args.manifest_url, args.version)
    server.restart()
    plugin = next(p for p in server.api("/Plugins") if p["Name"] == "Meta Tagger")
    assert plugin["Status"] == "Active" and plugin["Version"] == args.version
    after_reinstall = server.api(f"/Plugins/{PLUGIN_ID}/Configuration")
    assert after_reinstall["PreviewOnly"] is True
    server.evidence("release-zip-lifecycle", {
        "packageSha256": package_hash,
        "freshInstallPreviewOnly": initial_configuration["PreviewOnly"],
        "upgradeRetainedConfiguration": after_upgrade["GeneratedTagPrefix"] == "upgrade",
        "disableEnableRetainedConfiguration": after_enable["GeneratedTagPrefix"] == "upgrade",
        "uninstallPreservedMediaTags": server.tags() == tags_before_uninstall,
        "reinstallPreviewOnly": after_reinstall["PreviewOnly"],
    })
    print("PASS catalog ZIP install, upgrade, disable, enable, uninstall, and reinstall lifecycle", flush=True)


def test_recovery(args):
    server = Server(args.directory)
    server.wait_ready()
    server.login()
    server.configure(StaleTagMode="Remove", RunAfterLibraryScan=False)
    server.task("MetaTaggerApplyTags")
    before = server.tags()
    ledger = server.directory / "config/plugins/Jellyfin.Plugin.MetaTagger/meta-tagger-state.json"
    backup = ledger.with_name(ledger.name + ".bak")
    good = ledger.read_bytes()
    original_backup = backup.read_bytes()
    results = []
    movie = next(k for k, x in server.items().items() if x["Type"] == "Movie")
    movie_dto = server.api(f"/Users/{server.user_id}/Items/{movie}")
    try:
        for name, faulty in [("missing primary", None), ("JSON null primary", b"null")]:
            backup.write_bytes(good)
            if faulty is None:
                ledger.unlink()
            else:
                ledger.write_bytes(faulty)
            summary = server.task("MetaTaggerApplyTags")
            assert server.tags() == before
            assert summary["WritesApplied"] == 0 and summary["ItemsSkippedUnchanged"] == 7
            assert len(json.loads(ledger.read_text())["items"]) == len(json.loads(good)["items"])
            results.append({"scenario": name + " recovers ownership from backup", "summary": summary})
            print("PASS", results[-1]["scenario"], flush=True)
        ledger.write_text("{invalid primary")
        backup.write_text("{invalid backup")
        server.update_item(movie, Genres=["Recovery Test Genre"])
        summary = server.task("MetaTaggerApplyTags")
        assert summary["PreviewOnly"] is True and summary["WritesApplied"] == 0
        assert summary["EstimatedWrites"] > 0 and server.tags() == before
        results.append({"scenario": "unreadable primary and backup force preview during apply", "summary": summary})
        print("PASS", results[-1]["scenario"], flush=True)
        server.evidence("recovery-results", results)
    finally:
        ledger.write_bytes(good)
        backup.write_bytes(original_backup)
        server.api(f"/Items/{movie}", movie_dto)
    summary = server.task("MetaTaggerApplyTags")
    assert server.tags() == before and summary["WritesApplied"] == 0


def test_concurrent(args):
    server = Server(args.directory)
    server.wait_ready()
    server.login()
    server.configure(StaleTagMode="Remove", GeneratedTagPrefix="custom", WriteDelayMilliseconds=0)
    server.task("MetaTaggerApplyTags")
    initial = server.items()
    original = server.tags()
    movie = next(k for k, x in initial.items() if x["Type"] == "Movie")
    locked = sorted(initial)[-1]
    assert movie != locked and sorted(initial).index(movie) >= 3
    movie_dto = server.api(f"/Users/{server.user_id}/Items/{movie}")
    server.configure(GeneratedTagPrefix="race", WriteDelayMilliseconds=1000)
    task, before = server.start_task("MetaTaggerApplyTags")
    for _ in range(100):
        if any(t.startswith("race:") for tags in server.tags().values() for t in tags):
            break
        time.sleep(.05)
    else:
        raise RuntimeError("Did not observe the first write of the delayed test run")
    server.update_item(movie, Name="Paper Satellites - Edited during tagging", Genres=["Concurrent Mystery"],
                       Tags=[*original[movie], "manual:concurrent:keep", "concurrent-user-tag"])
    server.update_item(locked, LockData=True)
    protected = {key for key, item in initial.items() if key == locked or item.get("SeriesId") == locked}
    protection_snapshot = server.tags()
    summary = server.finish_task(task, before)
    after = server.tags()
    updated_movie = server.api(f"/Users/{server.user_id}/Items/{movie}")
    assert after[locked] == original[locked]
    assert all(after[key] == protection_snapshot[key] for key in protected)
    # A newly locked series also locks descendants that have not yet been processed.
    assert summary["ItemsSkippedLocked"] >= 1
    assert {"race:genre:concurrent-mystery", "manual:concurrent:keep", "concurrent-user-tag"}.issubset(after[movie])
    assert "race:genre:science-fiction" not in after[movie]
    assert updated_movie["Name"] == "Paper Satellites - Edited during tagging"
    assert updated_movie["Genres"] == ["Concurrent Mystery"]
    server.evidence("concurrent-results", {"summary": summary, "movie": updated_movie,
                                             "lockedItemId": locked, "lockedTags": after[locked],
                                             "protectedTags": {key: after[key] for key in protected}})
    print("PASS queued items retain concurrent admin tags/metadata and honor a new lock", flush=True)
    server.update_item(movie, Name=movie_dto["Name"], Genres=movie_dto["Genres"],
                       Tags=[t for t in after[movie] if t not in ["manual:concurrent:keep", "concurrent-user-tag"]])
    server.update_item(locked, LockData=False)
    server.configure(GeneratedTagPrefix="custom", WriteDelayMilliseconds=0)
    server.task("MetaTaggerApplyTags")
    assert_no_recursive_keywords(server.tags())


def test_upgrade(args):
    if find_reusable_server() is not None:
        raise RuntimeError("Migration checks require a separate environment; the reusable Jellyfin instance is retained. No second container was created.")
    directory = create_generated(argparse.Namespace(port=args.port, server_version="10.11.10"))
    Server(directory).evidence("upgrade-artifacts", {
        "oldDllSha256": hashlib.sha256(pathlib.Path(args.old_dll).read_bytes()).hexdigest(),
        "newDllSha256": hashlib.sha256(pathlib.Path(args.new_dll).read_bytes()).hexdigest()})
    install(argparse.Namespace(directory=directory, dll=args.old_dll))
    server = Server(directory)
    server.login()
    server.configure(GeneratedTagPrefix="upgrade", StaleTagMode="Remove", PreviewOnly=True,
                     EnableExistingTagsAsKeywords=False, IncludeParentSeriesMetadataOnEpisodes=True)
    summary = server.task("MetaTaggerApplyTags")
    assert summary["WritesApplied"] == 7
    before = server.tags()
    ledger = directory / "config/plugins/Jellyfin.Plugin.MetaTagger/meta-tagger-state.json"
    original_ledger = ledger.read_bytes()
    server.evidence("upgrade-before", {"version": server.version, "summary": summary, "storedTags": before,
                                       "ledger": json.loads(original_ledger)})
    docker("stop", server.meta["container"])
    shutil.copytree(directory / "config", directory / "pre-upgrade-config-backup")
    shutil.move(str(directory / "config/plugins/Meta Tagger_0.1.0.0"), str(directory / "old-plugin-10.11.10"))
    docker("rm", server.meta["container"])
    meta = server.meta
    meta.update(serverVersion="12.0.0", image="jellyfin/jellyfin:12.0")
    (directory / "disposable.json").write_text(json.dumps(meta, indent=2) + "\n")
    docker("run", "-d", "--name", meta["container"], "--label", f"{LABEL}={meta['id']}", *docker_host_args(),
           "-p", f"127.0.0.1:{args.port}:8096", "-v", f"{directory / 'config'}:/config",
           "-v", f"{directory / 'cache'}:/cache", "-v", f"{directory / 'media'}:/media:ro", meta["image"])
    server = Server(directory)
    server.evidence("upgrade-server", server.wait_ready())
    server.login()
    scan, previous = server.start_task("RefreshLibrary")
    for _ in range(240):
        current = server.api(f"/ScheduledTasks/{scan['Id']}")
        result = current.get("LastExecutionResult")
        if current["State"] == "Idle" and result and result != previous:
            assert result["Status"] == "Completed", result
            break
        time.sleep(.25)
    else:
        raise RuntimeError("Required post-upgrade library scan did not complete in 60 seconds")
    assert server.tags() == before, "Server migration/scan changed fixture tags before plugin installation"
    assert ledger.read_bytes() == original_ledger, "Server migration changed plugin ownership ledger"
    server.evidence("upgrade-after-scan", {"scan": result, "storedTags": server.tags()})
    install(argparse.Namespace(directory=directory, dll=args.new_dll))
    server = Server(directory)
    server.login()
    summary = server.task("MetaTaggerApplyTags")
    assert server.tags() == before and summary["WritesApplied"] == 0
    assert summary["ItemsSkippedUnchanged"] == 7
    movie = next(k for k, x in server.items().items() if x["Type"] == "Movie")
    preview = server.api("/MetaTagger/Cleanup/Preview", {"ItemId": movie})
    assert preview["Token"] and len(preview["Changes"]) == 1
    removals = set(preview["Changes"][0]["RemovedTags"])
    cleanup = server.api("/MetaTagger/Cleanup/Apply", {"Token": preview["Token"]})
    assert set(server.tags()[movie]) == set(before[movie]) - removals
    assert all(server.tags()[key] == before[key] for key in before if key != movie)
    server.evidence("upgrade-results", {"from": "10.11.10", "to": "12.0.0", "idempotentSummary": summary,
                                         "cleanupPreview": preview, "cleanupSummary": cleanup,
                                         "storedTags": server.tags()})
    print("PASS generated 10.11.10 to 12.0.0 migration preserves tags and ledger; new cleanup owns old tags", flush=True)
    server.task("MetaTaggerApplyTags")
    print("Upgrade evidence:", directory / "evidence", flush=True)


def capture_evidence(args):
    server = Server(args.directory)
    server.wait_ready()
    server.login()
    server.evidence("server-final", server.api("/System/Info/Public", auth=False))
    server.evidence("plugins-final", server.api("/Plugins"))
    server.evidence("configuration-final", server.api(f"/Plugins/{PLUGIN_ID}/Configuration"))
    server.evidence("items-final", server.items())
    logs = docker("logs", server.meta["container"])
    credentials = json.loads((server.directory / "credentials.json").read_text())
    logs = redact_secrets(logs, [server.token, credentials["password"]])
    (server.directory / "evidence/server-redacted.log").write_text(logs)
    print("Saved final server, plugin, configuration, item, and redacted log evidence", flush=True)


def redact_secrets(value, known_secrets=()):
    for secret in known_secrets:
        if secret:
            value = value.replace(secret, "[REDACTED]")
    value = re.sub(r"(?i)(access token\s+)[0-9a-f]{32}\b", r"\1[REDACTED]", value)
    value = re.sub(r'''(?ix)(["']?(?:api_key|accessToken|token|password|pw)["']?\s*[:=]\s*)
                      (["'])(.*?)\2''', r"\1\2[REDACTED]\2", value)
    return re.sub(r'(?i)((?:api_key|accessToken|token|password|pw)=)(?!["\[])[^\s,&]+',
                  r"\1[REDACTED]", value)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    creation = commands.add_parser("create")
    creation.add_argument("--port", type=int, default=8097)
    creation.add_argument("--server-version", choices=["10.11.10", "12.0.0"], default="12.0.0")
    setup_command = commands.add_parser("setup")
    setup_command.add_argument("directory")
    installation = commands.add_parser("install")
    installation.add_argument("directory")
    installation.add_argument("dll")
    basic = commands.add_parser("test-basic")
    basic.add_argument("directory")
    cleanup = commands.add_parser("test-cleanup")
    cleanup.add_argument("directory")
    lifecycle = commands.add_parser("test-lifecycle")
    lifecycle.add_argument("directory")
    release_lifecycle = commands.add_parser("test-release-lifecycle")
    release_lifecycle.add_argument("manifest_url")
    release_lifecycle.add_argument("package")
    release_lifecycle.add_argument("--directory")
    release_lifecycle.add_argument("--version", default="0.1.0.0")
    release_lifecycle.add_argument("--previous-manifest-url")
    release_lifecycle.add_argument("--previous-version", default="0.0.0.0")
    reset = commands.add_parser("reset-fixtures")
    reset.add_argument("directory")
    recovery = commands.add_parser("test-recovery")
    recovery.add_argument("directory")
    concurrent = commands.add_parser("test-concurrent")
    concurrent.add_argument("directory")
    upgrade = commands.add_parser("test-upgrade")
    upgrade.add_argument("old_dll")
    upgrade.add_argument("new_dll")
    upgrade.add_argument("--port", type=int, default=18099)
    capture = commands.add_parser("capture-evidence")
    capture.add_argument("directory")
    cleanup_filters = commands.add_parser("test-cleanup-filters")
    cleanup_filters.add_argument("directory")
    args = parser.parse_args()
    {"create": create, "setup": setup, "install": install, "test-basic": test_basic,
     "test-cleanup": test_cleanup, "test-lifecycle": test_lifecycle,
     "test-release-lifecycle": test_release_lifecycle,
     "reset-fixtures": reset_fixtures, "test-recovery": test_recovery,
     "test-concurrent": test_concurrent, "test-upgrade": test_upgrade,
     "capture-evidence": capture_evidence, "test-cleanup-filters": test_cleanup_filters}[args.command](args)


if __name__ == "__main__":
    main()
