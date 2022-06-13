import tarfile
import sys
import os
import io
from pathlib import Path

from typing import List, Tuple

f = tarfile.open(name=sys.argv[1], mode='x:gz')

all_files: List[Tuple[str, str]] = []


def get_uuid(meta_path: str) -> str:
    for l in Path(meta_path).read_text(encoding='utf-8').splitlines():
        if l.startswith("guid: "):
            return l[len("guid: "):]
    raise Exception(f"uuid not found: {meta_path}")


for root, dirs, files in os.walk(sys.argv[2], followlinks=True):
    all_files.append((get_uuid(f"{root}.meta"), root))
    for file in [f for f in files if not f.endswith(".meta")]:
        all_files.append((get_uuid(f"{root}/{file}.meta"), f"{root}/{file}"))

for uuid, path in reversed(all_files):
    tar_info = tarfile.TarInfo(f"./{uuid}/")
    tar_info.type = tarfile.DIRTYPE
    tar_info.mode = 0o755
    f.addfile(tar_info)

for uuid, path in all_files:
    tar_info = tarfile.TarInfo(f"./{uuid}/")
    tar_info.type = tarfile.DIRTYPE
    tar_info.mode = 0o755
    f.addfile(tar_info)

    tar_info = tarfile.TarInfo(f"./{uuid}/pathname")
    tar_info.type = tarfile.REGTYPE
    tar_info.mode = 0o644
    tar_info.size = len(path)
    f.addfile(tar_info, io.BytesIO(bytes(path, 'utf-8')))

    meta_path = f"{path}.meta"
    tar_info = tarfile.TarInfo(f"./{uuid}/asset.meta")
    tar_info.type = tarfile.REGTYPE
    tar_info.mode = 0o644
    tar_info.size = os.path.getsize(meta_path)
    with open(meta_path, mode='rb') as meta:
        f.addfile(tar_info, meta)

    if os.path.isfile(path):
        tar_info = tarfile.TarInfo(f"./{uuid}/asset")
        tar_info.type = tarfile.REGTYPE
        tar_info.size = os.path.getsize(path)
        tar_info.mode = 0o644
        with open(path, mode='rb') as asset:
            f.addfile(tar_info, asset)
