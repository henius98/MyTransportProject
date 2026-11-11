import time
import json
import urllib.request
import urllib.error
import urllib.parse
from datetime import date
from pathlib import Path
from typing import Any, Dict, List, Optional
import argparse

today_str = date.today().strftime("%Y-%m-%d")
MET_API_URL = f"https://api.data.gov.my/weather/forecast?date={today_str}"
NOMINATIM_URL = "https://nominatim.openstreetmap.org/search"
# Compliant User-Agent per Nominatim Acceptable Use Policy (identifying application and contact)
HEADERS = {
    "User-Agent": "MyTransportApp/1.0 (https://github.com/henius/MyTransportProject; transit-weather-planner)",
    "Referer": "https://data.gov.my"
}
REQUEST_TIMEOUT_SECONDS = 15

SCRIPT_DIR = Path(__file__).resolve().parent
OUTPUT_DIR = SCRIPT_DIR.parent / "MyTransportAppWASM" / "wwwroot" / "data"
OUTPUT_FILE = OUTPUT_DIR / "recreation_centres.json"
FAILED_FILE = SCRIPT_DIR / "failed_locations.json"

NAME_OVERRIDES: Dict[str, str] = {
    "FP Labuan": "Labuan",
    "WP Putrajaya": "Putrajaya"
}

HARDCODED_COORDINATES: Dict[str, List[Dict[str, float]]] = {
    "Pusat Konservasi Gajah Kebangsaan": [{"lat": 3.5907, "lon": 102.1469}],
    "Kuala Gandah Elephant Conservation Centre": [{"lat": 3.5907, "lon": 102.1469}],
    "Tasik Bera": [{"lat": 3.0833, "lon": 102.5833}],
    "Bera Lake": [{"lat": 3.0833, "lon": 102.5833}]
}


def geocode_location(loc_name: str) -> Optional[List[Dict[str, Any]]]:
    # Immediate bypass for known failing names
    if loc_name in HARDCODED_COORDINATES:
        return HARDCODED_COORDINATES[loc_name]

    loc_name = NAME_OVERRIDES.get(loc_name, loc_name)

    # Secondary check just in case the overridden name is in the hardcoded list
    if loc_name in HARDCODED_COORDINATES:
        return HARDCODED_COORDINATES[loc_name]

    query = f"{loc_name}, Malaysia"
    params = urllib.parse.urlencode({
        "q": query,
        "format": "json",
        "limit": 1
    })
    url = f"{NOMINATIM_URL}?{params}"
    req = urllib.request.Request(url, headers=HEADERS)
    for attempt in range(3):
        try:
            with urllib.request.urlopen(req, timeout=REQUEST_TIMEOUT_SECONDS) as geo_response:
                return json.loads(geo_response.read().decode('utf-8'))
        except urllib.error.HTTPError as e:
            if e.code in (429, 503) and attempt < 2:
                time.sleep(2.0 * (attempt + 1))
                continue
            raise


