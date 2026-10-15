const storageKey = "mytransport.google-connections.v1";
let memoryConnections = {};
let storageAvailable = true;

function getStorage() {
    if (typeof sessionStorage !== "undefined") return sessionStorage;
    if (typeof localStorage !== "undefined") return localStorage;
    return null;
}

function readConnections() {
    if (!storageAvailable) return memoryConnections;
    let stored;
    try {
        const storage = getStorage();
        if (!storage) {
            storageAvailable = false;
            return memoryConnections;
        }
        stored = storage.getItem(storageKey);
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
        const storage = getStorage();
        if (!storage) {
            storageAvailable = false;
            return;
        }
        if (Object.keys(connections).length) storage.setItem(storageKey, JSON.stringify(connections));
        else storage.removeItem(storageKey);
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
    try {
        if (typeof sessionStorage !== "undefined") sessionStorage.removeItem(storageKey);
    } catch {}
    try {
        if (typeof localStorage !== "undefined") localStorage.removeItem(storageKey);
    } catch {}
    writeConnections({});
}
