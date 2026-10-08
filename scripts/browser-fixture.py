#!/usr/bin/env python3
"""Private stdin/stdout bridge for browser checks of a verified generated server.

Never save this output as evidence. It contains disposable login credentials.
"""

import argparse
import hashlib
import importlib.util
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("disposable", ROOT / "scripts/disposable-jellyfin.py")
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory")
    parser.add_argument("--restore", action="store_true")
    args = parser.parse_args()
    directory = pathlib.Path(args.directory).resolve()
    if not directory.is_relative_to((ROOT / ".jellyfin-test").resolve()):
        raise RuntimeError("Browser checks require a generated directory under this repository's .jellyfin-test")
    server = fixture.Server(directory)
    if server.version != "12.0.0" or server.meta["image"] != "jellyfin/jellyfin:12.0":
        raise RuntimeError("Browser checks require the generated Jellyfin 12.0 image")
    if server.meta["container"] != fixture.REUSABLE_CONTAINER:
        raise RuntimeError("Browser checks require the repository's reusable generated container")
    server.wait_ready()
    server.login()
    seeds = json.loads((directory / "evidence/seed-items.json").read_text())["Items"]
    seed_ids = {item["Id"] for item in seeds}
    if args.restore:
        original = json.load(sys.stdin)
        item = server.items().get(original["itemId"])
        if not item or item["Id"] not in seed_ids or not item.get("Path", "").startswith("/media/"):
            raise RuntimeError("Restoration requires an existing generated seed item")
        server.configure(**original["configuration"])
        server.update_item(original["itemId"], Name=original["name"])
        return
    from package_release import _project_versions
    version = _project_versions()[0] + ".0"
    plugin = next(p for p in server.api("/Plugins") if p["Name"] == "Meta Tagger")
    if plugin["Status"] != "Active" or plugin["Version"] != version:
        raise RuntimeError("Install and activate the current release ZIP before browser verification")
    package = ROOT / ".jellyfin-test/repository" / f"meta-tagger_{version}.zip"
    digest = hashlib.sha256(package.read_bytes()).hexdigest()
    lifecycle = json.loads((directory / "evidence/release-zip-lifecycle.json").read_text())
    if lifecycle["packageSha256"] != digest:
        raise RuntimeError("The release lifecycle evidence does not identify the current tested ZIP")
    item = next(item for item in server.items().values() if item["Id"] in seed_ids and item["Type"] == "Movie")
    if not item["Path"].startswith("/media/"):
        raise RuntimeError("Browser fixture movie is outside generated media")
    original = {"configuration": server.api(f"/Plugins/{fixture.PLUGIN_ID}/Configuration"),
                "itemId": item["Id"], "name": item["Name"]}
    try:
        server.update_item(item["Id"], Name="Generated browser fixture with a deliberately long movie title " + "longlabel" * 16)
        server.configure(IsEnabled=True, EnableGenres=True, IncludeMovies=True, PreviewOnly=True,
                         RunAfterLibraryScan=False)
        server.task("MetaTaggerApplyTags")
        server.task("MetaTaggerPreviewTags")
        print(json.dumps({"base": server.base, "credentials": json.loads((directory / "credentials.json").read_text()),
                          "original": original, "packageSha256": digest, "pluginVersion": version,
                          "serverVersion": server.version}))
    except BaseException:
        server.configure(**original["configuration"])
        server.update_item(original["itemId"], Name=original["name"])
        raise


if __name__ == "__main__":
    main()
