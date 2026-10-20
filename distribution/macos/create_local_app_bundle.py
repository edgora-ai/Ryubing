#!/usr/bin/env python3
"""Package a reviewed osx-arm64 publish into a new, independent local app.

--validate-only is read-only and never invokes a native tool or executes an input.
An actual run retains incomplete output on failure; it never cleans an existing path.
"""
import argparse
import ctypes
import hashlib
import json
import mmap
import os
from pathlib import Path
import plistlib
import posixpath
import re
import shutil
import stat
import struct
import subprocess
import sys

IDENTITY = {
    "DisplayName": "Ryubing Canary Local",
    "BundleIdentifier": "local.ryubing.RyubingCanaryLocal",
    "DataDirectoryName": "Ryubing-Canary-Local",
    "PortableDirectoryName": "portable-ryubing-canary-local",
}
VERSION = "1.3.351"
SOURCE_REVISION = "475615f0431b4995b03c930c5089d20f6f3570e0"
BUILD_MARKER = "ryubing-canary-local+475615f.local1"
ARM64 = 0x0100000C
X64 = 0x01000007
BUNDLE_SIGNATURE = hashlib.sha256(b".net core bundle\n").digest()
MAX_DOTNET_BUNDLE_BYTES = 512 * 1024 * 1024
LOAD_DYLIB = {0xC, 0x80000018, 0x8000001F, 0x80000023, 0x20}
REQUIRED_LIBRARIES = {
    "libSDL3.dylib", "libAvaloniaNative.dylib", "libSkiaSharp.dylib", "libHarfBuzzSharp.dylib",
    "libMoltenVK.dylib", "libarmeilleure-jitsupport.dylib",
}


class PackagingError(Exception):
    pass


def fail(message):
    raise PackagingError(message)


def checked_path(value, kind=None):
    path = Path(os.path.abspath(value))
    for component in [*reversed(path.parents), path]:
        try:
            attributes = component.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(attributes.st_mode):
            fail(f"Symbolic links are not allowed in packaging paths: {component}")
    if kind == "file" and not path.is_file():
        fail(f"Required regular file is missing: {path}")
    if kind == "directory" and not path.is_dir():
        fail(f"Required input directory is missing: {path}")
    return path


def file_digest(path, offset=0, length=None):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        source.seek(offset)
        while length is None or length > 0:
            data = source.read(1024 * 1024 if length is None else min(length, 1024 * 1024))
            if not data:
                if length:
                    fail(f"Unexpected end of file: {path}")
                break
            digest.update(data)
            if length is not None:
                length -= len(data)
    return digest.hexdigest()


def darwin_xattr_functions():
    # Some macOS Python distributions omit os.*xattr. Use the actual no-follow libc APIs.
    library = ctypes.CDLL(None, use_errno=True)
    library.listxattr.argtypes = [ctypes.c_char_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.c_int]
    library.listxattr.restype = ctypes.c_ssize_t
    library.getxattr.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.c_uint32, ctypes.c_int]
    library.getxattr.restype = ctypes.c_ssize_t
    library.setxattr.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.c_uint32, ctypes.c_int]
    library.setxattr.restype = ctypes.c_int
    return library


def read_attributes(path):
    if hasattr(os, "listxattr"):
        return {name: os.getxattr(path, name, follow_symlinks=False) for name in os.listxattr(path, follow_symlinks=False)}
    if sys.platform != "darwin":
        fail("This Python cannot read extended attributes; input preservation cannot be verified.")
    library = darwin_xattr_functions()
    encoded = os.fsencode(path)
    size = library.listxattr(encoded, None, 0, 1)  # XATTR_NOFOLLOW
    if not 0 <= size <= 1024 * 1024:
        raise OSError(ctypes.get_errno(), "Cannot read bounded input xattr names", str(path))
    if not size:
        return {}
    buffer = ctypes.create_string_buffer(size)
    if library.listxattr(encoded, buffer, size, 1) != size:
        raise OSError(ctypes.get_errno(), "Input xattr names changed or could not be read", str(path))
    result = {}
    for raw_name in buffer.raw.rstrip(b"\0").split(b"\0"):
        length = library.getxattr(encoded, raw_name, None, 0, 0, 1)
        if not 0 <= length <= 16 * 1024 * 1024:
            raise OSError(ctypes.get_errno(), "Cannot read bounded input xattr value", str(path))
        value = ctypes.create_string_buffer(length)
        if library.getxattr(encoded, raw_name, value, length, 0, 1) != length:
            raise OSError(ctypes.get_errno(), "Input xattr changed or could not be read", str(path))
        result[os.fsdecode(raw_name)] = value.raw
    return result


