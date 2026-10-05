const storageKey = "mytransport.google-connections.v1";
let memoryConnections = {};
let storageAvailable = true;

function readConnections() {
    if (!storageAvailable) return memoryConnections;
    let stored;
    try {
        stored = localStorage.getItem(storageKey);
    } catch {
        // Browsers can block storage; keep access available for the current page.
        storageAvailable = false;
        return memoryConnections;
    }
    try {
        const parsed = JSON.parse(stored ?? "{}");
        if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) throw new Error("Invalid saved connections");
        memoryConnections = parsed;
    } catch {
        clearConnections();
    }
    return memoryConnections;
}

function writeConnections(connections) {
    memoryConnections = connections;
    if (!storageAvailable) return;
    try {
        if (Object.keys(connections).length) localStorage.setItem(storageKey, JSON.stringify(connections));
        else localStorage.removeItem(storageKey);
    } catch {
        // A failed write must still leave the newly granted token usable in memory.
        storageAvailable = false;
    }
}

export function getConnection(service) {
    return readConnections()[service] ?? null;
}

export function setConnection(service, connection) {
    const connections = readConnections();
    if (connection) connections[service] = connection;
    else delete connections[service];
    writeConnections(connections);
}

export function clearConnections() {
    writeConnections({});
}
