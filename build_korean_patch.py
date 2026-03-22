from __future__ import annotations

import base64
import csv
import hashlib
import json
import shutil
import struct
import subprocess
import tempfile
from pathlib import Path
from typing import Any

import UnityPy

BASE_DIR = Path(__file__).resolve().parent
GAME_DIR = Path(r"D:\SteamLibrary\steamapps\common\GUNTOUCHABLES")
DATA_DIR = GAME_DIR / "GUNTOUCHABLES_Data"
AA_DIR = DATA_DIR / "StreamingAssets" / "aa"
AA_BUNDLE_DIR = AA_DIR / "StandaloneWindows64"
MANAGED_DIR = DATA_DIR / "Managed"
ASSEMBLY_CSHARP = MANAGED_DIR / "Assembly-CSharp.dll"

TRANSLATION_CSV = BASE_DIR / "translation" / "ko" / "LocalizedStrings_ko.csv"
FONT_SOURCE_FILE = BASE_DIR / "assets" / "NanumGothic.ttf"
REQUIRED_CHARSET_FILE = BASE_DIR / "managed_runtime" / "required_chars_ko.txt"

PATCH_ROOT = BASE_DIR / "patch"
PATCH_DATA_DIR = PATCH_ROOT / "GUNTOUCHABLES_Data"
PATCH_AA_DIR = PATCH_DATA_DIR / "StreamingAssets" / "aa"
PATCH_BUNDLE_DIR = PATCH_AA_DIR / "StandaloneWindows64"
PATCH_MANAGED_DIR = PATCH_DATA_DIR / "Managed"
PATCH_ASSEMBLY_CSHARP = PATCH_MANAGED_DIR / "Assembly-CSharp.dll"

CATALOG_JSON = AA_DIR / "catalog.json"
PATCH_CATALOG_JSON = PATCH_AA_DIR / "catalog.json"

ZH_STRING_BUNDLE = "localization-string-tables-chinese(simplified)(zh-cn)_assets_all.bundle"
EN_STRING_BUNDLE = "localization-string-tables-english(en)_assets_all.bundle"
ZH_ASSET_BUNDLE = "localization-asset-tables-chinese(simplified)(zh-cn)_assets_all.bundle"

MANAGED_FONT_PATCH_PROJECT = BASE_DIR / "managed_runtime" / "GuntouchablesManagedFontPatch.csproj"
MANAGED_FONT_PATCH_DLL = "Hanpaemo.GUNTOUCHABLES.ManagedFontPatch.dll"
ASSEMBLY_PATCH_PROJECT = BASE_DIR / "assembly_patch" / "PatchAssemblyCSharp.csproj"


def get_script_name(data: Any) -> str | None:
    try:
        script = data.m_Script.read()
    except Exception:
        return None
    return getattr(script, "name", None) or getattr(script, "m_Name", None)


