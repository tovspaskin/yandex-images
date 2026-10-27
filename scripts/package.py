"""Package dotnet publish output with stdlib; sign macOS bundles on a Mac."""
from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path
import plistlib
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import zipfile


def zip_tree(source: Path, destination: Path) -> None:
    with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted([source, *source.rglob("*")]):
            name = path.relative_to(source.parent).as_posix()
            if path.is_symlink():
                entry = zipfile.ZipInfo(name)
                entry.create_system = 3
                entry.external_attr = (stat.S_IFLNK | 0o777) << 16
                archive.writestr(entry, os.readlink(path))
            else:
                entry = zipfile.ZipInfo.from_file(path, name)
                entry.create_system = 3
                mode = path.stat().st_mode
                if path.is_dir():
                    mode = stat.S_IFDIR | 0o755
                elif path.name == "YandexImages":
                    mode = stat.S_IFREG | 0o755
                entry.external_attr = mode << 16
                if path.is_dir():
                    archive.writestr(entry, b"")
                else:
                    entry.compress_type = zipfile.ZIP_DEFLATED
                    with path.open("rb") as stream, archive.open(entry, "w", force_zip64=True) as target:
                        shutil.copyfileobj(stream, target)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=("win-x64", "linux-x64", "osx-x64", "osx-arm64"))
    parser.add_argument("--publish-dir", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--version", default="1.0.0")
    options = parser.parse_args()
    source = options.publish_dir.resolve(strict=True)
    output = options.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=True)
    stage = output.parent / "stage" / options.rid
    # Only this generated RID stage is replaced, never publish output/user data.
    if stage.resolve() != output.parent / "stage" / options.rid or stage.is_symlink():
        raise ValueError("Unsafe packaging stage")
    if source == stage or stage in source.parents:
        raise ValueError("Publish input must be outside the generated packaging stage")
    if stage.exists():
        shutil.rmtree(stage)
    package = stage / "YandexImages"
    package.mkdir(parents=True)
    executable = "YandexImages.exe" if options.rid.startswith("win-") else "YandexImages"
    if not (source / executable).is_file():
        raise FileNotFoundError(source / executable)

    if options.rid.startswith("osx-"):
        bundle = package / "YandexImages.app"
        binaries = bundle / "Contents" / "MacOS"
        shutil.copytree(source, binaries, symlinks=True)
        (bundle / "Contents" / "Resources").mkdir()
        template = Path(__file__).resolve().parent.parent / "packaging" / "macos" / "Info.plist"
        with template.open("rb") as stream:
            info = plistlib.load(stream)
        version = re.match(r"\d+\.\d+\.\d+", options.version)
        if version is None:
            raise ValueError("Version must start with major.minor.patch")
        info["CFBundleShortVersionString"] = version.group()
        info["CFBundleVersion"] = version.group()
        with (bundle / "Contents" / "Info.plist").open("wb") as stream:
            plistlib.dump(info, stream, sort_keys=False)
        (binaries / executable).chmod(0o755)
        if sys.platform == "darwin":
            for library in sorted(binaries.rglob("*.dylib")):
                if not library.is_symlink():
                    subprocess.run(["codesign", "--force", "--sign", "-", "--timestamp=none", str(library)], check=True)
            subprocess.run(["codesign", "--force", "--sign", "-", "--timestamp=none", str(bundle)], check=True)
            subprocess.run(["codesign", "--verify", "--deep", "--strict", "--verbose=2", str(bundle)], check=True)
        else:
            print("macOS cross-build: bundle is unsigned; native CI adds an ad-hoc signature.")
        shutil.copy2(source / "README.md", package / "README.md")
    else:
        shutil.copytree(source, package, dirs_exist_ok=True, symlinks=True)
        if options.rid.startswith("linux-"):
            (package / executable).chmod(0o755)

    archive_name = f"YandexImages-{options.rid}"
    if options.rid.startswith("linux-"):
        archive = output / f"{archive_name}.tar.gz"
        def portable_mode(member: tarfile.TarInfo) -> tarfile.TarInfo:
            if member.isdir() or member.name == "YandexImages/YandexImages":
                member.mode = 0o755
            return member
        with tarfile.open(archive, "w:gz", format=tarfile.PAX_FORMAT, dereference=False) as stream:
            stream.add(package, arcname=package.name, filter=portable_mode)
    else:
        archive = output / f"{archive_name}.zip"
        zip_tree(package, archive)
    with archive.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    archive.with_name(archive.name + ".sha256").write_text(f"{digest}  {archive.name}\n", encoding="utf-8")
    print(archive)


if __name__ == "__main__":
    main()
