from __future__ import annotations

import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile


MODULE_PATH = Path(__file__).parents[1] / "package_release.py"
SPEC = importlib.util.spec_from_file_location("package_release", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
PACKAGE_RELEASE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PACKAGE_RELEASE)


class PackageReleaseTests(unittest.TestCase):
    def test_writes_installable_release_assets(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            dll_path = root / "Jellyfin.Plugin.MetaTagger.dll"
            dll_path.write_bytes(b"release-dll")
            manifest_path = root / "manifest.json"
            manifest_path.write_text(
                json.dumps(
                    [
                        {
                            "guid": "4851185f-7284-4cad-9eeb-2c73576bb214",
                            "name": "Meta Tagger",
                            "owner": "sntna",
                            "versions": [
                                {
                                    "version": "0.1.0.0",
                                    "targetAbi": "12.0.0.0",
                                    "sourceUrl": "",
                                    "checksum": "",
                                    "timestamp": "",
                                }
                            ],
                        }
                    ]
                )
            )
            output = root / "release"

            assets = PACKAGE_RELEASE.write_release_assets(
                dll_path=dll_path,
                manifest_path=manifest_path,
                output_dir=output,
                repository="https://github.com/sntna/jellyfin-plugin-meta-tagger",
                tag="v0.1.0",
                timestamp="2026-09-15T20:00:00Z",
            )

            package_path = output / "meta-tagger_0.1.0.0.zip"
            self.assertEqual(package_path, assets.package_path)
            with zipfile.ZipFile(package_path) as package:
                self.assertEqual(["Jellyfin.Plugin.MetaTagger.dll", "meta-tagger.png"], package.namelist())
                self.assertEqual(PACKAGE_RELEASE.PLUGIN_IMAGE_PATH.read_bytes(), package.read("meta-tagger.png"))
                self.assertEqual(b"release-dll", package.read("Jellyfin.Plugin.MetaTagger.dll"))

            manifest = json.loads((output / "manifest.json").read_text())
            version = manifest[0]["versions"][0]
            self.assertEqual(
                "https://github.com/sntna/jellyfin-plugin-meta-tagger/"
                "releases/download/v0.1.0/meta-tagger_0.1.0.0.zip",
                version["sourceUrl"],
            )
            self.assertEqual(hashlib.md5(package_path.read_bytes()).hexdigest(), version["checksum"])
            self.assertEqual("2026-09-15T20:00:00Z", version["timestamp"])

            sums = (output / "SHA256SUMS").read_text().splitlines()
            self.assertEqual(3, len(sums))
            self.assertTrue(sums[2].endswith("  meta-tagger.png"))
            self.assertEqual(PACKAGE_RELEASE.PLUGIN_IMAGE_PATH.read_bytes(), (output / "meta-tagger.png").read_bytes())
            self.assertEqual("https://github.com/sntna/jellyfin-plugin-meta-tagger/releases/download/v0.1.0/meta-tagger.png", manifest[0]["imageUrl"])
            self.assertTrue(sums[0].endswith("  manifest.json"))
            self.assertTrue(sums[1].endswith("  meta-tagger_0.1.0.0.zip"))

    def test_can_point_the_same_package_at_a_local_acceptance_repository(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            dll_path = root / "Jellyfin.Plugin.MetaTagger.dll"
            dll_path.write_bytes(b"release-dll")
            manifest_path = root / "manifest.json"
            manifest_path.write_text(
                json.dumps(
                    [{"guid": "4851185f-7284-4cad-9eeb-2c73576bb214", "name": "Meta Tagger",
                      "versions": [{"version": "0.1.0.0", "sourceUrl": "", "checksum": "", "timestamp": ""}]}]
                )
            )

            assets = PACKAGE_RELEASE.write_release_assets(
                dll_path=dll_path,
                manifest_path=manifest_path,
                output_dir=root / "release",
                repository="https://github.com/sntna/jellyfin-plugin-meta-tagger",
                tag="v0.1.0",
                timestamp="2026-09-19T12:00:00Z",
                source_url_base="http://host.docker.internal:8098",
            )

            manifest = json.loads(assets.manifest_path.read_text())
            self.assertEqual("http://host.docker.internal:8098/meta-tagger.png", manifest[0]["imageUrl"])
            self.assertEqual(
                "http://host.docker.internal:8098/meta-tagger_0.1.0.0.zip",
                manifest[0]["versions"][0]["sourceUrl"],
            )


if __name__ == "__main__":
    unittest.main()
