#!/usr/bin/env bash
# Updates the SHA-256 hashes of src/AspNetCore.IgniteServer/JavaLibs.props after a version change (e.g., IgniteVersion
# in Directory.Packages.props): downloads every listed jar from Maven Central and writes its hash back. Review the
# diff before committing: a hash change without a version change means the artifact changed upstream. Needs curl,
# sha256sum and python3.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
python3 - "$root" <<'PY'
import hashlib, re, sys, urllib.request

root = sys.argv[1]
props_path = f"{root}/src/AspNetCore.IgniteServer/JavaLibs.props"
packages = open(f"{root}/Directory.Packages.props").read()
ignite_version = re.search(r"<IgniteVersion>([^<]+)</IgniteVersion>", packages).group(1)
props = open(props_path).read()
repository = re.search(r"<JavaLibsMavenRepositoryUrl>([^<]+)</JavaLibsMavenRepositoryUrl>", props).group(1)

def update(match):
    coordinates, version, old_hash = match.group(1), match.group(2), match.group(3)
    group, artifact = coordinates.split(":")
    resolved = version.replace("$(IgniteVersion)", ignite_version)
    url = f"{repository}/{group.replace('.', '/')}/{artifact}/{resolved}/{artifact}-{resolved}.jar"
    with urllib.request.urlopen(url) as response:
        new_hash = hashlib.sha256(response.read()).hexdigest()
    print(f"{'unchanged' if new_hash == old_hash else 'UPDATED  '} {artifact}-{resolved}.jar")
    return match.group(0).replace(old_hash, new_hash)

props = re.sub(r'<JavaLib Include="([^"]+)" Version="([^"]+)" Sha256="([0-9a-f]{64})"/>', update, props)
open(props_path, "w").write(props)
PY
