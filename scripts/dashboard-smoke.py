#!/usr/bin/env python3
"""Verify the dashboard only on the repository's labelled disposable Jellyfin server."""

import argparse
import importlib.util
import json
import pathlib
import secrets
import urllib.parse
import urllib.request

MODULE = pathlib.Path(__file__).with_name("disposable-jellyfin.py")
spec = importlib.util.spec_from_file_location("disposable_jellyfin", MODULE)
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory")
    args = parser.parse_args()
    server = fixture.Server(args.directory)
    assert server.version == "12.0.0"
    assert server.meta["container"] == fixture.REUSABLE_CONTAINER
    server.wait_ready()
    server.login()
    config_path = f"/Plugins/{fixture.PLUGIN_ID}/Configuration"
    original_config = server.api(config_path)
    original_items = server.items()
    seed = json.loads((server.directory / "evidence/seed-items.json").read_text())["Items"]
    seed_ids = {item["Id"] for item in seed}
    movie = next(item for item in original_items.values() if item["Type"] == "Movie" and item["Id"] in seed_ids)
    movie_id = movie["Id"]
    assert movie_id in {item["Id"] for item in seed} and movie["Path"].startswith("/media/")
    original_movie = server.api(f"/Users/{server.user_id}/Items/{movie_id}")
    original_tags = server.tags()
    state_path = server.directory / "config/plugins/Jellyfin.Plugin.MetaTagger/meta-tagger-state.json"
    original_state = json.loads(state_path.read_text()) if state_path.exists() else {"items": {}}
    results = []
    viewer_id = None
    admin_token = server.token

    def passed(scenario, **detail):
        results.append({"scenario": scenario, "passed": True, **detail})
        server.evidence("dashboard-results", results)
        print("PASS", scenario, flush=True)

    try:
        libraries = server.api("/MetaTagger/Libraries")
        library = next(lib for lib in libraries if lib["Name"] == "Movies")
        query = urllib.parse.urlencode({"libraryId": library["ItemId"], "searchTerm": movie["Name"], "limit": 1})
        page = server.api("/MetaTagger/Items?" + query)
        assert page["TotalCount"] == 1 and page["Items"][0]["ItemId"] == movie_id
        assert "Path" not in page["Items"][0]
        first = server.api("/MetaTagger/Items?limit=1&startIndex=0")
        second = server.api("/MetaTagger/Items?limit=1&startIndex=1")
        assert first["Items"][0]["ItemId"] != second["Items"][0]["ItemId"]
        assert server.tags() == original_tags
        passed("bounded library search and distinct page identities", total=first["TotalCount"])

        series = next(item for item in seed if item["Type"] == "Series")
        browse = server.api("/MetaTagger/Items?hierarchical=true&limit=50")
        assert all(item["ItemType"] in {"Movie", "Series", "Video"} for item in browse["Items"])
        seasons = server.api("/MetaTagger/Items?hierarchical=true&parentId=" + series["Id"])
        assert seasons["Items"] and all(item["ItemType"] == "Season" for item in seasons["Items"])
        season_id = seasons["Items"][0]["ItemId"]
        episodes = server.api("/MetaTagger/Items?hierarchical=true&parentId=" + season_id)
        assert episodes["Items"] and all(item["ItemType"] == "Episode" for item in episodes["Items"])
        episode = episodes["Items"][0]
        inspection = server.api("/MetaTagger/Items/" + episode["ItemId"])
        normalize = lambda value: value.lower().replace("-", "")
        expected_artwork = [episode["ItemId"], season_id, series["Id"]]
        assert list(map(normalize, episode["ArtworkItemIds"])) == list(map(normalize, expected_artwork))
        assert inspection["ArtworkItemIds"] == episode["ArtworkItemIds"]
        invalid_parent = server.api("/MetaTagger/Items?hierarchical=true&parentId=" + movie_id)
        assert invalid_parent["Status"] == "ParentUnavailable" and not invalid_parent["Items"]
        assert server.tags() == original_tags
        passed("series and season browsing with inspector artwork fallback identities")

        # Optional public-domain libraries provide actual images. Keep this smoke
        # test usable with the original generated fixtures, which have no art.
        artwork_item = next((item for item in original_items.values() if item.get("ImageTags", {}).get("Primary")), None)
        if artwork_item:
            image_url = server.base + "/Items/" + artwork_item["Id"] + "/Images/Primary?maxWidth=120&quality=80"
            with urllib.request.urlopen(image_url, timeout=30) as response:
                assert response.status == 200 and response.headers.get_content_type().startswith("image/")
                assert len(response.read()) > 0
            passed("library artwork is served by Jellyfin's primary image endpoint")

        endpoints = [("/MetaTagger/Items", None, "GET"), ("/MetaTagger/Libraries", None, "GET"),
                     ("/MetaTagger/Runs", None, "GET"), (f"/MetaTagger/Items/{movie_id}", None, "GET"),
                     ("/MetaTagger/Example", {"ItemId": movie_id, "Configuration": original_config}, "POST"),
                     (f"/MetaTagger/Items/{movie_id}/Preview", {}, "POST"),
                     (f"/MetaTagger/Items/{movie_id}/Apply", {"Token": "invalid"}, "POST"),
                     ("/MetaTagger/Cleanup/Preview", {"ItemId": movie_id}, "POST"),
                     ("/MetaTagger/Cleanup/Apply", {"Token": "invalid"}, "POST")]
        task = next(task for task in server.api("/ScheduledTasks") if task["Key"] == "MetaTaggerPreviewTags")
        endpoints.extend([(f"/ScheduledTasks/Running/{task['Id']}", None, "POST"),
                          (f"/ScheduledTasks/Running/{task['Id']}", None, "DELETE")])
        for path, body, method in endpoints:
            server.denied(path, body, method, status=401, auth=False)
        name, password = "dashboard-viewer-" + secrets.token_hex(3), secrets.token_urlsafe(24)
        viewer = server.api("/Users/New", {"Name": name, "Password": password})
        viewer_id = viewer["Id"]
        assert not viewer["Policy"]["IsAdministrator"]
        server.token = server.api("/Users/AuthenticateByName", {"Username": name, "Pw": password}, auth=False)["AccessToken"]
        for path, body, method in endpoints:
            server.denied(path, body, method, status=403)
        server.token = admin_token
        passed("anonymous and non-admin callers denied", endpoints=len(endpoints), anonymous=401, non_admin=403)

        config = server.configure(IsEnabled=True, RunAfterLibraryScan=False, GeneratedTagPrefix="ui-check", TagSeparator=":",
                                  ManualTagPrefix="manual", IncludeMovies=True, EnableGenres=False, EnableParentalRating=False,
                                  EnableExistingTagsAsKeywords=False, EnableStudios=False, EnableProductionCountries=False,
                                  EnableProviderIds=False, EnableProductionYear=True, PreviewOnly=True, StaleTagMode="Keep",
                                  ClaimExistingGeneratedTagsForCleanup=False, MaxItemsPerRun=0, MaxWritesPerRun=0,
                                  MaxRunMinutes=0, WriteDelayMilliseconds=0)
        server.update_item(movie_id, ProductionYear=2023, LockData=False, LockedFields=[])
        before = server.tags()
        draft = dict(config, EnableGenres=True)
        example = server.api("/MetaTagger/Example", {"ItemId": movie_id, "Configuration": draft})
        assert not example.get("Token") and any(tag.startswith("ui-check:genre:") for tag in example["GeneratedTags"])
        assert not server.api(config_path)["EnableGenres"] and server.tags() == before
        preview = server.api(f"/MetaTagger/Items/{movie_id}/Preview", {})
        assert preview["Token"] and server.tags() == before
        server.api(f"/MetaTagger/Items/{movie_id}/Apply", {"Token": preview["Token"]})
        server.update_item(movie_id, Name="Dune <literal text>", Genres=["Science Fiction"], OfficialRating="PG-13", CustomRating=None,
                           ProductionYear=2024, Tags=["Favorites", "manual:tagger:force", "ui-check:year:2023"])
        config = server.configure(EnableGenres=True, EnableParentalRating=True, StaleTagMode="Remove")
        before = server.tags()
        state_before = json.loads(state_path.read_text())
        preview = server.api(f"/MetaTagger/Items/{movie_id}/Preview", {})
        assert preview["AddedTags"] == ["ui-check:genre:science-fiction", "ui-check:rating:pg-13", "ui-check:year:2024"]
        assert preview["RemovedTags"] == ["ui-check:year:2023"]
        assert preview["PreservedTags"] == ["Favorites"] and preview["ManualTags"] == ["manual:tagger:force"]
        assert server.tags() == before
        other_id = next(key for key in before if key != movie_id)
        server.denied(f"/MetaTagger/Items/{other_id}/Apply", {"Token": preview["Token"]}, status=409)
        preview = server.api(f"/MetaTagger/Items/{movie_id}/Preview", {})
        applied = server.api(f"/MetaTagger/Items/{movie_id}/Apply", {"Token": preview["Token"]})
        assert applied["WritesApplied"] == 1
        server.denied(f"/MetaTagger/Items/{movie_id}/Apply", {"Token": preview["Token"]}, status=409)
        after = server.tags()
        assert after[movie_id] == sorted(["Favorites", "manual:tagger:force"] + preview["AddedTags"])
        assert all(after[key] == tags for key, tags in before.items() if key != movie_id)
        state_after = json.loads(state_path.read_text())
        assert state_after.get("runCursors", {}) == state_before.get("runCursors", {})
        assert all(state_after["items"].get(key) == value for key, value in state_before["items"].items() if key != movie_id)
        passed("Dune item preview/apply preserves out-of-scope tags, ownership and cursor", additions=3, removals=1, preserved=2)

        missing_tag = "ui-check:genre:science-fiction"
        server.update_item(movie_id, Tags=[tag for tag in after[movie_id] if tag != missing_tag] + ["edited-genre"])
        before_restore = server.tags()
        restoration = server.api(f"/MetaTagger/Items/{movie_id}/Preview", {})
        assert restoration["MissingRecordedTags"] == [missing_tag]
        assert restoration["AddedTags"] == [missing_tag]
        assert "edited-genre" in restoration["PreservedTags"]
        assert server.tags() == before_restore
        server.api(f"/MetaTagger/Items/{movie_id}/Apply", {"Token": restoration["Token"]})
        assert not server.api(f"/MetaTagger/Items/{movie_id}")["MissingRecordedTags"]
        server.update_item(movie_id, Tags=after[movie_id])
        passed("missing recorded tag preview is read-only, Apply restores it, and inspection refreshes")

        server.update_item(movie_id, ProductionYear=2025)
        preview = server.api(f"/MetaTagger/Items/{movie_id}/Preview", {})
        server.update_item(movie_id, ProductionYear=2026)
        server.denied(f"/MetaTagger/Items/{movie_id}/Apply", {"Token": preview["Token"]}, status=409)
        assert server.tags() == after
        server.update_item(movie_id, LockData=True)
        locked = server.api(f"/MetaTagger/Items/{movie_id}/Preview", {})
        assert locked["Status"] == "Protected" and not locked.get("Token")
        server.update_item(movie_id, LockData=False)
        passed("stale metadata and full metadata locks reject item writes")

        for preview_only in (True, False):
            server.configure(PreviewOnly=preview_only)
            before_task = server.tags()
            summary = server.task("MetaTaggerPreviewTags")
            assert summary["PreviewOnly"] and summary["WritesApplied"] == 0
            assert summary["Invocation"] == "MetaTaggerPreviewTags"
            assert server.tags() == before_task
        runs = server.api("/MetaTagger/Runs")
        detail = server.api("/MetaTagger/Runs/" + runs[0]["RunId"])
        assert detail["Items"] and detail["DetailsAvailable"]
        assert all("Path" not in item and "ItemPath" not in item for item in detail["Items"])
        assert detail["Summary"].get("PreviewChangesExportPath") is None
        server.restart()
        admin_token = server.token
        assert server.api("/MetaTagger/Runs/" + detail["RunId"])["RunId"] == detail["RunId"]
        assert server.api(config_path)["PreviewOnly"] is False
        passed("native Preview task stays read-only and history/settings survive restart", run_id=detail["RunId"])

        cleanup = server.api("/MetaTagger/Cleanup/Preview", {"ItemId": movie_id})
        assert cleanup["Token"]
        # Choose an owned generated tag, independently of sort order or manual tags.
        removed_externally = next(tag for tag in server.tags()[movie_id] if tag.startswith("ui-check:"))
        server.update_item(movie_id, Tags=[tag for tag in server.tags()[movie_id] if tag != removed_externally])
        cleanup = server.api("/MetaTagger/Cleanup/Preview", {"ItemId": movie_id})
        assert cleanup["MissingRecordedTags"] == [removed_externally]
        assert all(removed_externally not in change["RemovedTags"] for change in cleanup["Changes"])
        server.api("/MetaTagger/Cleanup/Apply", {"Token": cleanup["Token"]})
        assert server.tags()[movie_id] == ["Favorites", "manual:tagger:force"]
        passed("separate item cleanup removes only owned generated tags")
    finally:
        server.token = admin_token
        if viewer_id:
            server.api(f"/Users/{viewer_id}", method="DELETE")
        server.configure(IsEnabled=False, RunAfterLibraryScan=False)
        server.api(f"/Items/{movie_id}", original_movie)
        # Restore only this generated fixture's ownership, retaining all unrelated records.
        fixture.docker("stop", server.meta["container"])
        try:
            for path in (state_path, pathlib.Path(str(state_path) + ".bak")):
                if path.exists():
                    state = json.loads(path.read_text())
                    if movie_id in original_state.get("items", {}):
                        state["items"][movie_id] = original_state["items"][movie_id]
                    else:
                        state.get("items", {}).pop(movie_id, None)
                    temporary = path.with_suffix(path.suffix + ".dashboard-restore.tmp")
                    temporary.write_text(json.dumps(state, indent=2) + "\n")
                    temporary.replace(path)
        finally:
            fixture.docker("start", server.meta["container"])
        server.wait_ready()
        server.login()
        server.api(config_path, original_config)
        assert server.tags()[movie_id] == original_tags[movie_id]
    passed("generated fixture tags, metadata and saved settings restored")


if __name__ == "__main__":
    main()
