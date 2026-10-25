"""Exercise the first-download boundary with real pip and only a local wheel server."""
import http.server
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import unittest
import venv


@unittest.skipUnless(os.environ.get('WAYFARER_TEST_PIP_WHEEL'), 'provide the verified pip 26.2.2 wheel')
class PipBootstrapTests(unittest.TestCase):
    """The same bootstrap command must reject corruption and make no other requests."""

    def test_first_download_is_hash_bound_and_has_no_index_or_version_check(self):
        """A positive install and a corrupt response use fresh, unmodified ensurepip venvs."""
        wheel = Path(os.environ['WAYFARER_TEST_PIP_WHEEL']).read_bytes()
        source = (Path(__file__).parents[1] / 'bootstrap-pip.sh').read_text()
        for corrupt in (False, True):
            with self.subTest(corrupt=corrupt), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                venv.create(root / 'venv', with_pip=True)
                requests = []
                payload = wheel + b'corrupt' if corrupt else wheel

                class Handler(http.server.BaseHTTPRequestHandler):
                    """Record all requests; never forward to an index or external service."""

                    def do_GET(self):
                        """Serve the intended wheel bytes, including the corruption counterexample."""
                        requests.append(self.path)
                        self.send_response(200)
                        self.send_header('Content-Length', str(len(payload)))
                        self.end_headers()
                        self.wfile.write(payload)

                    def log_message(self, *args):
                        """Keep routine local access logs out of test output."""

                with http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler) as server:
                    worker = threading.Thread(target=server.serve_forever, daemon=True)
                    worker.start()
                    # Replace only the transport endpoint; retain production hash and pip options.
                    start = source.index('https://files.pythonhosted.org/')
                    end = source.index('#sha256=', start)
                    script = root / 'bootstrap.sh'
                    script.write_text(source[:start] + f'http://127.0.0.1:{server.server_port}/pip-26.2.2-py3-none-any.whl' + source[end:])
                    try:
                        result = subprocess.run(['bash', str(script), str(root / 'venv/bin/python')],
                                                text=True, capture_output=True, timeout=60)
                    finally:
                        server.shutdown()
                        worker.join()
                self.assertEqual(['/pip-26.2.2-py3-none-any.whl'], requests)
                if corrupt:
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn('DO NOT MATCH THE HASHES', result.stderr)
                else:
                    self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                    self.assertIn('PASS pip vendored roots', result.stdout)
