#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Verify the built MSIX package."""
import os
import re
import sys
import zipfile

MSIX = sys.argv[1] if len(sys.argv) > 1 else (
    r"D:\admin\文档\.workbuddy\2026-10-20-18-07-44\PureUninstall\src\PureUninstall.App"
    r"\AppPackages\PureUninstall.App_0.1.1.0_x64_Test\PureUninstall.App_0.1.1.0_x64.msix"
)

if not os.path.exists(MSIX):
    print("MSIX NOT FOUND:", MSIX)
    sys.exit(1)

print("size:", round(os.path.getsize(MSIX) / 1024 / 1024, 1), "MB")
z = zipfile.ZipFile(MSIX)
names = z.namelist()
print("entries:", len(names))
print("has AppxManifest.xml:", "AppxManifest.xml" in names)
print("has exe:", any(n.endswith("PureUninstall.exe") for n in names))
print("asset files:", sum(1 for n in names if n.startswith("Assets/")))
print("dll count:", sum(1 for n in names if n.endswith(".dll")))
print("resources.pri:", any(n.endswith("resources.pri") for n in names))

manifest = [n for n in names if n.endswith("AppxManifest.xml")]
if manifest:
    xml = z.read(manifest[0]).decode("utf-8", "ignore")
    print("\n--- manifest highlights ---")
    identity = re.search(r"<Identity[^>]*/>", xml)
    if identity:
        print(identity.group(0).strip())
    for cap in re.findall(r'<(?:\w+:)?Capability[^>]*Name="([^"]+)"', xml):
        print("capability:", cap)
    dep = re.search(r"<PackageDependency[^>]*/>", xml)
    if dep:
        print(dep.group(0).strip())
    fam = re.search(r"<TargetDeviceFamily[^>]*/>", xml)
    if fam:
        print(fam.group(0).strip())
    entry = re.search(r"<Application[^>]*>", xml)
    if entry:
        print(entry.group(0).strip())
