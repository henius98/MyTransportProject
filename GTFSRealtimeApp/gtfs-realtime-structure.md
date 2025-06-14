FeedMessage
├── FeedHeader
│   ├── gtfs_realtime_version
│   ├── incrementality
│   ├── timestamp
│   ├── feed_version
│   └── extensions
└── FeedEntity (repeated)
    ├── id
    ├── is_deleted
    ├── One of:
    │   ├── TripUpdate
    │   │   ├── TripDescriptor
    │   │   ├── StopTimeUpdate (repeated)
    │   │   │   ├── StopTimeEvent (arrival/departure)
    │   │   │   └── StopTimeProperties
    │   │   ├── VehicleDescriptor
    │   │   ├── timestamp
    │   │   ├── delay
    │   │   └── TripProperties
    │   ├── VehiclePosition
    │   │   ├── Position
    │   │   ├── TripDescriptor
    │   │   ├── VehicleDescriptor
    │   │   ├── current_stop_sequence
    │   │   ├── stop_id
    │   │   ├── current_status
    │   │   ├── timestamp
    │   │   ├── congestion_level
    │   │   ├── occupancy_status
    │   │   ├── occupancy_percentage
    │   │   └── CarriageDetails (repeated)
    │   ├── Alert
    │   │   ├── TimeRange (repeated)
    │   │   ├── EntitySelector (repeated)
    │   │   ├── cause
    │   │   ├── effect
    │   │   ├── url
    │   │   ├── header_text
    │   │   ├── description_text
    │   │   ├── tts_header_text
    │   │   ├── tts_description_text
    │   │   ├── severity_level
    │   │   ├── image
    │   │   ├── image_alternative_text
    │   │   ├── cause_detail
    │   │   └── effect_detail
    │   ├── Shape
    │   ├── Stop
    │   ├── TripModifications
    │   │   ├── SelectedTrips (repeated)
    │   │   ├── start_times (repeated)
    │   │   ├── service_dates (repeated)
    │   │   └── Modification (repeated)
    │   └── extensions
    └── extensions