#!/usr/bin/env bash
# Updates the Java modules of src/AspNetCore.IgniteServer/JavaLibs.props after a version change (e.g., IgniteVersion in
# Directory.Packages.props): the Ignite modules listed there are kept, and the other entries are derived again from
# their Maven POMs (which list every compile and runtime dependency, with its version), without what the Apache.Ignite
# NuGet package already contains (ignite-core, ignite-spring, ignite-indexing and their dependencies). An entry with an
# Override attribute (its reason) keeps its version instead of the one the POMs declare (e.g., a fixed vulnerability).
# Every jar is downloaded from Maven Central and its SHA-256 written back. Review the diff before committing: a hash
# change without a version change means the artifact changed upstream. Needs python3.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
python3 -I - "$root" <<'PY'
import hashlib, re, sys, urllib.request, xml.etree.ElementTree as ET

root = sys.argv[1]
props_path = f"{root}/src/AspNetCore.IgniteServer/JavaLibs.props"
packages = open(f"{root}/Directory.Packages.props").read()
ignite_version = re.search(r"<IgniteVersion>([^<]+)</IgniteVersion>", packages).group(1)
props = open(props_path).read()
repository = re.search(r"<JavaLibsMavenRepositoryUrl>([^<]+)</JavaLibsMavenRepositoryUrl>", props).group(1)
IGNITE = "org.apache.ignite"
PACKAGED_MODULES = ["ignite-core", "ignite-spring", "ignite-indexing"]
NS = {"m": "http://maven.apache.org/POM/4.0.0"}


def url(group, artifact, version, extension):
    return f"{repository}/{group.replace('.', '/')}/{artifact}/{version}/{artifact}-{version}.{extension}"


def dependencies(module):
    """The compile and runtime dependencies (group, artifact, version) of an Ignite module."""
    with urllib.request.urlopen(url(IGNITE, module, ignite_version, "pom")) as response:
        pom = ET.fromstring(response.read())
    for dependency in pom.findall("m:dependencies/m:dependency", NS):
        def text(name):
            return dependency.findtext(f"m:{name}", namespaces=NS)

        if (text("scope") or "compile") in ("compile", "runtime") and text("optional") != "true":
            yield text("groupId"), text("artifactId"), text("version")


def version_key(version):
    # numbers as numbers, and a number before text where they differ (so that any two versions compare)
    return [(0, int(part), "") if part.isdigit() else (1, 0, part) for part in re.split(r"[.-]", version)]


packaged = {(IGNITE, module) for module in PACKAGED_MODULES}
for module in PACKAGED_MODULES:
    packaged |= {(group, artifact) for group, artifact, _ in dependencies(module)}

entry = re.compile(
    r'^[ \t]*<JavaLib Include="([^":]+):([^"]+)" Version="([^"]+)" Sha256="([0-9a-f]{64})"(?: Override="([^"]*)")?/>\n',
    re.M)
previous = {(group, artifact): (version, sha256) for group, artifact, version, sha256, _ in entry.findall(props)}
overrides = {(group, artifact): (version, reason)
             for group, artifact, version, _, reason in entry.findall(props) if reason}
libs, pending = {}, [artifact for group, artifact in previous if group == IGNITE]
while pending:
    module = pending.pop()
    if (IGNITE, module) in libs:
        continue
    libs[(IGNITE, module)] = ignite_version
    for group, artifact, version in dependencies(module):
        if (group, artifact) in packaged:
            continue
        if group == IGNITE:
            pending.append(artifact)
        elif (group, artifact) in libs and libs[(group, artifact)] != version:
            newest = max(version, libs[(group, artifact)], key=version_key)
            print(f"WARNING   {group}:{artifact}: {libs[(group, artifact)]} and {version} required, {newest} used")
            libs[(group, artifact)] = newest
        else:
            libs[(group, artifact)] = version

lines = []
for (group, artifact), (version, reason) in overrides.items():
    if (group, artifact) in libs:
        print(f"OVERRIDE  {group}:{artifact}: {version} instead of {libs[(group, artifact)]} ({reason})")
        libs[(group, artifact)] = version
    else:
        print(f"WARNING   {group}:{artifact} is no longer a dependency: its override is dropped")

for (group, artifact), version in sorted(libs.items(), key=lambda item: (item[0][1], item[0][0])):
    with urllib.request.urlopen(url(group, artifact, version, "jar")) as response:
        sha256 = hashlib.sha256(response.read()).hexdigest()
    old_version, old_sha256 = previous.get((group, artifact), (None, None))
    old_version = old_version and old_version.replace("$(IgniteVersion)", ignite_version)
    status = ("ADDED    " if old_version is None else "VERSION  " if old_version != version
              else "UPDATED  " if old_sha256 != sha256 else "unchanged")
    print(f"{status} {group}:{artifact}:{version}")
    shown = "$(IgniteVersion)" if group == IGNITE else version
    override = f' Override="{overrides[(group, artifact)][1]}"' if (group, artifact) in overrides else ""
    lines.append(f'        <JavaLib Include="{group}:{artifact}" Version="{shown}" Sha256="{sha256}"{override}/>\n')
for group, artifact in sorted(set(previous) - set(libs)):
    print(f"REMOVED   {group}:{artifact}")

# the entries are one block: it is replaced as a whole
first = entry.search(props)
block = re.compile(r"(?:" + entry.pattern.lstrip("^") + r")+", re.M).search(props, first.start())
open(props_path, "w").write(props[:block.start()] + "".join(lines) + props[block.end():])
PY
