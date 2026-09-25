from __future__ import annotations

import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import sys
from unittest.mock import patch
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



class TestedPackageTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.tested = self.root / "tested"
        self.output = self.root / "release"
        self.dll = self.root / "Jellyfin.Plugin.MetaTagger.dll"
        self.dll.write_bytes(b"tested-dll")
        self.assets = PACKAGE_RELEASE.write_release_assets(
            dll_path=self.dll, manifest_path=PACKAGE_RELEASE.MANIFEST_PATH,
            output_dir=self.tested,
            repository="https://github.com/sntna/jellyfin-plugin-meta-tagger",
            tag="v0.1.0", timestamp="2026-09-23T00:00:00Z",
            source_url_base="http://host.docker.internal:8098",
        )

    def publish(self):
        arguments = ["package_release.py", "--repository",
                     "https://github.com/sntna/jellyfin-plugin-meta-tagger",
                     "--tag", "v0.1.0", "--tested-repository", str(self.tested),
                     "--output", str(self.output)]
        with patch.object(sys, "argv", arguments), \
                patch.object(PACKAGE_RELEASE, "_build_plugin", side_effect=AssertionError("Must not rebuild")):
            return PACKAGE_RELEASE.main()

    def test_cli_reuses_exact_zip_without_building_and_rewrites_catalog(self):
        original_zip = self.assets.package_path.read_bytes()
        original_manifest = self.assets.manifest_path.read_bytes()
        self.dll.unlink()
        self.assertEqual(0, self.publish())
        self.assertEqual(original_zip, (self.output / self.assets.package_path.name).read_bytes())
        self.assertEqual(original_manifest, self.assets.manifest_path.read_bytes())
        entry = json.loads((self.output / "manifest.json").read_text())[0]
        base = "https://github.com/sntna/jellyfin-plugin-meta-tagger/releases/download/v0.1.0/"
        self.assertEqual(base + self.assets.package_path.name, entry["versions"][0]["sourceUrl"])
        self.assertEqual(base + "meta-tagger.png", entry["imageUrl"])
        self.assertEqual(hashlib.md5(original_zip).hexdigest(), entry["versions"][0]["checksum"])
        for line in (self.output / "SHA256SUMS").read_text().splitlines():
            digest, name = line.split("  ")
            self.assertEqual(digest, hashlib.sha256((self.output / name).read_bytes()).hexdigest())

    def test_rejects_mismatched_tested_catalog(self):
        original = self.assets.manifest_path.read_text()
        for key, value in [("guid", "another-plugin"), ("version", "0.0.0.0"), ("targetAbi", "10.0.0.0")]:
            with self.subTest(field=key):
                manifest = json.loads(original)
                target = manifest[0] if key == "guid" else manifest[0]["versions"][0]
                target[key] = value
                self.assets.manifest_path.write_text(json.dumps(manifest))
                with self.assertRaisesRegex(ValueError, "identity, version, or target ABI"):
                    self.publish()
                self.assertFalse(self.output.exists())

    def test_rejects_package_changed_after_catalog_generation(self):
        self.assets.package_path.write_bytes(b"changed-package")
        with self.assertRaisesRegex(ValueError, "checksum"):
            self.publish()
        self.assertFalse(self.output.exists())

    def test_rejects_unexpected_package_contents_even_with_matching_checksum(self):
        with zipfile.ZipFile(self.assets.package_path, "a") as package:
            package.writestr("unexpected-file.txt", "extra")
        manifest = json.loads(self.assets.manifest_path.read_text())
        manifest[0]["versions"][0]["checksum"] = hashlib.md5(self.assets.package_path.read_bytes()).hexdigest()
        self.assets.manifest_path.write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, "contents"):
            self.publish()
        self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()
