import os
import time
import json
import urllib.request
import urllib.error
import urllib.parse
from datetime import date
import argparse

today_str = date.today().strftime("%Y-%m-%d")
MET_API_URL = f"https://api.data.gov.my/weather/forecast?date={today_str}"
NOMINATIM_URL = "https://nominatim.openstreetmap.org/search"
HEADERS = {
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36",
    "Referer": "https://data.gov.my"
}
OUTPUT_DIR = os.path.join(os.path.dirname(
    __file__), "..", "MyTransportAppWASM", "wwwroot", "data")
OUTPUT_FILE = os.path.join(OUTPUT_DIR, "recreation_centres.json")
FAILED_FILE = os.path.join(os.path.dirname(__file__), "failed_locations.json")

NAME_OVERRIDES = {
    "FP Labuan": "Labuan",
    "WP Putrajaya": "Putrajaya"
}

HARDCODED_COORDINATES = {
    "Pusat Konservasi Gajah Kebangsaan": [{"lat": 3.5907, "lon": 102.1469}],
    "Kuala Gandah Elephant Conservation Centre": [{"lat": 3.5907, "lon": 102.1469}],
    "Tasik Bera": [{"lat": 3.0833, "lon": 102.5833}],
    "Bera Lake": [{"lat": 3.0833, "lon": 102.5833}]
}


def geocode_location(loc_name):
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
    with urllib.request.urlopen(req) as geo_response:
        return json.loads(geo_response.read().decode('utf-8'))


def fetch_all_data():
    print("Fetching all raw data from MET Malaysia...")
    try:
        req = urllib.request.Request(MET_API_URL, headers=HEADERS)
        with urllib.request.urlopen(req) as response:
            data = json.loads(response.read().decode('utf-8'))
    except Exception as e:
        print(f"Error fetching data from MET API: {e}")
        return

    unique_locations = {}
    for item in data:
        loc_id = item.get("location", {}).get("location_id")
        loc_name = item.get("location", {}).get("location_name")
        if loc_id and loc_name:
            unique_locations[loc_id] = loc_name

    print(f"Extracted {len(unique_locations)} unique locations.")
    os.makedirs(OUTPUT_DIR, exist_ok=True)

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

    with open(OUTPUT_FILE, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2, ensure_ascii=False)
    print(f"Saved master database to {OUTPUT_FILE}")

    if failed:
        with open(FAILED_FILE, "w", encoding="utf-8") as f:
            json.dump(failed, f, indent=2, ensure_ascii=False)
        print(f"Saved failed locations to {FAILED_FILE}")


def retry_failed():
    if not os.path.exists(FAILED_FILE):
        print("No failed_locations.json found. Nothing to retry.")
        return

    with open(FAILED_FILE, "r", encoding="utf-8") as f:
        failed_locations = json.load(f)

    if not failed_locations:
        print("Failed locations list is empty. Nothing to retry.")
        return

    existing_results = []
    if os.path.exists(OUTPUT_FILE):
        with open(OUTPUT_FILE, "r", encoding="utf-8") as f:
            existing_results = json.load(f)

    new_results = []
    still_failed = []

    print(f"Retrying {len(failed_locations)} failed locations...")
    for i, item in enumerate(failed_locations):
        loc_id = item["location_id"]
        loc_name = item["location_name"]
        print(f"[{i+1}/{len(failed_locations)}] Retrying {loc_name} ({loc_id})...",
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
        existing_results.extend(new_results)
        with open(OUTPUT_FILE, "w", encoding="utf-8") as f:
            json.dump(existing_results, f, indent=2, ensure_ascii=False)
        print(f"Appended {len(new_results)} locations to master database.")

    with open(FAILED_FILE, "w", encoding="utf-8") as f:
        json.dump(still_failed, f, indent=2, ensure_ascii=False)
    print(f"Updated failed locations list. {len(still_failed)} remaining.")


def main():
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
