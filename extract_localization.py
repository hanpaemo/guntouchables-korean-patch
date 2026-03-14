from __future__ import annotations

import csv
import json
from pathlib import Path
from typing import Any

import UnityPy

GAME_DIR = Path(r"D:\SteamLibrary\steamapps\common\GUNTOUCHABLES")
AA_DIR = GAME_DIR / "GUNTOUCHABLES_Data" / "StreamingAssets" / "aa" / "StandaloneWindows64"
OUTPUT_DIR = Path(__file__).resolve().parent / "output"
CSV_DIR = OUTPUT_DIR / "csv"
JSON_DIR = OUTPUT_DIR / "json"


def get_script_name(data: Any) -> str | None:
    try:
        script = data.m_Script.read()
    except Exception:
        return None
    return getattr(script, "name", None) or getattr(script, "m_Name", None)


def load_localization_objects() -> tuple[dict[str, dict[str, Any]], dict[int, str], dict[str, dict[str, Any]]]:
    env = UnityPy.load(str(AA_DIR))
    locales: dict[str, dict[str, Any]] = {}
    shared_keys: dict[int, str] = {}
    tables: dict[str, dict[str, Any]] = {}

    for obj in env.objects:
        if getattr(obj.type, "name", "") != "MonoBehaviour":
            continue

        try:
            data = obj.read()
        except Exception:
            continue

        script_name = get_script_name(data)
        if script_name == "Locale":
            code = data.m_Identifier.m_Code
            locales[code] = {
                "code": code,
                "name": data.m_Name,
                "locale_name": data.m_LocaleName,
                "sort_order": data.m_SortOrder,
                "bundle_name": obj.assets_file.parent.name,
                "bundle_internal_name": obj.assets_file.name,
                "path_id": obj.path_id,
            }
        elif script_name == "SharedTableData" and data.m_Name == "LocalizedStrings Shared Data":
            for entry in data.m_Entries:
                shared_keys[int(entry.m_Id)] = entry.m_Key
        elif script_name == "StringTable":
            code = data.m_LocaleId.m_Code
            tables[code] = {
                "code": code,
                "name": data.m_Name,
                "bundle_name": obj.assets_file.parent.name,
                "bundle_internal_name": obj.assets_file.name,
                "path_id": obj.path_id,
                "entry_count": len(data.m_TableData),
                "entries": {int(entry.m_Id): entry.m_Localized for entry in data.m_TableData},
            }

    return locales, shared_keys, tables


def write_outputs(locales: dict[str, dict[str, Any]], shared_keys: dict[int, str], tables: dict[str, dict[str, Any]]) -> None:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    CSV_DIR.mkdir(parents=True, exist_ok=True)
    JSON_DIR.mkdir(parents=True, exist_ok=True)

    locale_order = ["en", "zh-CN", "fr", "de", "jp", "ru", "es"]
    ordered_locale_codes = [code for code in locale_order if code in tables]
    ordered_locale_codes.extend(sorted(code for code in tables if code not in ordered_locale_codes))

    all_key_ids = set(shared_keys)
    for table in tables.values():
        all_key_ids.update(table["entries"].keys())

    orphan_key_ids = sorted(key_id for key_id in all_key_ids if key_id not in shared_keys)

    inventory = {
        "game_dir": str(GAME_DIR),
        "aa_dir": str(AA_DIR),
        "locales": list(locales.values()),
        "shared_key_count": len(shared_keys),
        "all_key_count": len(all_key_ids),
        "orphan_key_ids": orphan_key_ids,
        "tables": [
            {
                "code": code,
                "name": tables[code]["name"],
                "bundle_name": tables[code]["bundle_name"],
                "entry_count": tables[code]["entry_count"],
            }
            for code in ordered_locale_codes
        ],
    }

    (OUTPUT_DIR / "inventory.json").write_text(
        json.dumps(inventory, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

    for code in ordered_locale_codes:
        table = tables[code]
        payload = {
            "code": code,
            "name": table["name"],
            "bundle_name": table["bundle_name"],
            "entries": [
                {
                    "key_id": key_id,
                    "key": shared_keys.get(key_id, f"__missing_key_{key_id}"),
                    "text": text,
                }
                for key_id, text in sorted(table["entries"].items())
            ],
        }
        (JSON_DIR / f"{code}.json").write_text(
            json.dumps(payload, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )

    ordered_key_ids = sorted(all_key_ids)
    csv_path = CSV_DIR / "LocalizedStrings_all_locales.csv"
    with csv_path.open("w", encoding="utf-8-sig", newline="") as f:
        fieldnames = ["key_id", "key"] + ordered_locale_codes
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for key_id in ordered_key_ids:
            row = {"key_id": key_id, "key": shared_keys.get(key_id, f"__missing_key_{key_id}")}
            for code in ordered_locale_codes:
                row[code] = tables.get(code, {}).get("entries", {}).get(key_id, "")
            writer.writerow(row)

    print(f"Locales: {len(locales)}")
    print(f"Shared keys: {len(shared_keys)}")
    print(f"All keys written: {len(ordered_key_ids)}")
    for code in ordered_locale_codes:
        print(f"{code}: {tables[code]['entry_count']} entries")
    print(f"Wrote: {csv_path}")


def main() -> None:
    locales, shared_keys, tables = load_localization_objects()
    write_outputs(locales, shared_keys, tables)


if __name__ == "__main__":
    main()
