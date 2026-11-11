import http.server
import socketserver
import os
import argparse
import urllib.parse
from pathlib import Path

parser = argparse.ArgumentParser(description="Serve release SPA directory with base path fallback.")
parser.add_argument("--port", type=int, default=int(os.environ.get("PORT", 8000)), help="Port to listen on (default: 8000 or $PORT)")
parser.add_argument("--host", default=os.environ.get("HOST", ""), help="Host to bind (default: '' or $HOST)")
parser.add_argument("--directory", default=os.environ.get("DIRECTORY", "release/wwwroot"), help="Directory to serve (default: release/wwwroot)")
args = parser.parse_args()

PORT = args.port
HOST = args.host
DIRECTORY = args.directory

class SPAHTTPRequestHandler(http.server.SimpleHTTPRequestHandler):
    extensions_map = {
        **http.server.SimpleHTTPRequestHandler.extensions_map,
        ".wasm": "application/wasm",
        ".json": "application/json",
        ".js": "application/javascript",
        ".mjs": "application/javascript",
        ".css": "text/css",
        ".svg": "image/svg+xml",
        ".webmanifest": "application/manifest+json",
        ".dll": "application/octet-stream",
        ".pdb": "application/octet-stream",
        ".dat": "application/octet-stream",
        ".blat": "application/octet-stream",
    }

    def __init__(self, *args, **kwargs):
        self.base_dir = Path(DIRECTORY).resolve()
        super().__init__(*args, directory=str(self.base_dir), **kwargs)

    def end_headers(self):
        self.send_header("X-Content-Type-Options", "nosniff")
        if self.path.endswith("service-worker.js") or self.path.endswith("index.html"):
            self.send_header("Cache-Control", "no-cache, no-store, must-revalidate")
        super().end_headers()

    def do_GET(self):
        # We want to serve 'release/wwwroot' as '/MyTransportProject/'
        parsed = urllib.parse.urlparse(self.path)
        parsed_path = parsed.path

        # Redirect root and base path without trailing slash to base path with trailing slash
        if parsed_path in ('', '/', '/MyTransportProject'):
            self.send_response(301)
            self.send_header("Location", "/MyTransportProject/")
            self.end_headers()
            return

        resolved_route = None
        if parsed_path.startswith('/MyTransportProject/'):
            # Strip the prefix to serve from release/wwwroot
            local_path = parsed_path[len('/MyTransportProject/'):].lstrip('/')
            full_path = (self.base_dir / local_path).resolve() if local_path else self.base_dir
            if not full_path.is_relative_to(self.base_dir):
                self.send_error(403, "Forbidden")
                return

            if full_path.is_dir() and (full_path / 'index.html').exists():
                resolved_route = '/' + (local_path + '/' if local_path else '') + 'index.html'
            elif full_path.exists() and not full_path.is_dir():
                resolved_route = '/' + local_path

        if resolved_route is not None:
            self.path = resolved_route
        elif (self.base_dir / 'index.html').exists():
            self.path = '/index.html'
        elif (self.base_dir / '404.html').exists():
            self.path = '/404.html'
        else:
            self.send_error(404, "File not found")
            return

        super().do_GET()

socketserver.TCPServer.allow_reuse_address = True
with socketserver.TCPServer((HOST, PORT), SPAHTTPRequestHandler) as httpd:
    display_host = HOST if HOST else "0.0.0.0"
    print(f"serving '{DIRECTORY}' at http://{display_host}:{PORT}/MyTransportProject/")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        print("\nShutting down server...")
        httpd.server_close()
