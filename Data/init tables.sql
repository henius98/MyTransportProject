CREATE TABLE Trip (
    TripId VARCHAR(20) PRIMARY KEY,
    RouteId VARCHAR(6) NOT NULL,
    VehicleId VARCHAR(10) NOT NULL
);

-- Table to store position info
CREATE TABLE VehiclePositions (
    TripId VARCHAR(20) NOT NULL,
    Latitude FLOAT NOT NULL,
    Longitude FLOAT NOT NULL,
    Bearing INTEGER NOT NULL,
    Speed FLOAT NOT NULL,
    Timestamp INTEGER NOT NULL,
    FOREIGN KEY (TripId) REFERENCES Trip(TripId)
);

-- static API data --
CREATE TABLE trips (
    route_id INTEGER,
    service_id INTEGER,
    trip_id VARCHAR(20) PRIMARY KEY,
    trip_headsign TEXT,
    direction_id INTEGER, -- 0 = from Jetty or 1 = end Jetty
    shape_id TEXT
    -- FOREIGN KEY (route_id) REFERENCES trips(routes),
    -- FOREIGN KEY (service_id) REFERENCES trips(calendar),
    -- FOREIGN KEY (shape_id) REFERENCES trips(shapes),
);
CREATE TABLE calendar (
    service_id INTEGER,
    monday BOOLEAN,
    tuesday BOOLEAN,
    wednesday BOOLEAN,
    thursday BOOLEAN,
    friday BOOLEAN,
    saturday BOOLEAN,
    sunday BOOLEAN,
    start_date INTEGER,
    end_date INTEGER,
    PRIMARY KEY (service_id, start_date, end_date)
);
CREATE TABLE routes (
    route_id INTEGER PRIMARY KEY,
    agency_id TEXT,
    route_short_name TEXT,
    route_long_name TEXT,
    route_type INTEGER
    -- FOREIGN KEY (agency_id) REFERENCES trips(agency),
);
CREATE TABLE shapes (
    shape_id INTEGER,
    shape_pt_lat REAL,
    shape_pt_lon REAL,
    shape_pt_sequence INTEGER,
    shape_dist_traveled REAL,
    PRIMARY KEY (shape_id, shape_pt_sequence)
);
CREATE TABLE stops (
    stop_id INTEGER PRIMARY KEY,
    stop_code TEXT,
    stop_name TEXT,
    stop_lat REAL,
    stop_lon REAL
);
CREATE TABLE stop_times (
    trip_id TEXT,
    arrival_time TEXT,
    departure_time TEXT,
    stop_id INTEGER,
    stop_sequence INTEGER,
    stop_headsign TEXT,
    shape_dist_traveled REAL,
    PRIMARY KEY (trip_id, stop_sequence)
    -- FOREIGN KEY (trip_id) REFERENCES trips(trip_id),
    -- FOREIGN KEY (stop_id) REFERENCES stops(stop_id)
);