def save_merged_results(new_results: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    existing_results = []
    if OUTPUT_FILE.exists():
        try:
            with open(OUTPUT_FILE, "r", encoding="utf-8") as f:
                existing_results = json.load(f)
        except Exception:
            existing_results = []

    # Deduplicate existing and new results by location_id
    merged = {item["location_id"]: item for item in existing_results if isinstance(item, dict) and "location_id" in item}
    for item in new_results:
        merged[item["location_id"]] = item
    combined_results = list(merged.values())

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    with open(OUTPUT_FILE, "w", encoding="utf-8") as f:
        json.dump(combined_results, f, indent=2, ensure_ascii=False)
    return combined_results


def fetch_all_data() -> None:
    print("Fetching all raw data from MET Malaysia...")
    try:
        req = urllib.request.Request(MET_API_URL, headers=HEADERS)
        with urllib.request.urlopen(req, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            data = json.loads(response.read().decode('utf-8'))
    except Exception as e:
        print(f"Error fetching data from MET API: {e}")
        return

    if not isinstance(data, list):
        print(f"Unexpected response format from MET API: expected list, got {type(data).__name__}")
        return

    unique_locations = {}
    for item in data:
        if isinstance(item, dict):
            loc = item.get("location") or {}
            if isinstance(loc, dict):
                loc_id = loc.get("location_id")
                loc_name = loc.get("location_name")
                if loc_id and loc_name:
                    unique_locations[loc_id] = loc_name

    print(f"Extracted {len(unique_locations)} unique locations.")
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    results = []
    failed = []

    print("Geocoding coordinates via OpenStreetMap Nominatim...")
    for i, (loc_id, loc_name) in enumerate(unique_locations.items()):
        print(f"[{i+1}/{len(unique_locations)}] Geocoding {loc_name} ({loc_id})...",
              end=" ", flush=True)
        try:
            geo_data = geocode_location(loc_name)
            if geo_data and len(geo_data) > 0:
                lat = float(geo_data[0]["lat"])
                lon = float(geo_data[0]["lon"])
                results.append(
                    {"location_id": loc_id, "location_name": loc_name, "latitude": lat, "longitude": lon})
                print(f"Success! ({lat}, {lon})")
            else:
                failed.append({"location_id": loc_id, "location_name": loc_name,
                              "reason": "Nominatim returned no results"})
                print("Failed (No results)")
        except Exception as e:
            failed.append(
                {"location_id": loc_id, "location_name": loc_name, "reason": str(e)})
            print(f"Failed (Error: {e})")

        time.sleep(1.5)

    print(
        f"\nGeocoding complete. Success: {len(results)}, Failed: {len(failed)}")

    combined_results = save_merged_results(results)
    print(f"Saved master database to {OUTPUT_FILE} ({len(combined_results)} total locations)")

    if failed:
        with open(FAILED_FILE, "w", encoding="utf-8") as f:
            json.dump(failed, f, indent=2, ensure_ascii=False)
        print(f"Saved failed locations to {FAILED_FILE}")


def retry_failed() -> None:
    if not FAILED_FILE.exists():
        print("No failed_locations.json found. Nothing to retry.")
        return

    try:
        with open(FAILED_FILE, "r", encoding="utf-8") as f:
            failed_locations = json.load(f)
    except Exception as e:
        print(f"Error reading {FAILED_FILE}: {e}")
        return

    if not isinstance(failed_locations, list) or not failed_locations:
        print("Failed locations list is empty or invalid. Nothing to retry.")
        return

    valid_failed = [
        item for item in failed_locations
        if isinstance(item, dict) and "location_id" in item and "location_name" in item
    ]
    if not valid_failed:
        print("No valid location entries found in failed_locations.json.")
        return

    new_results = []
    still_failed = []

    print(f"Retrying {len(valid_failed)} failed locations...")
    for i, item in enumerate(valid_failed):
        loc_id = item["location_id"]
        loc_name = item["location_name"]
        print(f"[{i+1}/{len(valid_failed)}] Retrying {loc_name} ({loc_id})...",
              end=" ", flush=True)

        try:
            geo_data = geocode_location(loc_name)
            if geo_data and len(geo_data) > 0:
                lat = float(geo_data[0]["lat"])
                lon = float(geo_data[0]["lon"])
                new_results.append(
                    {"location_id": loc_id, "location_name": loc_name, "latitude": lat, "longitude": lon})
                print(f"Success! ({lat}, {lon})")
            else:
                still_failed.append(item)
                print("Still Failed (No results)")
        except Exception as e:
            item["reason"] = str(e)
            still_failed.append(item)
            print(f"Still Failed (Error: {e})")

        time.sleep(1.5)

    print(
        f"\nRetry complete. New Successes: {len(new_results)}, Still Failed: {len(still_failed)}")

    if new_results:
        save_merged_results(new_results)
        print(f"Appended {len(new_results)} locations to master database.")

    with open(FAILED_FILE, "w", encoding="utf-8") as f:
        json.dump(still_failed, f, indent=2, ensure_ascii=False)
    print(f"Updated failed locations list. {len(still_failed)} remaining.")


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Fetch and geocode location data from MET Malaysia API.")
    parser.add_argument("--retry", action="store_true",
                        help="Retry geocoding for previously failed locations.")
    args = parser.parse_args()

    if args.retry:
        retry_failed()
    else:
        fetch_all_data()


if __name__ == "__main__":
    main()
