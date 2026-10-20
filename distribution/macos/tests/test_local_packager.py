#!/usr/bin/env python3
"""Offline tests of the production packager; fixtures are retained under an explicit owned root.

No fixture is executed, made executable, fixed up or signed. --validate-only invokes no native tool.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import plistlib
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest

SOURCE = Path(os.environ.get("RYUBING_CANARY_LOCAL_SOURCE_ROOT", str(Path(__file__).absolute().parents[3])))
SCRIPT = SOURCE / "distribution/macos/create_local_app_bundle.py"
spec = importlib.util.spec_from_file_location("local_packager", SCRIPT)
packager = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packager)
FIXTURE_ROOT = os.environ.get("RYUBING_CANARY_LOCAL_TEST_ROOT")


def string(value):
    encoded = value.encode("utf-8")
    if len(encoded) >= 128:
        raise ValueError("Fixture string too long")
    return bytes([len(encoded)]) + encoded


def command(kind, value):
    name = value.encode("utf-8") + b"\0"
    start = 12 if kind == 0x8000001C else 24
    length = (start + len(name) + 7) // 8 * 8
    return struct.pack("<III", kind, length, start) + b"\0" * (start - 12) + name + b"\0" * (length - start - len(name))


def macho(cpu=packager.ARM64, file_type=6, name="libfixture.dylib", dependency="/usr/lib/libSystem.B.dylib", bundled=False, marker=1024):
    commands = command(0xC, dependency)
    if file_type == 6:
        commands += command(0xD, "@rpath/" + name)
    header = struct.pack("<IiiIIIII", 0xFEEDFACF, cpu, 0, file_type, 1 if file_type == 2 else 2, len(commands), 0, 0)
    host_size = max(4096, ((marker + len(packager.BUNDLE_SIGNATURE) + 4095) // 4096) * 4096) if bundled else 4096
    data = bytearray(header + commands + b"\0" * (host_size - len(header) - len(commands)))
    if bundled:
        payload = b"This is managed fixture data, not executable code."
        manifest = struct.pack("<III", 6, 0, 1) + string("owned-fixture") + struct.pack("<QQQQQ", 0, 0, 0, 0, 0)
        manifest += struct.pack("<QQQB", len(data), len(payload), 0, 1) + string("Ryujinx.dll")
        struct.pack_into("<Q", data, marker - 8, len(data) + len(payload))
        data[marker:marker + len(packager.BUNDLE_SIGNATURE)] = packager.BUNDLE_SIGNATURE
        data += payload + manifest
    return bytes(data)


def universal():
    x64 = macho(cpu=packager.X64)
    arm64 = macho()
    header = struct.pack(">II", 0xCAFEBABE, 2)
    header += struct.pack(">iiIII", packager.X64, 0, 4096, len(x64), 12)
    header += struct.pack(">iiIII", packager.ARM64, 0, 8192, len(arm64), 12)
    return header + b"\0" * (4096 - len(header)) + x64 + arm64


class LocalPackagerTests(unittest.TestCase):
    def setUp(self):
        if not FIXTURE_ROOT or not Path(FIXTURE_ROOT).is_absolute():
            raise RuntimeError("Use --fixture-root or RYUBING_CANARY_LOCAL_TEST_ROOT with an existing owned absolute directory.")
        fixture_root = packager.checked_path(FIXTURE_ROOT, "directory")
        self.root = Path(tempfile.mkdtemp(prefix=self._testMethodName + "-", dir=fixture_root))
        self.source = self.root / "source"
        self.publish = self.root / "publish"
        self.output = self.root / "new-output"
        self.distribution = self.source / "distribution/macos"
        self.distribution.mkdir(parents=True)
        self.publish.mkdir()
        policy = self.source / "src/Ryujinx.Common/Configuration/RyubingCanaryLocalPolicy.cs"
        policy.parent.mkdir(parents=True)
        policy.write_text("\n".join(f'public const string {name} = "{value}";' for name, value in packager.IDENTITY.items()), encoding="utf-8")
        for name in ("Info.plist", "entitlements.xml", "updater.sh"):
            shutil.copyfile(SOURCE / "distribution/macos" / name, self.distribution / name)
        (self.distribution / "Ryujinx.icns").write_bytes(b"Owned icon fixture")
        (self.distribution / "Assets.car").write_bytes(b"Owned asset fixture")
        shutil.copyfile(SOURCE / "LICENSE.txt", self.source / "LICENSE.txt")
        (self.source / "distribution/legal").mkdir()
        shutil.copyfile(SOURCE / "distribution/legal/THIRDPARTY.md", self.source / "distribution/legal/THIRDPARTY.md")
        notice = self.source / "src/Ryujinx/Assets/Fonts/Mono/OFL.txt"
        notice.parent.mkdir(parents=True)
        shutil.copyfile(SOURCE / "src/Ryujinx/Assets/Fonts/Mono/OFL.txt", notice)
        (self.publish / "Ryujinx").write_bytes(macho(file_type=2, bundled=True))
        for name in packager.REQUIRED_LIBRARIES:
            (self.publish / name).write_bytes(macho(name=name))

    def arguments(self, *extra):
        return [str(self.publish), str(self.output), str(self.distribution / "entitlements.xml"),
                "--source-root", str(self.source), "--version", "1.3.351", "--revision", "475615f0431b4995b03c930c5089d20f6f3570e0", *extra]

    def cli(self, *extra, validate=True):
        python = os.environ.get("RYUBING_CANARY_LOCAL_PYTHON", sys.executable)
        result = subprocess.run([python, "-B", str(SCRIPT), *self.arguments(*extra),
                                 *(["--validate-only"] if validate else [])], capture_output=True, text=True)
        (self.root / f"cli-{len(list(self.root.glob('cli-*')))}.json").write_text(
            json.dumps({"exit_code": result.returncode, "stdout": result.stdout, "stderr": result.stderr}, indent=2), encoding="utf-8")
        return result

    def refuse(self, expected, *extra, validate=True):
        result = self.cli(*extra, validate=validate)
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn(expected, result.stderr)
        self.assertFalse(self.output.exists())
        return result

    def change_plist(self, key, value):
        path = self.distribution / "Info.plist"
        data = plistlib.loads(path.read_bytes())
        data[key] = value
        path.write_bytes(plistlib.dumps(data))

    def test_valid_preflight_is_read_only_and_never_signs(self):
        before = {str(path): packager.file_record(path) for path in self.root.rglob("*") if path.is_file()}
        result = self.cli()
        self.assertEqual(0, result.returncode, result.stderr)
        response = json.loads(result.stdout)
        self.assertEqual("validated_inputs_only", response["status"])
        self.assertFalse(response["signing_performed"])
        self.assertFalse(response["app_executed"])
        self.assertFalse(self.output.exists())
        for path, record in before.items():
            self.assertEqual(record, packager.file_record(Path(path)))
        self.assertEqual("Ryubing Canary Local", response["identity"]["DisplayName"])
        self.assertEqual("local.ryubing.RyubingCanaryLocal", response["identity"]["BundleIdentifier"])
        self.assertFalse((self.source / "LICENSE.md").exists())

    def test_existing_output_is_refused_without_removing_anything(self):
        self.output.mkdir()
        sentinel = self.output / "keep"
        sentinel.write_bytes(b"unknown output must survive")
        result = self.cli(validate=False)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("must not exist", result.stderr)
        self.assertEqual(b"unknown output must survive", sentinel.read_bytes())
        self.assertEqual([sentinel], list(self.output.iterdir()))

    def test_existing_empty_output_is_also_refused(self):
        self.output.mkdir()
        result = self.cli()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("must not exist", result.stderr)
        self.assertEqual([], list(self.output.iterdir()))

    def test_official_bundle_identifier_override_is_refused(self):
        self.refuse("Local bundle identity", "--bundle-identifier", "org.ryujinx.Ryujinx")

    def test_old_app_name_override_is_refused(self):
        self.refuse("Local bundle identity", "--app-name", "Ryujinx")

    def test_source_policy_identity_mismatch_is_refused(self):
        path = self.source / "src/Ryujinx.Common/Configuration/RyubingCanaryLocalPolicy.cs"
        path.write_text(path.read_text().replace("Ryubing-Canary-Local", "Ryujinx"), encoding="utf-8")
        self.refuse("Source identity contract mismatch")

    def test_plist_identity_mismatch_is_refused(self):
        self.change_plist("CFBundleIdentifier", "org.ryujinx.Ryujinx")
        self.refuse("Info.plist does not describe")

    def test_document_handler_registration_is_refused(self):
        self.change_plist("CFBundleDocumentTypes", [])
        self.refuse("must not register")

    def test_protocol_handler_registration_is_refused(self):
        self.change_plist("CFBundleURLTypes", [{"CFBundleURLSchemes": ["ryujinx"]}])
        self.refuse("must not register")

    def test_exported_uti_registration_is_refused(self):
        self.change_plist("UTExportedTypeDeclarations", [])
        self.refuse("must not register")

    def test_imported_uti_registration_is_refused(self):
        self.change_plist("UTImportedTypeDeclarations", [])
        self.refuse("must not register")

    def test_wrong_local_update_policy_is_refused(self):
        self.change_plist("RyubingCanaryLocalUpdatePolicy", "Automatic")
        self.refuse("Info.plist does not describe")

    def test_stack_size_setting_cannot_be_silently_lost(self):
        self.change_plist("LSEnvironment", {})
        self.refuse("DOTNET_DefaultStackSize")

    def test_missing_jit_support_library_is_refused(self):
        (self.publish / "libarmeilleure-jitsupport.dylib").rename(self.root / "retained-jitsupport.dylib")
        self.refuse("Required native libraries are missing: libarmeilleure-jitsupport.dylib")

    def test_missing_executable_is_refused(self):
        (self.publish / "Ryujinx").rename(self.root / "retained-executable")
        self.refuse("executable Ryujinx is missing")

    def test_wrong_architecture_executable_is_refused(self):
        (self.publish / "Ryujinx").write_bytes(macho(cpu=packager.X64, file_type=2, bundled=True))
        self.refuse("supported arm64 slice")

    def test_non_macho_executable_is_refused(self):
        (self.publish / "Ryujinx").write_bytes(b"#!/bin/sh\nexit 0\n")
        self.refuse("Expected a 64-bit Mach-O input")

    def test_wrong_architecture_required_library_is_refused(self):
        (self.publish / "libSkiaSharp.dylib").write_bytes(macho(cpu=packager.X64))
        self.refuse("supported arm64 slice")

    def test_known_x64_only_soundio_is_explicitly_recorded_and_retained(self):
        soundio = self.publish / "libsoundio.dylib"
        soundio.write_bytes(macho(cpu=packager.X64, name=soundio.name))
        digest = packager.file_digest(soundio)
        result = self.cli()
        self.assertEqual(0, result.returncode, result.stderr)
        skipped = json.loads(result.stdout)["skipped_native_inputs"]
        self.assertEqual(1, len(skipped))
        self.assertEqual(digest, skipped[0]["sha256"])
        self.assertEqual(packager.X64, skipped[0]["architectures"][0]["cpu"])
        self.assertEqual(digest, packager.file_digest(soundio))

    def test_arm64_soundio_is_not_silently_dropped(self):
        (self.publish / "libsoundio.dylib").write_bytes(macho(name="libsoundio.dylib"))
        result = self.cli()
        self.assertEqual(0, result.returncode, result.stderr)
        response = json.loads(result.stdout)
        self.assertIn("libsoundio.dylib", response["native_inputs"])
        self.assertEqual([], response["skipped_native_inputs"])

    def test_universal_library_requires_real_arm64_slice(self):
        (self.publish / "libSkiaSharp.dylib").write_bytes(universal())
        result = self.cli()
        self.assertEqual(0, result.returncode, result.stderr)
        slices = json.loads(result.stdout)["native_inputs"]["libSkiaSharp.dylib"]["slices"]
        self.assertEqual([packager.X64, packager.ARM64], [item["cpu"] for item in slices])

    def test_malformed_fat_slice_is_refused(self):
        raw = bytearray(universal())
        struct.pack_into(">I", raw, 20, 2**31)
        (self.publish / "libSkiaSharp.dylib").write_bytes(raw)
        self.refuse("Invalid FAT slice range")

    def test_symlink_input_is_refused_before_reading_target(self):
        target = self.root / "not-a-publish-input"
        target.write_bytes(b"never followed by the packager")
        (self.publish / "external.dylib").symlink_to(target)
        self.refuse("Symbolic links are not allowed")
        self.assertEqual(b"never followed by the packager", target.read_bytes())

    def test_broken_symlink_output_is_refused(self):
        self.output.symlink_to(self.root / "absent-target")
        result = self.cli()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Symbolic links are not allowed", result.stderr)
        self.assertTrue(self.output.is_symlink())
        self.assertFalse((self.root / "absent-target").exists())

    def test_output_below_publish_is_refused(self):
        self.output = self.publish / "unsafe-output"
        self.refuse("must be separate")

    def test_output_below_source_is_refused(self):
        self.output = self.source / "unsafe-output"
        self.refuse("must be separate")

    def test_unknown_publish_content_is_refused(self):
        (self.publish / "unreviewed-secret.pem").write_text("owned dummy fixture", encoding="utf-8")
        self.refuse("Unexpected publish input")

    def test_user_configuration_is_not_packaged(self):
        (self.publish / "Config.json").write_text("{}", encoding="utf-8")
        self.refuse("User data or credentials")

    def test_project_entitlements_must_match(self):
        altered = self.source / "altered-entitlements.xml"
        altered.write_bytes(plistlib.dumps({}))
        args = packager.parser().parse_args(self.arguments())
        args.entitlements_file_path = str(altered)
        with self.assertRaisesRegex(packager.PackagingError, "unchanged project entitlements"):
            packager.validate_inputs(args)
        self.assertFalse(self.output.exists())

    def test_missing_font_notice_is_refused(self):
        notice = self.source / "src/Ryujinx/Assets/Fonts/Mono/OFL.txt"
        notice.rename(self.root / "retained-OFL.txt")
        self.refuse("font OFL license notices are missing")

    def test_canary_license_is_required_without_a_nextendo_shield_license(self):
        self.assertFalse((self.source / "LICENSE.md").exists())
        (self.source / "LICENSE.txt").rename(self.root / "retained-license.txt")
        self.refuse("Required regular file is missing")

    def test_third_party_notices_are_required(self):
        (self.source / "distribution/legal/THIRDPARTY.md").rename(self.root / "retained-thirdparty.md")
        self.refuse("Required regular file is missing")

    def test_font_authors_and_ofl_notices_are_retained_in_the_plan(self):
        authors = self.source / "src/Ryujinx/Assets/Fonts/Mono/AUTHORS.txt"
        shutil.copyfile(SOURCE / "src/Ryujinx/Assets/Fonts/Mono/AUTHORS.txt", authors)
        plan = packager.validate_inputs(packager.parser().parse_args(self.arguments()))
        targets = {str(target) for _, target in plan["notices"]}
        self.assertIn("Licenses/Fonts/Mono/OFL.txt", targets)
        self.assertIn("Licenses/Fonts/Mono/AUTHORS.txt", targets)

    def test_unresolved_native_dependency_is_refused(self):
        (self.publish / "libSkiaSharp.dylib").write_bytes(macho(dependency="/usr/local/lib/unbundled.dylib"))
        self.refuse("Unresolved non-system Mach-O dependency")

    def test_relative_dependency_mapping_uses_loader_and_executable_contexts(self):
        info = {"dependencies": ["/opt/build/libSkiaSharp.dylib", "/usr/lib/libSystem.B.dylib"]}
        self.assertEqual([("/opt/build/libSkiaSharp.dylib", "@loader_path/libSkiaSharp.dylib")],
                         packager.dependency_changes(info, {"libSkiaSharp.dylib"}))
        self.assertEqual([("/opt/build/libSkiaSharp.dylib", "@executable_path/../Frameworks/libSkiaSharp.dylib")],
                         packager.dependency_changes(info, {"libSkiaSharp.dylib"}, executable=True))
        self.assertFalse(packager.system_dependency("/usr/lib/../../opt/unsafe.dylib"))

    def test_dotnet_payload_commitment_detects_changed_managed_bytes(self):
        path = self.publish / "Ryujinx"
        before = packager.dotnet_bundle(path)
        raw = bytearray(path.read_bytes())
        raw[4096] ^= 1
        path.write_bytes(raw)
        self.assertNotEqual(before, packager.dotnet_bundle(path))

    def test_dotnet10_marker_beyond_four_mib_is_found_and_payload_hash_is_verified(self):
        path = self.publish / "Ryujinx"
        # Match the actual published .NET 10 apphost's observed marker location.
        marker = 8339816
        self.assertGreater(marker, 4 * 1024 * 1024)
        path.write_bytes(macho(file_type=2, bundled=True, marker=marker))
        bundle = packager.dotnet_bundle(path)
        self.assertEqual([6, 0], bundle["version"])
        self.assertGreater(bundle["header_offset"], marker)
        self.assertEqual(1, len(bundle["entries"]))
        self.assertEqual("Ryujinx.dll", bundle["entries"][0]["name"])
        self.assertGreater(bundle["entries"][0]["offset"], marker)
        payload = b"This is managed fixture data, not executable code."
        self.assertEqual(hashlib.sha256(payload).hexdigest(), bundle["entries"][0]["sha256"])
        result = self.cli()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("validated_inputs_only", json.loads(result.stdout)["status"])
        self.assertFalse(self.output.exists())

    def test_duplicate_marker_beyond_four_mib_is_refused_across_the_full_file(self):
        path = self.publish / "Ryujinx"
        with path.open("r+b") as target:
            target.seek(8339816)
            target.write(packager.BUNDLE_SIGNATURE)
        self.refuse("Missing or ambiguous .NET single-file bundle marker")

    def test_dotnet_bundle_size_bound_refuses_a_real_oversized_sparse_input(self):
        path = self.publish / "Ryujinx"
        self.assertEqual(512 * 1024 * 1024, packager.MAX_DOTNET_BUNDLE_BYTES)
        # Real owned file metadata exercises the actual guard before mmap/search.
        # Keep the fixture sparse: do not allocate or copy a 512 MiB test payload.
        with path.open("r+b") as target:
            target.truncate(packager.MAX_DOTNET_BUNDLE_BYTES + 1)
        self.assertEqual(packager.MAX_DOTNET_BUNDLE_BYTES + 1, path.stat().st_size)
        self.assertLess(path.stat().st_blocks * 512, 4 * 1024 * 1024)
        self.refuse("512 MiB) inspection bound")

    def test_invalid_dotnet_pointer_is_refused(self):
        path = self.publish / "Ryujinx"
        raw = bytearray(path.read_bytes())
        struct.pack_into("<Q", raw, 1016, len(raw) + 1)
        path.write_bytes(raw)
        self.refuse("Invalid .NET bundle header offset")

    def test_version_and_revision_are_explicit_not_unexpanded_placeholders(self):
        self.refuse("Version must be", "--version", "%%RYUJINX_BUILD_VERSION%%")
        self.refuse("Source revision must be", "--revision", "HEAD")

    def test_other_numeric_version_and_full_revision_are_not_the_approved_pin(self):
        self.refuse("pinned to", "--version", "1.3.352")
        self.refuse("pinned to", "--revision", "0000000000000000000000000000000000000000")

    def test_direct_updater_script_refuses_without_replacing_owned_directories(self):
        installed = self.root / "owned-installation"
        incoming = self.root / "owned-incoming"
        installed.mkdir()
        incoming.mkdir()
        sentinel = installed / "keep"
        sentinel.write_bytes(b"owned installation fixture must survive")
        result = subprocess.run(["/bin/bash", str(SOURCE / "distribution/macos/updater.sh"), str(installed), str(incoming), "0"],
                                capture_output=True, text=True)
        self.assertEqual(78, result.returncode)
        self.assertIn("automatic installation replacement is disabled", result.stderr)
        self.assertEqual(b"owned installation fixture must survive", sentinel.read_bytes())
        self.assertTrue(incoming.is_dir())
        self.assertEqual([sentinel], list(installed.iterdir()))

    def test_copied_owned_file_keeps_bytes_and_nonsecurity_xattr(self):
        source = self.root / "owned-source.txt"
        destination = self.root / "owned-copy.txt"
        source.write_bytes(b"fixture bytes")
        packager.write_attribute(source, "user.ryubing-canary-local-fixture", b"retain-this-owned-attribute")
        packager.copy_preserving_attributes(source, destination)
        self.assertEqual(source.read_bytes(), destination.read_bytes())
        self.assertEqual(b"retain-this-owned-attribute", packager.read_attributes(destination)["user.ryubing-canary-local-fixture"])
        with self.assertRaises(FileExistsError):
            packager.copy_preserving_attributes(source, destination)


if __name__ == "__main__":
    arguments = argparse.ArgumentParser(description=__doc__)
    arguments.add_argument("--fixture-root", default=FIXTURE_ROOT, required=not FIXTURE_ROOT,
                           help="Existing owned task-root directory; retained, never system temp")
    parsed, remaining = arguments.parse_known_args()
    if not Path(parsed.fixture_root).is_absolute():
        arguments.error("--fixture-root must be an explicit absolute path")
    FIXTURE_ROOT = packager.checked_path(parsed.fixture_root, "directory")
    unittest.main(argv=[sys.argv[0], *remaining], verbosity=2)
