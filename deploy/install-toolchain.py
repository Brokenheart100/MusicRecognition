"""Build-time only downloads, pinned by official release SHA256 digests."""
import hashlib
import json
from pathlib import Path
import subprocess
import urllib.request

root = Path('/opt/recognition')
lock = json.loads((root/'deploy/toolchain.lock.json').read_text())
for name, tool in lock.items():
    package = Path('/tmp')/name
    with urllib.request.urlopen(tool['url'], timeout=120) as source, package.open('wb') as target:
        while chunk := source.read(1024*1024): target.write(chunk)
    if hashlib.sha256(package.read_bytes()).hexdigest() != tool['sha256']:
        raise RuntimeError(f'{name} package SHA256 mismatch')
    if name == 'audiveris':
        # Runtime /tmp is a private tmpfs, so installed libraries must live in
        # the immutable toolchain tree rather than the extraction scratch area.
        unpacked = root/'.tools/audiveris-distribution'
        subprocess.run(['dpkg-deb','-x',str(package),str(unpacked)],check=True)
        jars = list(unpacked.rglob('audiveris.jar'))
        if len(jars)!=1: raise RuntimeError('Unexpected Audiveris layout')
        app = root/'.tools/audiveris/Audiveris'
        app.mkdir(parents=True)
        (app/'app').symlink_to(jars[0].parent,target_is_directory=True)
        (app/'runtime').symlink_to('/opt/java/openjdk',target_is_directory=True)
    package.unlink()
