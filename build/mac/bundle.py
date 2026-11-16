#!/usr/bin/env python3
"""Wraps a `dotnet publish -r osx-*` folder into the .app bundle and, after signing, zips it.

  bundle.py app  <publish-dir> <out.app> <version> <icns>
  bundle.py zip  <out.zip> <readme.txt> <app> [<app> ...]

The zip is written with Python's zipfile (the `zip` CLI is not always installed) and keeps Unix permissions and
symlinks: Finder's Archive Utility restores both, and an executable that lost its x bit would not start.
"""
import os, plistlib, shutil, stat, sys, zipfile

DISPLAY_NAME = "EasySellPut"
SHORT_NAME = "EasySellPut"          # CFBundleName: menu-bar app menu, ≤ 15 characters
BUNDLE_ID = "com.meilidaogu.terminal"
EXECUTABLE = "MeiliMac"


def make_app(publish, app, version, icns):
    if os.path.exists(app):
        shutil.rmtree(app)
    contents = os.path.join(app, "Contents")
    macos = os.path.join(contents, "MacOS")
    resources = os.path.join(contents, "Resources")
    shutil.copytree(publish, macos, symlinks=True, ignore=shutil.ignore_patterns("*.pdb"))
    os.makedirs(resources)
    shutil.copy(icns, os.path.join(resources, "AppIcon.icns"))
    for root, _, files in os.walk(macos):
        for f in files:
            p = os.path.join(root, f)
            executable = f in (EXECUTABLE, "easysellput") or f.endswith(".dylib") or f == "createdump"
            os.chmod(p, 0o755 if executable else 0o644)
    info = {
        "CFBundleDevelopmentRegion": "zh_CN",
        "CFBundleLocalizations": ["zh_CN", "en"],
        "CFBundleDisplayName": DISPLAY_NAME,
        "CFBundleName": SHORT_NAME,
        "CFBundleIdentifier": BUNDLE_ID,
        "CFBundleExecutable": EXECUTABLE,
        "CFBundleIconFile": "AppIcon",
        "CFBundlePackageType": "APPL",
        "CFBundleInfoDictionaryVersion": "6.0",
        "CFBundleShortVersionString": version,
        "CFBundleVersion": version,
        "LSMinimumSystemVersion": "15.0",
        "LSApplicationCategoryType": "public.app-category.finance",
        "NSHighResolutionCapable": True,
        "NSAppTransportSecurity": {"NSAllowsLocalNetworking": True},
        "NSSupportsAutomaticGraphicsSwitching": True,
        "NSPrincipalClass": "NSApplication",
        "NSHumanReadableCopyright": DISPLAY_NAME,
    }
    with open(os.path.join(contents, "Info.plist"), "wb") as f:
        plistlib.dump(info, f, fmt=plistlib.FMT_XML)
    with open(os.path.join(contents, "PkgInfo"), "w") as f:
        f.write("APPL????")


def add(z, path, arc):
    st = os.lstat(path)
    info = zipfile.ZipInfo(arc)
    info.create_system = 3                       # Unix, so external_attr carries the mode
    info.date_time = (2026, 1, 1, 0, 0, 0)
    if stat.S_ISLNK(st.st_mode):
        info.external_attr = (stat.S_IFLNK | 0o755) << 16
        z.writestr(info, os.readlink(path))
    elif stat.S_ISDIR(st.st_mode):
        info.filename = arc.rstrip("/") + "/"
        info.external_attr = (stat.S_IFDIR | 0o755) << 16 | 0x10
        z.writestr(info, b"")
    else:
        info.external_attr = (stat.S_IFREG | stat.S_IMODE(st.st_mode)) << 16
        info.compress_type = zipfile.ZIP_DEFLATED
        with open(path, "rb") as f:
            z.writestr(info, f.read(), compresslevel=9)


def make_zip(out, readme, apps):
    if os.path.exists(out):
        os.remove(out)
    with zipfile.ZipFile(out, "w") as z:
        add(z, readme, os.path.basename(readme))
        for app in apps:
            base = os.path.dirname(os.path.abspath(app))
            for root, dirs, files in os.walk(app):
                dirs.sort()
                add(z, root, os.path.relpath(root, base))
                for name in sorted(files) + sorted(d for d in dirs if os.path.islink(os.path.join(root, d))):
                    p = os.path.join(root, name)
                    add(z, p, os.path.relpath(p, base))


if __name__ == "__main__":
    cmd, *args = sys.argv[1:]
    if cmd == "app":
        make_app(*args)
    elif cmd == "zip":
        make_zip(args[0], args[1], args[2:])
    else:
        sys.exit(__doc__)