def read_translation_map() -> dict[int, str]:
    translations: dict[int, str] = {}
    with TRANSLATION_CSV.open("r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        for row in reader:
            key_id = int(row["key_id"])
            translations[key_id] = row.get("translation", "").replace("\r\n", "\n").strip()
    return translations


def write_required_charset(translations: dict[int, str]) -> None:
    seen: set[str] = set()
    chars: list[str] = []

    for text in translations.values():
        for ch in text:
            if ord(ch) < 32:
                continue
            if ch in seen:
                continue
            seen.add(ch)
            chars.append(ch)

    REQUIRED_CHARSET_FILE.write_text("".join(chars), encoding="utf-8")


def reset_patch_layout() -> None:
    if PATCH_ROOT.exists():
        shutil.rmtree(PATCH_ROOT)
    PATCH_BUNDLE_DIR.mkdir(parents=True, exist_ok=True)
    PATCH_MANAGED_DIR.mkdir(parents=True, exist_ok=True)


def build_english_fallback_map() -> dict[int, str]:
    english_map: dict[int, str] = {}
    english_env = UnityPy.load(str(AA_BUNDLE_DIR / EN_STRING_BUNDLE))

    for obj in english_env.objects:
        if getattr(obj.type, "name", "") != "MonoBehaviour":
            continue
        try:
            data = obj.read()
        except Exception:
            continue
        if get_script_name(data) != "StringTable":
            continue
        if getattr(data, "m_Name", "") != "LocalizedStrings_en":
            continue

        for entry in obj.read_typetree()["m_TableData"]:
            english_map[int(entry["m_Id"])] = entry["m_Localized"]
        break

    return english_map


def patch_string_table_bundle(translations: dict[int, str]) -> tuple[Path, int, int]:
    english_map = build_english_fallback_map()
    env = UnityPy.load(str(AA_BUNDLE_DIR / ZH_STRING_BUNDLE))
    translated_count = 0
    total_count = 0

    for obj in env.objects:
        if getattr(obj.type, "name", "") != "MonoBehaviour":
            continue
        try:
            data = obj.read()
        except Exception:
            continue
        if get_script_name(data) != "StringTable":
            continue

        tree = obj.read_typetree()
        existing_ids = {int(entry["m_Id"]) for entry in tree["m_TableData"]}

        for entry in tree["m_TableData"]:
            key_id = int(entry["m_Id"])
            ko = translations.get(key_id, "")
            if ko:
                entry["m_Localized"] = ko
                translated_count += 1
            else:
                en = english_map.get(key_id, "")
                if en:
                    entry["m_Localized"] = en
            total_count += 1

        # Inject entries that exist in EN but not in zh-CN
        added_count = 0
        for key_id, en_text in english_map.items():
            if key_id in existing_ids:
                continue
            ko = translations.get(key_id, "")
            new_entry = {
                "m_Id": key_id,
                "m_Localized": ko if ko else en_text,
                "m_Metadata": {"m_Items": []},
            }
            tree["m_TableData"].append(new_entry)
            if ko:
                translated_count += 1
            total_count += 1
            added_count += 1

        if added_count:
            print(f"Injected {added_count} new entries into zh-CN bundle")

        obj.save_typetree(tree)
        obj.assets_file.mark_changed()
        obj.assets_file.parent.mark_changed()
        break

    with tempfile.TemporaryDirectory() as temp_dir:
        env.save(out_path=temp_dir)
        built_bundle = Path(temp_dir) / ZH_STRING_BUNDLE
        if not built_bundle.exists():
            raise FileNotFoundError(f"UnityPy did not emit {ZH_STRING_BUNDLE}")
        target_bundle = PATCH_BUNDLE_DIR / ZH_STRING_BUNDLE
        shutil.copy2(built_bundle, target_bundle)

    return target_bundle, translated_count, total_count


def parse_extra_entries(blob: bytes) -> tuple[list[dict[str, Any]], dict[int, dict[str, Any]]]:
    entries: list[dict[str, Any]] = []
    by_offset: dict[int, dict[str, Any]] = {}
    offset = 0

    while offset < len(blob):
        magic = blob[offset]
        if magic != 7:
            raise ValueError(f"Unexpected extra-data magic {magic} at offset {offset}")

        assembly_len = blob[offset + 1]
        assembly_start = offset + 2
        assembly_end = assembly_start + assembly_len
        assembly_name = blob[assembly_start:assembly_end].decode("utf-8")

        class_len = blob[assembly_end]
        class_start = assembly_end + 1
        class_end = class_start + class_len
        class_name = blob[class_start:class_end].decode("utf-8")

        payload_len = struct.unpack_from("<I", blob, class_end)[0]
        payload_start = class_end + 4
        payload_end = payload_start + payload_len
        payload = blob[payload_start:payload_end].decode("utf-16-le")

        entry = {
            "old_offset": offset,
            "assembly_name": assembly_name,
            "class_name": class_name,
            "payload": payload,
        }
        entries.append(entry)
        by_offset[offset] = entry
        offset = payload_end

    return entries, by_offset


def parse_entry_data(blob: bytes) -> list[dict[str, int]]:
    count = struct.unpack_from("<I", blob, 0)[0]
    entries: list[dict[str, int]] = []
    offset = 4
    for _ in range(count):
        (
            internal_id_index,
            provider_index,
            dependency_key_index,
            bundled_asset_provider_crc,
            extra_data_offset,
            resource_type_index,
            data_index,
        ) = struct.unpack_from("<7I", blob, offset)
        entries.append(
            {
                "internal_id_index": internal_id_index,
                "provider_index": provider_index,
                "dependency_key_index": dependency_key_index,
                "bundled_asset_provider_crc": bundled_asset_provider_crc,
                "extra_data_offset": extra_data_offset,
                "resource_type_index": resource_type_index,
                "data_index": data_index,
            }
        )
        offset += 28
    return entries


def encode_extra_entries(entries: list[dict[str, Any]]) -> tuple[bytes, dict[int, int]]:
    blob = bytearray()
    offset_map: dict[int, int] = {}

    for entry in entries:
        new_offset = len(blob)
        offset_map[entry["old_offset"]] = new_offset

        assembly_bytes = entry["assembly_name"].encode("utf-8")
        class_bytes = entry["class_name"].encode("utf-8")
        payload_bytes = entry["payload"].encode("utf-16-le")

        if len(assembly_bytes) > 255 or len(class_bytes) > 255:
            raise ValueError("Addressables extra-data strings exceeded 255 bytes")

        blob.append(7)
        blob.append(len(assembly_bytes))
        blob.extend(assembly_bytes)
        blob.append(len(class_bytes))
        blob.extend(class_bytes)
        blob.extend(struct.pack("<I", len(payload_bytes)))
        blob.extend(payload_bytes)

    return bytes(blob), offset_map


def encode_entry_data(entries: list[dict[str, int]], offset_map: dict[int, int]) -> bytes:
    blob = bytearray(struct.pack("<I", len(entries)))
    for entry in entries:
        extra_offset = entry["extra_data_offset"]
        if extra_offset != 0xFFFFFFFF:
            extra_offset = offset_map[extra_offset]
        blob.extend(
            struct.pack(
                "<7I",
                entry["internal_id_index"],
                entry["provider_index"],
                entry["dependency_key_index"],
                entry["bundled_asset_provider_crc"],
                extra_offset,
                entry["resource_type_index"],
                entry["data_index"],
            )
        )
    return bytes(blob)


def patch_catalog_json(patched_string_bundle: Path) -> None:
    catalog = json.loads(CATALOG_JSON.read_text(encoding="utf-8"))
    entry_blob = base64.b64decode(catalog["m_EntryDataString"])
    extra_blob = base64.b64decode(catalog["m_ExtraDataString"])

    entry_records = parse_entry_data(entry_blob)
    extra_entries, extra_by_offset = parse_extra_entries(extra_blob)
    internal_ids: list[str] = catalog["m_InternalIds"]

    string_bundle_size = patched_string_bundle.stat().st_size
    string_bundle_hash = hashlib.md5(patched_string_bundle.read_bytes()).hexdigest()

    target_suffixes = {
        ZH_STRING_BUNDLE: {"bundle_size": string_bundle_size, "bundle_hash": string_bundle_hash},
        ZH_ASSET_BUNDLE: {},
    }

    patched_targets = set()
    for entry in entry_records:
        internal_id = internal_ids[entry["internal_id_index"]]
        for suffix, updates in target_suffixes.items():
            if not internal_id.endswith(suffix):
                continue

            extra_offset = entry["extra_data_offset"]
            extra_entry = extra_by_offset.get(extra_offset)
            if not extra_entry:
                continue

            payload = json.loads(extra_entry["payload"])
            payload["m_Crc"] = 0
            payload["m_UseCrcForCachedBundles"] = False

            if "bundle_size" in updates:
                payload["m_BundleSize"] = updates["bundle_size"]
            if "bundle_hash" in updates:
                payload["m_Hash"] = updates["bundle_hash"]

            extra_entry["payload"] = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
            patched_targets.add(suffix)

    missing = sorted(set(target_suffixes) - patched_targets)
    if missing:
        raise RuntimeError(f"Failed to patch catalog entries for: {missing}")

    new_extra_blob, offset_map = encode_extra_entries(extra_entries)
    new_entry_blob = encode_entry_data(entry_records, offset_map)

    catalog["m_ExtraDataString"] = base64.b64encode(new_extra_blob).decode("ascii")
    catalog["m_EntryDataString"] = base64.b64encode(new_entry_blob).decode("ascii")

    PATCH_CATALOG_JSON.parent.mkdir(parents=True, exist_ok=True)
    PATCH_CATALOG_JSON.write_text(
        json.dumps(catalog, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )


def build_managed_font_patch() -> Path:
    if not FONT_SOURCE_FILE.exists():
        raise FileNotFoundError(f"Missing packaged font file: {FONT_SOURCE_FILE}")

    subprocess.run(
        ["dotnet", "build", str(MANAGED_FONT_PATCH_PROJECT), "-c", "Release"],
        check=True,
        cwd=BASE_DIR,
    )

    built_dll = PATCH_MANAGED_DIR / MANAGED_FONT_PATCH_DLL
    if not built_dll.exists():
        raise FileNotFoundError(f"Managed font patch DLL was not built: {built_dll}")

    shutil.copy2(FONT_SOURCE_FILE, PATCH_MANAGED_DIR / FONT_SOURCE_FILE.name)
    required_charset_output = PATCH_MANAGED_DIR / REQUIRED_CHARSET_FILE.name
    if not required_charset_output.exists():
        raise FileNotFoundError(f"Required charset file was not copied to output: {required_charset_output}")
    return built_dll


def patch_assembly_csharp(managed_patch_dll: Path) -> Path:
    subprocess.run(
        [
            "dotnet",
            "run",
            "--project",
            str(ASSEMBLY_PATCH_PROJECT),
            "--configuration",
            "Release",
            "--",
            str(ASSEMBLY_CSHARP),
            str(managed_patch_dll),
            str(PATCH_ASSEMBLY_CSHARP),
        ],
        check=True,
        cwd=BASE_DIR,
    )
    if not PATCH_ASSEMBLY_CSHARP.exists():
        raise FileNotFoundError(f"Patched Assembly-CSharp.dll was not created: {PATCH_ASSEMBLY_CSHARP}")
    return PATCH_ASSEMBLY_CSHARP


def remove_legacy_patch_artifacts() -> None:
    legacy_paths = [
        PATCH_ROOT / "BepInEx",
        PATCH_ROOT / "0Harmony.dll",
        PATCH_ROOT / "doorstop_config.ini",
        PATCH_ROOT / "Hanpaemo.GUNTOUCHABLES.FontDoorstop.dll",
        PATCH_ROOT / "Hanpaemo.GUNTOUCHABLES.FontDoorstop.pdb",
        PATCH_ROOT / "Hanpaemo.GUNTOUCHABLES.PingDoorstop.dll",
        PATCH_ROOT / "Hanpaemo.GUNTOUCHABLES.PingDoorstop.pdb",
        PATCH_ROOT / "Hanpaemo.GUNTOUCHABLES.PingDoorstop35.dll",
        PATCH_ROOT / "NanumGothic.ttf",
        PATCH_ROOT / "winhttp.dll",
        PATCH_MANAGED_DIR / "net472",
    ]

    for path in legacy_paths:
        if path.is_dir():
            shutil.rmtree(path, ignore_errors=True)
        elif path.exists():
            path.unlink()

    keep_names = {MANAGED_FONT_PATCH_DLL, FONT_SOURCE_FILE.name, REQUIRED_CHARSET_FILE.name}
    if PATCH_MANAGED_DIR.exists():
        for path in PATCH_MANAGED_DIR.iterdir():
            if path.name in keep_names or path.name == "Assembly-CSharp.dll":
                continue
            if path.is_dir():
                shutil.rmtree(path, ignore_errors=True)
            else:
                path.unlink()


def write_readme(translated_count: int, total_count: int) -> None:
    readme = PATCH_ROOT / "README.txt"
    readme.write_text(
        "\n".join(
            [
                "GUNTOUCHABLES Korean Patch",
                "",
                "Copy these files into the game folder:",
                r"GUNTOUCHABLES_Data\StreamingAssets\aa\catalog.json",
                rf"GUNTOUCHABLES_Data\StreamingAssets\aa\StandaloneWindows64\{ZH_STRING_BUNDLE}",
                r"GUNTOUCHABLES_Data\Managed\Hanpaemo.GUNTOUCHABLES.ManagedFontPatch.dll",
                r"GUNTOUCHABLES_Data\Managed\NanumGothic.ttf",
                r"GUNTOUCHABLES_Data\Managed\required_chars_ko.txt",
                r"GUNTOUCHABLES_Data\Managed\Assembly-CSharp.dll",
                "",
                "What this patch does:",
                "- Reuses the zh-CN slot as Korean. Select \u7b80\u4f53\u4e2d\u6587 in-game.",
                "- Rebuilds the zh-CN string table using Korean text.",
                "- Falls back to English when a Korean translation cell is empty.",
                "- Patches the Addressables catalog so the modified string bundle loads without CRC mismatch.",
                "- Adds a managed font helper and patches SteamManager to initialize the Korean TMP fallback once during startup.",
                "",
                f"Rows with Korean translation: {translated_count}",
                f"Total string rows written: {total_count}",
            ]
        ),
        encoding="utf-8",
    )


def main() -> None:
    reset_patch_layout()
    translations = read_translation_map()
    write_required_charset(translations)
    patched_string_bundle, translated_count, total_count = patch_string_table_bundle(translations)
    patch_catalog_json(patched_string_bundle)
    managed_dll = build_managed_font_patch()
    patched_assembly = patch_assembly_csharp(managed_dll)
    remove_legacy_patch_artifacts()
    write_readme(translated_count, total_count)

    print(f"Translated rows: {translated_count}")
    print(f"Total rows written: {total_count}")
    print(f"Patched bundle: {patched_string_bundle}")
    print(f"Patched catalog: {PATCH_CATALOG_JSON}")
    print(f"Managed font patch: {managed_dll}")
    print(f"Patched Assembly-CSharp: {patched_assembly}")


if __name__ == "__main__":
    main()
