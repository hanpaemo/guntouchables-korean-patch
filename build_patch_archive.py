from __future__ import annotations

from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

BASE_DIR = Path(__file__).resolve().parent
PATCH_ROOT = BASE_DIR / "patch"
ZIP_PATH = BASE_DIR / "Hanpaemo_GUNTOUCHABLES_KoreanPatch_Prep.zip"


def main() -> None:
    with ZipFile(ZIP_PATH, "w", compression=ZIP_DEFLATED) as zf:
        for path in sorted(PATCH_ROOT.rglob("*")):
            if not path.is_file():
                continue
            if path.suffix.lower() == ".pdb":
                continue
            rel = path.relative_to(PATCH_ROOT)
            if rel.parts and rel.parts[0] == "BepInEx":
                continue
            zf.write(path, rel)
    print(f"Wrote: {ZIP_PATH}")


if __name__ == "__main__":
    main()