def write_attribute(path, name, value):
    if hasattr(os, "setxattr"):
        os.setxattr(path, name, value, follow_symlinks=False)
    elif sys.platform == "darwin":
        library = darwin_xattr_functions()
        data = ctypes.create_string_buffer(value)
        if library.setxattr(os.fsencode(path), os.fsencode(name), data, len(value), 0, 1) != 0:
            raise OSError(ctypes.get_errno(), "Cannot preserve input xattr", str(path))
    else:
        fail("This Python cannot preserve extended attributes.")


def file_record(path):
    attributes = path.stat(follow_symlinks=False)
    return {"path": str(path), "bytes": attributes.st_size, "mode": stat.S_IMODE(attributes.st_mode),
            "sha256": file_digest(path),
            "xattrs": {name: hashlib.sha256(value).hexdigest() for name, value in sorted(read_attributes(path).items())}}


def macho_slices(path):
    """Read bounded Mach-O metadata, including exact architecture and load commands."""
    size = path.stat().st_size
    with path.open("rb") as source:
        magic = source.read(4)
        if magic == b"\xcf\xfa\xed\xfe":
            ranges = [(0, size, None)]
        elif magic in (b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca", b"\xca\xfe\xba\xbf", b"\xbf\xba\xfe\xca"):
            endian = ">" if magic in (b"\xca\xfe\xba\xbe", b"\xca\xfe\xba\xbf") else "<"
            count = struct.unpack(endian + "I", source.read(4))[0]
            if not 1 <= count <= 16:
                fail(f"Invalid FAT architecture count: {path}")
            fat64 = magic in (b"\xca\xfe\xba\xbf", b"\xbf\xba\xfe\xca")
            row_format = endian + ("iiQQII" if fat64 else "iiIII")
            row_size = struct.calcsize(row_format)
            ranges = []
            for _ in range(count):
                row = source.read(row_size)
                if len(row) != row_size:
                    fail(f"Truncated FAT header: {path}")
                cpu, subtype, offset, length, *_ = struct.unpack(row_format, row)
                if offset < 8 + count * row_size or length < 32 or offset + length > size:
                    fail(f"Invalid FAT slice range: {path}")
                ranges.append((offset, length, (cpu, subtype)))
            ordered = sorted(ranges)
            if any(left[0] + left[1] > right[0] for left, right in zip(ordered, ordered[1:])):
                fail(f"Overlapping FAT slices: {path}")
        else:
            fail(f"Expected a 64-bit Mach-O input, not another platform or a script: {path}")

        result = []
        for offset, length, fat_identity in ranges:
            source.seek(offset)
            header = source.read(32)
            if len(header) != 32:
                fail(f"Truncated Mach-O header: {path}")
            magic, cpu, subtype, file_type, count, command_size, _, _ = struct.unpack("<IiiIIIII", header)
            if magic != 0xFEEDFACF or command_size > 16 * 1024 * 1024 or 32 + command_size > length or count > command_size // 8:
                fail(f"Invalid Mach-O load-command region: {path}")
            if fat_identity is not None and fat_identity != (cpu, subtype):
                fail(f"FAT and Mach-O architecture identities disagree: {path}")
            commands = source.read(command_size)
            position = 0
            info = {"offset": offset, "bytes": length, "cpu": cpu, "subtype": subtype,
                    "file_type": file_type, "dependencies": [], "rpaths": [], "id": None}
            for _ in range(count):
                if position + 8 > len(commands):
                    fail(f"Truncated Mach-O command: {path}")
                command, command_length = struct.unpack_from("<II", commands, position)
                if command_length < 8 or command_length % 8 or position + command_length > len(commands):
                    fail(f"Invalid Mach-O command length: {path}")
                if command in LOAD_DYLIB | {0xD, 0x8000001C}:
                    if command_length < (12 if command == 0x8000001C else 24):
                        fail(f"Truncated Mach-O string command: {path}")
                    name_offset = struct.unpack_from("<I", commands, position + 8)[0]
                    minimum = 12 if command == 0x8000001C else 24
                    if not minimum <= name_offset < command_length:
                        fail(f"Invalid Mach-O string offset: {path}")
                    raw = commands[position + name_offset:position + command_length]
                    if b"\0" not in raw:
                        fail(f"Unterminated Mach-O string: {path}")
                    name = raw.split(b"\0", 1)[0].decode("utf-8", errors="strict")
                    if command == 0xD:
                        if info["id"] is not None:
                            fail(f"Duplicate Mach-O install ID: {path}")
                        info["id"] = name
                    elif command == 0x8000001C:
                        info["rpaths"].append(name)
                    else:
                        info["dependencies"].append(name)
                position += command_length
            if position != len(commands):
                fail(f"Mach-O command count and extent disagree: {path}")
            result.append(info)
        return result


def arm64_slice(slices, path):
    candidates = [item for item in slices if item["cpu"] == ARM64 and (item["subtype"] & 0xFFFFFF) in (0, 1)]
    if len(candidates) != 1:
        fail(f"Expected exactly one supported arm64 slice: {path}")
    return candidates[0]


def dotnet_bundle(path):
    """Commit every embedded managed payload so native fixups cannot silently truncate it."""
    with path.open("rb") as source:
        file_size = os.fstat(source.fileno()).st_size
        if file_size > MAX_DOTNET_BUNDLE_BYTES:
            fail(f".NET bundle input exceeds the {MAX_DOTNET_BUNDLE_BYTES}-byte (512 MiB) inspection bound: {path}")
        if file_size < 8 + len(BUNDLE_SIGNATURE):
            fail(f"Missing or ambiguous .NET single-file bundle marker: {path}")
        # The .NET 10 marker can be well beyond the apphost prefix. Search the entire
        # bounded input read-only, including uniqueness beyond the first occurrence.
        with mmap.mmap(source.fileno(), 0, access=mmap.ACCESS_READ) as data:
            signature = data.find(BUNDLE_SIGNATURE)
            if signature < 8 or data.find(BUNDLE_SIGNATURE, signature + 1) >= 0:
                fail(f"Missing or ambiguous .NET single-file bundle marker: {path}")
            header_offset = struct.unpack_from("<Q", data, signature - 8)[0]
        if not 0 < header_offset < file_size:
            fail(f"Invalid .NET bundle header offset: {path}")
        source.seek(header_offset)

        def read(format_string):
            size = struct.calcsize(format_string)
            raw = source.read(size)
            if len(raw) != size:
                fail(f"Truncated .NET bundle metadata: {path}")
            return struct.unpack(format_string, raw)

        def text():
            length = 0
            for shift in range(0, 35, 7):
                byte = source.read(1)
                if not byte:
                    fail(f"Truncated .NET string: {path}")
                length |= (byte[0] & 0x7F) << shift
                if not byte[0] & 0x80:
                    break
            else:
                fail(f"Invalid .NET string length: {path}")
            if length > 32768:
                fail(f"Oversized .NET bundle path: {path}")
            raw = source.read(length)
            if len(raw) != length:
                fail(f"Truncated .NET string: {path}")
            return raw.decode("utf-8", errors="strict")

        major, minor, count = read("<III")
        if major != 6 or not 1 <= count <= 20000:
            fail(f"Unsupported .NET bundle manifest version/count: {path}: {major}.{minor}/{count}")
        bundle_id = text()
        locations = read("<QQQQQ")
        entries = []
        names = set()
        for _ in range(count):
            offset, size, compressed, file_type = read("<QQQB")
            name = text()
            length = compressed or size
            if name in names or not name or name.startswith(("/", "\\")) or ".." in Path(name).parts:
                fail(f"Invalid or duplicate .NET bundle entry: {path}: {name}")
            if offset + length > header_offset:
                fail(f"Invalid .NET bundle payload range: {path}: {name}")
            names.add(name)
            entries.append({"name": name, "offset": offset, "bytes": length, "uncompressed_bytes": size, "type": file_type})
        metadata_end = source.tell()
    for item in entries:
        item["sha256"] = file_digest(path, item["offset"], item["bytes"])
    return {"version": [major, minor], "bundle_id": bundle_id, "locations": list(locations),
            "header_offset": header_offset, "metadata_sha256": file_digest(path, header_offset, metadata_end - header_offset), "entries": entries}


def system_dependency(name):
    normalized = posixpath.normpath(name)
    return name == normalized and (name.startswith("/usr/lib/") or name.startswith("/System/Library/"))


def dependency_changes(info, libraries, executable=False):
    changes = []
    for name in info["dependencies"]:
        if system_dependency(name):
            continue
        basename = posixpath.basename(name)
        if basename not in libraries:
            fail(f"Unresolved non-system Mach-O dependency: {name}")
        replacement = ("@executable_path/../Frameworks/" if executable else "@loader_path/") + basename
        if replacement != name:
            changes.append((name, replacement))
    return changes


def validate_inputs(args):
    source_root = checked_path(args.source_root, "directory")
    publish = checked_path(args.publish_directory, "directory")
    output = checked_path(args.output_directory)
    if os.path.lexists(output):
        fail(f"Output directory must not exist; refusing to replace or clean it: {output}")
    if not output.parent.is_dir():
        fail(f"Output parent must already exist: {output.parent}")
    if output.is_relative_to(publish) or publish.is_relative_to(output) or output.is_relative_to(source_root):
        fail("Packaging output must be separate from the publish input and source tree.")
    if output.is_relative_to(Path("/Applications")) or output.is_relative_to(Path.home() / "Applications"):
        fail("Package into a fresh build directory, not an installation directory.")
    if args.app_name != IDENTITY["DisplayName"] or args.bundle_identifier != IDENTITY["BundleIdentifier"]:
        fail("Local bundle identity must remain Ryubing Canary Local / local.ryubing.RyubingCanaryLocal.")
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?", args.version):
        fail("Version must be a numeric three- or four-component local build version.")
    if not re.fullmatch(r"[a-f0-9]{40}", args.revision):
        fail("Source revision must be an explicit full lowercase Git commit ID.")

    if args.version != VERSION or args.revision != SOURCE_REVISION:
        fail(f"This local packager is pinned to {VERSION} / {SOURCE_REVISION}.")

    policy = checked_path(source_root / "src/Ryujinx.Common/Configuration/RyubingCanaryLocalPolicy.cs", "file")
    policy_text = policy.read_text(encoding="utf-8")
    for field, expected in IDENTITY.items():
        values = re.findall(r"public\s+const\s+string\s+" + field + r'\s*=\s*"([^"\n]*)"\s*;', policy_text)
        if values != [expected]:
            fail(f"Source identity contract mismatch for {field}: {policy}")

    distribution = source_root / "distribution/macos"
    inputs = {
        "Info.plist": checked_path(distribution / "Info.plist", "file"),
        "Ryujinx.icns": checked_path(distribution / "Ryujinx.icns", "file"),
        "Assets.car": checked_path(distribution / "Assets.car", "file"),
        "updater.sh": checked_path(distribution / "updater.sh", "file"),
        "LICENSE.txt": checked_path(source_root / "LICENSE.txt", "file"),
        "THIRDPARTY.md": checked_path(source_root / "distribution/legal/THIRDPARTY.md", "file"),
    }
    entitlements = checked_path(args.entitlements_file_path, "file")
    project_entitlements = checked_path(distribution / "entitlements.xml", "file")
    if entitlements.read_bytes() != project_entitlements.read_bytes():
        fail("Use the unchanged project entitlements.xml for this local developer build.")
    plist = plistlib.loads(inputs["Info.plist"].read_bytes())
    if (plist.get("CFBundleName") != args.app_name or plist.get("CFBundleDisplayName") != args.app_name or
            plist.get("CFBundleIdentifier") != args.bundle_identifier or plist.get("CFBundleExecutable") != "Ryujinx" or
            plist.get("RyubingCanaryLocalUpdatePolicy") != "ManualSourceOnly"):
        fail("Info.plist does not describe the approved independent local developer app.")
    for forbidden in ("CFBundleDocumentTypes", "CFBundleURLTypes", "UTExportedTypeDeclarations", "UTImportedTypeDeclarations"):
        if forbidden in plist:
            fail(f"Local bundle must not register official document types or protocol callbacks: {forbidden}")
    for key in ("CFBundleShortVersionString", "CFBundleVersion"):
        plist[key] = args.version
    plist["CFBundleLongVersionString"] = f"{args.version}-{BUILD_MARKER} (source {args.revision})"
    if plist.get("LSEnvironment", {}).get("DOTNET_DefaultStackSize") != "200000":
        fail("Preserve the project's DOTNET_DefaultStackSize=200000 bundle setting.")

    notices = []
    font_ofl_found = False
    fonts = checked_path(source_root / "src/Ryujinx/Assets/Fonts", "directory")
    for directory, dirs, files in os.walk(fonts, followlinks=False):
        for name in dirs + files:
            checked_path(Path(directory) / name)
        for name in files:
            if name.upper().startswith(("OFL", "LICENSE", "NOTICE", "AUTHORS")) and Path(name).suffix.lower() in (".txt", ".md"):
                notice = checked_path(Path(directory) / name, "file")
                notices.append((notice, Path("Licenses/Fonts") / notice.relative_to(fonts)))
                font_ofl_found |= name.upper().startswith("OFL")
    if not font_ofl_found:
        fail("Bundled font OFL license notices are missing.")

    files = []
    native = {}
    skipped = []
    for directory, dirs, names in os.walk(publish, followlinks=False):
        for name in dirs + names:
            checked_path(Path(directory) / name)
        for name in sorted(names):
            path = checked_path(Path(directory) / name, "file")
            relative = path.relative_to(publish)
            if name in ("Config.json", "RyubingCanaryLocalAppSecrets.cs") or any(part in ("portable", IDENTITY["PortableDirectoryName"], "portable-nextendo-local", "system", "bis", "sdcard", "games", ".switch") for part in relative.parts):
                fail(f"User data or credentials must not be copied into an app bundle: {relative}")
            if name.endswith(".dylib") or relative == Path("Ryujinx") or relative == Path("createdump"):
                if len(relative.parts) != 1:
                    fail(f"Native publish inputs must be unambiguous top-level files: {relative}")
                slices = macho_slices(path)
                if name == "libsoundio.dylib" and all(item["cpu"] == X64 for item in slices):
                    skipped.append({**file_record(path), "architectures": slices,
                                    "reason": "Known optional SoundIO backend has no arm64 slice; publish source retained unchanged."})
                    continue
                selected = arm64_slice(slices, path)
                if selected["file_type"] != (6 if name.endswith(".dylib") else 2):
                    fail(f"Unexpected Mach-O file type: {path}")
                if name == "Ryujinx" and len(slices) != 1:
                    fail("Publish the executable specifically for osx-arm64; a universal apphost is not accepted.")
                native[name] = {"path": path, "slices": slices, "selected": selected}
            elif name not in ("THIRDPARTY.md", "LICENSE.txt") and not (
                    name.endswith((".dll", ".deps.json", ".runtimeconfig.json")) or relative.parts[0] in ("Assets", "Resources")):
                fail(f"Unexpected publish input; review rather than silently omit or bundle it: {relative}")
            files.append((path, relative))
    if "Ryujinx" not in native:
        fail("The osx-arm64 publish executable Ryujinx is missing.")
    libraries = {name for name in native if name.endswith(".dylib")}
    missing = REQUIRED_LIBRARIES - libraries
    if missing:
        fail(f"Required native libraries are missing: {', '.join(sorted(missing))}")
    for name, item in native.items():
        item["changes"] = dependency_changes(item["selected"], libraries, executable=not name.endswith(".dylib"))
    bundle = dotnet_bundle(native["Ryujinx"]["path"])
    return {"source": source_root, "publish": publish, "output": output, "inputs": inputs,
            "entitlements": entitlements, "policy": policy, "plist": plist, "notices": notices,
            "files": files, "native": native, "skipped": skipped, "bundle": bundle}


def copy_preserving_attributes(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("xb") as target, source.open("rb") as original:
        shutil.copyfileobj(original, target, 1024 * 1024)
    shutil.copystat(source, destination, follow_symlinks=False)
    # copy2/copyfile do not preserve all macOS xattrs. Preserve, never clear, source attributes.
    attributes = read_attributes(source)
    for name, value in attributes.items():
        write_attribute(destination, name, value)
    copied_attributes = read_attributes(destination)
    if any(copied_attributes.get(name) != value for name, value in attributes.items()):
        fail(f"Extended attribute readback mismatch: {destination}")
    if file_digest(source) != file_digest(destination):
        fail(f"Copy digest mismatch: {destination}")


def run_tool(arguments, journal):
    result = subprocess.run(arguments, check=False, capture_output=True, text=True)
    journal.append({"arguments": [str(value) for value in arguments], "exit_code": result.returncode,
                    "stdout": result.stdout, "stderr": result.stderr})
    if result.returncode:
        fail(f"Native packaging tool failed ({result.returncode}): {arguments[0]}: {result.stderr.strip()}")


def package(plan, args):
    if sys.platform != "darwin":
        fail("Actual local arm64 packaging/signing requires macOS; --validate-only is portable and read-only.")
    for tool in ("/usr/bin/lipo", "/usr/bin/install_name_tool", "/usr/bin/codesign"):
        checked_path(tool, "file")
        if not os.access(tool, os.X_OK):
            fail(f"Required Apple packaging tool is unavailable: {tool}")
    output = plan["output"]
    output.mkdir(mode=0o700)  # Exclusive: no parents=True, cleanup, overwrite or installation replacement.
    journal = []
    receipt = output / "packaging-manifest.json"
    manifest = {"status": "incomplete", "identity": IDENTITY, "version": args.version, "revision": args.revision,
                "build_marker": BUILD_MARKER,
                "skipped_native_inputs": plan["skipped"], "tool_invocations": journal,
                "source_inputs": [file_record(path) for path, _ in plan["files"]] +
                                 [file_record(path) for path in plan["inputs"].values()] +
                                 [file_record(path) for path, _ in plan["notices"]] +
                                 [file_record(plan["policy"]), file_record(plan["entitlements"])] +
                                 [file_record(Path(item["path"])) for item in plan["skipped"]],
                "input_bundle": plan["bundle"]}
    try:
        app = output / (args.app_name + ".app")
        contents = app / "Contents"
        for folder in (contents / "MacOS", contents / "Frameworks", contents / "Resources"):
            folder.mkdir(parents=True)
        for source, relative in plan["files"]:
            name = relative.name
            if name in ("LICENSE.txt", "THIRDPARTY.md"):
                continue  # The reviewed source notices below are authoritative.
            target = contents / ("Frameworks" if name.endswith(".dylib") else "MacOS") / relative
            copy_preserving_attributes(source, target)
            item = plan["native"].get(name)
            if item is not None and len(item["slices"]) > 1:
                thinned = target.with_name(target.name + ".arm64")
                run_tool(["/usr/bin/lipo", str(target), "-thin", "arm64", "-output", str(thinned)], journal)
                # Replace only our just-copied owned file; never a publish or pre-existing destination.
                os.replace(thinned, target)
                for attribute, value in read_attributes(source).items():
                    write_attribute(target, attribute, value)
                target.chmod(source.stat().st_mode & 0o777)
        for name, source in plan["inputs"].items():
            if name != "Info.plist":
                copy_preserving_attributes(source, contents / "Resources" / name)
        for source, relative in plan["notices"]:
            copy_preserving_attributes(source, contents / "Resources" / relative)
        with (contents / "Info.plist").open("xb") as target:
            plistlib.dump(plan["plist"], target, sort_keys=False)
        with (contents / "PkgInfo").open("xb") as target:
            target.write(b"APPL????")

        for name, item in plan["native"].items():
            executable = not name.endswith(".dylib")
            target = contents / ("MacOS" if executable else "Frameworks") / name
            if executable:
                target.chmod(target.stat().st_mode | stat.S_IXUSR)
            info = arm64_slice(macho_slices(target), target)
            for old, new in item["changes"]:
                run_tool(["/usr/bin/install_name_tool", "-change", old, new, str(target)], journal)
            if not executable:
                run_tool(["/usr/bin/install_name_tool", "-id", "@rpath/" + name, str(target)], journal)
            desired_rpaths = ["@executable_path/../Frameworks"] if executable else []
            for rpath in info["rpaths"]:
                if rpath not in desired_rpaths:
                    run_tool(["/usr/bin/install_name_tool", "-delete_rpath", rpath, str(target)], journal)
            for rpath in desired_rpaths:
                if rpath not in info["rpaths"]:
                    run_tool(["/usr/bin/install_name_tool", "-add_rpath", rpath, str(target)], journal)
            slices = macho_slices(target)
            fixed = arm64_slice(slices, target)
            if len(slices) != 1 or dependency_changes(fixed, set(plan["native"]), executable) or fixed["rpaths"] != desired_rpaths:
                fail(f"Native fixup readback failed: {target}")
            if not executable and fixed["id"] != "@rpath/" + name:
                fail(f"Native install ID readback failed: {target}")
        executable = contents / "MacOS/Ryujinx"
        if dotnet_bundle(executable) != plan["bundle"]:
            fail("Native fixup changed the embedded .NET payload. Partial output is retained; do not install it.")
        for name in plan["native"]:
            if name == "Ryujinx":
                continue
            target = contents / ("Frameworks" if name.endswith(".dylib") else "MacOS") / name
            run_tool(["/usr/bin/codesign", "--force", "--sign", "-", "--timestamp=none", str(target)], journal)
            run_tool(["/usr/bin/codesign", "--verify", "--strict", str(target)], journal)
        run_tool(["/usr/bin/codesign", "--force", "--sign", "-", "--timestamp=none", "--entitlements", str(plan["entitlements"]), str(app)], journal)
        run_tool(["/usr/bin/codesign", "--verify", "--deep", "--strict", "--verbose=2", str(app)], journal)
        if dotnet_bundle(executable) != plan["bundle"]:
            fail("Signing changed the embedded .NET payload. Do not install this retained output.")
        # Re-read every original input; nothing in publish/source is allowed to drift during packaging.
        for original in manifest["source_inputs"]:
            if file_record(Path(original["path"])) != original:
                fail(f"Input changed during packaging: {original['path']}")
        manifest["output_files"] = [file_record(path) for path in sorted(app.rglob("*")) if path.is_file()]
        manifest["status"] = "packaged_arm64_adhoc_signature_verified"
        manifest["app_path"] = str(app)
    except Exception as exception:
        manifest["failure"] = str(exception)
        raise
    finally:
        with receipt.open("x", encoding="utf-8") as target:
            json.dump(manifest, target, ensure_ascii=False, indent=2)
            target.write("\n")
    return manifest


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    result.add_argument("publish_directory")
    result.add_argument("output_directory", help="Must be absent, with an already-existing owned parent")
    result.add_argument("entitlements_file_path")
    result.add_argument("--source-root", default=str(Path(__file__).absolute().parents[2]))
    result.add_argument("--version", required=True)
    result.add_argument("--revision", required=True)
    result.add_argument("--app-name", default=IDENTITY["DisplayName"])
    result.add_argument("--bundle-identifier", default=IDENTITY["BundleIdentifier"])
    result.add_argument("--validate-only", action="store_true", help="Read-only input checks; no tools, copy, signing, app launch or output creation")
    return result


def main(arguments=None):
    args = parser().parse_args(arguments)
    try:
        plan = validate_inputs(args)
        if args.validate_only:
            result = {"status": "validated_inputs_only", "identity": IDENTITY, "output_directory": str(plan["output"]),
                      "version": args.version, "revision": args.revision, "build_marker": BUILD_MARKER,
                      "native_inputs": {name: {key: value for key, value in item.items() if key != "path"} for name, item in plan["native"].items()},
                      "skipped_native_inputs": plan["skipped"], "font_notices": [str(path) for path, _ in plan["notices"]],
                      "signing_performed": False, "app_executed": False}
        else:
            result = package(plan, args)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    except (PackagingError, OSError, ValueError, struct.error, plistlib.InvalidFileException) as exception:
        print(f"Ryubing Canary Local packaging refused: {exception}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
