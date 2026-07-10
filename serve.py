import http.server
import socketserver
import os
import urllib.parse

PORT = 8000
DIRECTORY = "release/wwwroot"

class SPAHTTPRequestHandler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=".", **kwargs)

    def do_GET(self):
        # We want to serve 'release/wwwroot' as '/MyTransportProject/'
        parsed_path = urllib.parse.urlparse(self.path).path
        if parsed_path.startswith('/MyTransportProject/'):
            # Strip the prefix to serve from release/wwwroot
            local_path = parsed_path[len('/MyTransportProject/'):]
            if not local_path:
                local_path = '/'
            # Check if exists
            full_path = os.path.join(DIRECTORY, local_path.lstrip('/'))
            if os.path.isdir(full_path) and os.path.exists(os.path.join(full_path, 'index.html')):
                self.path = '/release/wwwroot/' + local_path.lstrip('/') + ('/' if not local_path.endswith('/') else '') + 'index.html'
            elif os.path.exists(full_path) and not os.path.isdir(full_path):
                self.path = '/release/wwwroot/' + local_path.lstrip('/')
            else:
                self.path = '/release/wwwroot/404.html'
        else:
            self.path = '/release/wwwroot/404.html'
            
        super().do_GET()

with socketserver.TCPServer(("", PORT), SPAHTTPRequestHandler) as httpd:
    print("serving at port", PORT)
    httpd.serve_forever()
