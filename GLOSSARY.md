# MyTransportProject Domain Glossary

Core domain vocabulary for the transit tracking, weather planning, and route evaluation dashboard.

## Weather Planning

**Weather Query**:
The spatial and temporal criteria submitted by a traveler, defining geographic coordinates, optional station identifier, departure timestamp, and return timestamp.
_Avoid_: WeatherRequest, LocationFilter

**Weather Plan**:
A consolidated forecast bundle containing validated realtime conditions and multi-day return outlooks across all active meteorological providers for a single weather query.
_Avoid_: WeatherPlanResult, ForecastSummary

**Weather Time Slice**:
A discrete temporal meteorological record capturing temperature, precipitation, rain probability, wind, UV index, and weather summary conditions for a specific point in time.
_Avoid_: WeatherPeriod, TimeSlot, ForecastItem

**Weather Provider**:
An external meteorological data authority (such as Open-Meteo, MetMalaysia, or Singapore NEA) supplying observational or predictive weather feeds.
_Avoid_: WeatherSource, WeatherAPI, WeatherService

**Weather Provider Adapter**:
An internal translation component that binds a meteorological provider's HTTP endpoints and payloads to canonical weather time slices behind the weather planner seam.
_Avoid_: WeatherClient, WeatherWrapper

## Transit & Live Tracking

**Transit Provider**:
A regional public transport operating agency (such as Rapid Bus Penang, Rapid Bus KL, KTMB, or BAS.MY) with defined coverage coordinates and GTFS feeds.
_Avoid_: Agency, TransportCompany

**Vehicle Location**:
A point-in-time telemetry snapshot of a transit vehicle containing route identifier, vehicle identifier, coordinates, bearing, and speed.
_Avoid_: BusPosition, GPSPoint

**Enriched Route**:
A navigational transit itinerary cross-referenced against live vehicle telemetry, estimated arrival delays, and optional astrological fortune rating.
_Avoid_: LiveRoute, EvaluatedRoute

## Place Resolution & Station Catalogs

**Place Resolution**:
The unified conversion of user queries (coordinate pairs, station identifiers, or place names) into verified geographic coordinates and formatted addresses, backed by Google Places (New) and reverse geocoding APIs.
_Avoid_: GeoLookup, AddressSearch

**Weather Station Catalog**:
The static repository of verified meteorological monitoring stations and recreation centres (e.g. MetMalaysia `recreation_centres.json`) with spatial indexing for nearest-station queries.
_Avoid_: LocationDatabase, RecreationCentresJson


