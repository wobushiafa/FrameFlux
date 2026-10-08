"""Validate release package identities, dependencies, sources and native layouts."""
import hashlib
import re
import struct
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

directory = Path(sys.argv[1])
managed = {
    "FrameFlux.Abstractions", "FrameFlux.Presentation", "FrameFlux.FFmpeg",
    "FrameFlux.FFmpeg.Android", "FrameFlux.WebRtc", "FrameFlux.Rendering.Windows",
    "FrameFlux.Wpf", "FrameFlux.Avalonia", "FrameFlux.Avalonia.Windows",
    "FrameFlux.Avalonia.Linux", "FrameFlux.Avalonia.Android",
}
native_rids = {
    "FrameFlux.FFmpeg.NativeAssets.Windows": ["win-x64"],
    "FrameFlux.FFmpeg.NativeAssets.Linux": ["linux-x64", "linux-arm64"],
    "FrameFlux.FFmpeg.NativeAssets.Android": ["android-arm64", "android-x64"],
}
source_hashes = {
    "source/ffmpeg-9.0.1.tar.gz": "fb1931fd4eb29297ee1c1017a24f800c4d8fbea35b4f2aaeb28308a48a9149b4",
    "source/ffmpeg-kit-next-9.0.0.tar.gz": "4eb50b840334b22e72b3d02fa72c9b39cf014372bae163896e8bdab9cc0e4b7d",
    "source/cpu-features-0.11.0.tar.gz": "ab2463f2d38fcaff1ce806be8e4c91333449931f5e02009d543b2569a3fa471a",
}
components = {"avcodec": 63, "avfilter": 12, "avformat": 63, "avutil": 61, "swresample": 7, "swscale": 10}
identities = set()
versions = set()
for path in sorted(directory.glob("*.nupkg")):
    with zipfile.ZipFile(path) as package:
        names = package.namelist()
        root = ET.fromstring(package.read(next(name for name in names if name.endswith(".nuspec"))))
        # NuGet has emitted multiple namespace versions; local element names suffice.
        for node in root.iter():
            node.tag = node.tag.split("}")[-1]
        metadata = root.find("metadata")
        identity = metadata.findtext("id")
        version = metadata.findtext("version")
        assert identity not in identities, f"Duplicate package: {identity}"
        identities.add(identity)
        versions.add(version)
        assert metadata.findtext("readme") in names, f"Missing README: {identity}"
        assert metadata.find("repository").get("url") == "https://github.com/wobushiafa/FrameFlux"
        for dependency in metadata.iter("dependency"):
            if dependency.get("id").startswith("FrameFlux."):
                assert dependency.get("version") == f"[{version}]", f"Unpinned dependency: {identity}"
        binaries = [name for name in names if name.startswith("runtimes/")]
        license_node = metadata.find("license")
        if identity in managed:
            assert not binaries, f"Native binary leaked into managed package: {identity}"
            assert license_node.get("type") == "expression" and license_node.text == "MIT"
        else:
            assert identity in native_rids, f"Unexpected package: {identity}"
            assert license_node.get("type") == "file" and license_node.text in names
            assert "FFMPEG-GPL-3.0.txt" in names and "FFMPEG-SOURCE.txt" in names
            for source, expected_hash in source_hashes.items():
                if "ffmpeg-9.0.1" not in source and not identity.endswith("Android"):
                    continue
                assert hashlib.sha256(package.read(source)).hexdigest() == expected_hash
            expected = set()
            for rid in native_rids[identity]:
                for component, major in components.items():
                    filename = (f"{component}-{major}.dll" if rid.startswith("win") else
                                f"lib{component}.so" if rid.startswith("android") else
                                f"lib{component}.so.{major}")
                    name = f"runtimes/{rid}/native/{filename}"
                    expected.add(name)
                    data = package.read(name)
                    assert len(data) > 1024, f"Missing binary/LFS pointer: {name}"
                    if rid.startswith("win"):
                        pe_offset = struct.unpack_from("<I", data, 60)[0]
                        assert data[pe_offset:pe_offset + 4] == b"PE\0\0"
                        assert struct.unpack_from("<H", data, pe_offset + 4)[0] == 0x8664
                    else:
                        assert data[:6] == b"\x7fELF\x02\x01", f"Expected 64-bit little-endian ELF: {name}"
                        assert struct.unpack_from("<H", data, 18)[0] == (183 if "arm64" in rid else 62)
                        if rid.startswith("linux"):
                            versions_required = [(int(a), int(b)) for a, b in re.findall(rb"GLIBC_(\d+)\.(\d+)", data)]
                            assert not versions_required or max(versions_required) <= (2, 35), f"glibc too new: {name}"
                        else:
                            offset = struct.unpack_from("<Q", data, 32)[0]
                            size, count = struct.unpack_from("<HH", data, 54)
                            alignments = [struct.unpack_from("<Q", data, offset + index * size + 48)[0]
                                          for index in range(count)
                                          if struct.unpack_from("<I", data, offset + index * size)[0] == 1]
                            assert alignments and min(alignments) >= 16384, f"Android LOAD alignment: {name}"
            assert set(binaries) == expected, f"Wrong native file set: {identity}"
            assert any(name.startswith("source-build/") for name in names), f"Missing build records: {identity}"
        print(f"Verified {identity} {version}")
assert identities == managed | native_rids.keys(), f"Missing packages: {(managed | native_rids.keys()) - identities}"
assert len(versions) == 1, f"Mixed release versions: {versions}"
assert len(list(directory.glob("*.snupkg"))) == len(managed), "Expected 11 managed symbol packages"
print("Verified all 14 NuGet packages and 11 symbol packages.")
