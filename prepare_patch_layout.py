from __future__ import annotations

import csv
from pathlib import Path

BASE_DIR = Path(__file__).resolve().parent
SOURCE_CSV = BASE_DIR / "output" / "csv" / "LocalizedStrings_all_locales.csv"
TRANSLATION_DIR = BASE_DIR / "translation" / "ko"
TARGET_CSV = TRANSLATION_DIR / "LocalizedStrings_ko.csv"


def main() -> None:
    TRANSLATION_DIR.mkdir(parents=True, exist_ok=True)

    with SOURCE_CSV.open("r", encoding="utf-8-sig", newline="") as src:
        reader = csv.DictReader(src)
        rows = list(reader)

    with TARGET_CSV.open("w", encoding="utf-8-sig", newline="") as dst:
        fieldnames = ["key_id", "key", "english", "zh-CN", "translation"]
        writer = csv.DictWriter(dst, fieldnames=fieldnames)
        writer.writeheader()
        for row in rows:
            writer.writerow(
                {
                    "key_id": row["key_id"],
                    "key": row["key"],
                    "english": row.get("en", ""),
                    "zh-CN": row.get("zh-CN", ""),
                    "translation": "",
                }
            )

    print(f"Wrote translation template: {TARGET_CSV}")
    print(f"Rows: {len(rows)}")


if __name__ == "__main__":
    main()
