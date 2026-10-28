"""Focused negative proof for #749's ACME identities and joined browser observations."""
import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from qualify import BUNDLE, Stack, managed_config, assert_tls_transition
from qualify_caddy_replacement import ManagedJourney


def tls_facts():
    """Only non-secret storage hashes and validated leaf identities are evidence."""
    return {'account': {'/data/caddy/acme/ca/users/default/default.json': 'a' * 64,
                        '/data/caddy/acme/ca/users/default/default.key': 'b' * 64},
            'certificate': {'/data/caddy/certificates/ca/wayfarer.example.org.crt': 'c' * 64},
            'volumes': {'/data': 'project_caddy-data', '/config': 'project_caddy-config'},
            'leaf': {'fingerprint': 'd' * 64, 'serial': '01', 'notAfter': 'Oct  3 15:00:00 2026 GMT'}}


class ManagedIngressTests(unittest.TestCase):
    """Prove failure-closed joins without repeating issuer or product endpoint matrices."""
    def test_failed_proxy_command_retains_exact_exit_and_stderr_without_stdin(self):
        """The missing hosted setup failure must remain diagnosable after disposable cleanup."""
        with tempfile.TemporaryDirectory() as directory:
            journey = ManagedJourney.__new__(ManagedJourney)
            journey.directory, journey.project, journey.ca = Path(directory), 'fixture-project', 'fixture-acme'
            journey.host = Mock()
            journey.install_adapter()
            wrapper = journey.host.call_args.kwargs['data']
            docker = Path(directory) / 'docker'
            docker.write_text('#!/bin/sh\necho "synthetic Docker failure" >&2\nexit 42\n')
            docker.chmod(0o755)
            script = Path(directory) / 'adapter'
            script.write_text(wrapper.replace('/usr/bin/docker', str(docker)))
            command = ['--host', 'unix:///var/run/docker.sock', 'compose', '--project-name', journey.project,
                       'up', '-d', '--wait', '--wait-timeout', '180', 'caddy']
            result = subprocess.run(['sh', str(script), *command], input='protected-stdin-MUST-NOT-APPEAR',
                                    text=True, capture_output=True)
            self.assertEqual(result.returncode, 42)
            log = (Path(directory) / 'proxy-command.log').read_text()
            self.assertIn(' '.join(f'<{arg}>' for arg in command), log)
            self.assertIn('exitCode=42 stage=docker-command', log)
            self.assertIn('synthetic Docker failure', (Path(directory) / 'proxy-command.stderr').read_text())
            self.assertNotIn('protected-stdin', log)

    def test_initial_up_primes_stopped_proxy_before_first_execution(self):
        """Execute the old operator's command and retain create, DNS/trust, start and verified HTTPS order."""
        with tempfile.TemporaryDirectory() as directory:
            fixture = Path(directory)
            journey = ManagedJourney.__new__(ManagedJourney)
            journey.directory, journey.project, journey.ca = fixture, 'fixture-project', 'fixture-acme'
            journey.host = Mock()
            journey.install_adapter()
            docker = fixture / 'docker'
            docker.write_text(f'''#!/bin/sh
printf '%s\\n' "$*" >> '{fixture}/calls'
case "$1" in
 ps) echo proxy ;;
 inspect) echo caddy@source ;;
 start) test -e '{fixture}/primed-proxy' ;;
esac
''')
            docker.chmod(0o755)
            script = fixture / 'adapter'
            script.write_text(journey.host.call_args.kwargs['data'].replace('/usr/bin/docker', str(docker)))
            result = subprocess.run(['sh', str(script), 'compose', '--project-name', journey.project,
                                     'up', '-d', '--wait', '--wait-timeout', '180', 'caddy'], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            calls = (fixture / 'calls').read_text().splitlines()
            self.assertEqual(calls[0], 'compose --project-name fixture-project create caddy')
            self.assertLess(next(i for i, call in enumerate(calls) if call.startswith('network connect ')),
                            next(i for i, call in enumerate(calls) if call.startswith('cp ')))
            self.assertLess(next(i for i, call in enumerate(calls) if call.startswith('cp ')), calls.index('start proxy'))
            self.assertIn('exec proxy wget -O /dev/null https://wayfarer.example.org/health/ready', calls)
            self.assertFalse(any(call.startswith('restart ') or ' up ' in call for call in calls))

    def test_failure_diagnostics_retain_issuer_state_and_omit_container_environment(self):
        """Keep native image, OOM/network/configuration and operator evidence without dumping credentials."""
        with tempfile.TemporaryDirectory() as directory:
            journey = ManagedJourney.__new__(ManagedJourney)
            journey.directory, journey.project, journey.ca = Path(directory), 'fixture-project', 'fixture-acme'
            journey.docker_volumes = '/var/lib/docker/volumes'
            journey.setup_result = subprocess.CompletedProcess([], 1, 'Starting HTTPS...', 'Safe setup failure')
            journey.host = Mock(return_value=subprocess.CompletedProcess([], 0, 'fixture diagnostics', ''))
            journey.ctl = Mock(return_value=subprocess.CompletedProcess([], 1, 'safe operator diagnostics', ''))
            container = dict(Id='id', Name='proxy', Image='sha256:fixture', State={'OOMKilled': False},
                             Mounts=[], NetworkSettings={'Networks': {}},
                             Config=dict(Image='caddy@sha256:fixture', Entrypoint=['caddy'], Cmd=['run'],
                                         Labels={}, Env=['PRIVATE=credential-MUST-NOT-APPEAR']))
            def observed(*args, **kwargs):
                """Only container inspection carries the deliberately private environment counterexample."""
                output = json.dumps([container]) if args[1] == 'inspect' else 'native fixture facts'
                return subprocess.CompletedProcess(args, 0, output, '')
            with patch('qualify_caddy_replacement.run', side_effect=observed):
                journey.retain_diagnostics(Path(directory))
            self.assertIn('Starting HTTPS', (Path(directory) / 'setup-operator.log').read_text())
            self.assertTrue((Path(directory) / 'fixture-acme.log').exists())
            facts = json.loads((Path(directory) / 'fixture-project-caddy-1.json').read_text())
            self.assertEqual(facts['State'], {'OOMKilled': False})
            self.assertIn('native fixture facts', (Path(directory) / 'fixture-acme-image.log').read_text())
            self.assertIn('native fixture facts', (Path(directory) / 'edge-network.json').read_text())
            self.assertNotIn('credential-MUST-NOT-APPEAR', ''.join(path.read_text() for path in Path(directory).iterdir()))
            self.assertEqual([call.args[0] for call in journey.ctl.call_args_list], ['status', 'doctor'])

    def test_automatic_acme_retains_shipped_site(self):
        """Only a global fixture preamble changes the client certificate issuer seam."""
        site = (BUNDLE / 'caddy/Caddyfile').read_text()
        config = managed_config(site)
        self.assertTrue(config.endswith(site))
        self.assertIn('acme_ca https://acme.example.org/acme/local/directory', config)
        self.assertIn('acme_ca_root /etc/caddy/fixture-root.crt', config)
        self.assertNotIn('tls internal', config)
        self.assertNotIn('tls ', config)
        self.assertNotIn('local_certs', config)

    def test_replacement_requires_unchanged_account_certificate_and_volumes(self):
        """Changing any persisted owner or the served leaf must reject replacement proof."""
        before = tls_facts()
        assert_tls_transition(before, copy.deepcopy(before))
        for key in ('account', 'certificate', 'volumes', 'leaf'):
            with self.subTest(key=key):
                changed = copy.deepcopy(before)
                changed[key][next(iter(changed[key]))] = 'changed'
                with self.assertRaises(AssertionError):
                    assert_tls_transition(before, changed)

    def test_renewal_requires_new_served_and_persisted_leaf_with_same_owner(self):
        """A restarted server, unchanged leaf or new account cannot count as renewal."""
        before = tls_facts()
        after = copy.deepcopy(before)
        after['leaf'].update(fingerprint='e' * 64, serial='02', notAfter='Oct  3 15:01:00 2026 GMT')
        after['certificate'][next(iter(after['certificate']))] = 'f' * 64
        assert_tls_transition(before, after, renewal=True)
        for key in ('account', 'volumes', 'certificate', 'leaf'):
            with self.subTest(key=key):
                invalid = copy.deepcopy(after)
                invalid[key] = copy.deepcopy(before[key]) if key in ('certificate', 'leaf') else {}
                with self.assertRaises(AssertionError):
                    assert_tls_transition(before, invalid, renewal=True)
        for key in ('fingerprint', 'serial', 'notAfter'):
            with self.subTest(key=key):
                invalid = copy.deepcopy(after)
                invalid['leaf'][key] = before['leaf'][key]
                with self.assertRaises(AssertionError):
                    assert_tls_transition(before, invalid, renewal=True)

    def test_bearer_is_stdin_only_and_result_does_not_retain_it(self):
        """Even a response carrying unexpected secret fields is projected to the location ID."""
        secret = 'synthetic-bearer-MUST-NOT-APPEAR'
        stack = Stack('/tmp/unused', 'app', 'db')
        stack.sql = Mock(side_effect=['0', '42', '1'])
        stack.container = Mock(return_value='fixture-container')
        response = Mock(returncode=0, stdout=json.dumps({'location': {'id': 42}, 'token': secret}))
        stack.curl = Mock(return_value=response)
        with patch('qualify.run', return_value=Mock(stdout='', stderr='')):
            result = stack.check_in(secret, 1)
        self.assertEqual(result, {'locationId': 42, 'status': 200})
        args, kwargs = stack.curl.call_args
        self.assertNotIn(secret, json.dumps(args))
        self.assertEqual(kwargs['data'], f'Authorization: Bearer {secret}\n')
        self.assertNotIn(secret, json.dumps(result))

    def test_joined_browser_evidence_requires_canonical_routes_and_reconnect_update(self):
        """Run the browser helper's real projection, including negative joins, without Chromium."""
        helper = Path(__file__).resolve().parents[1] / 'managed_ingress.mjs'
        script = """
            import { validateObservation } from './tools/compose/managed_ingress.mjs';
            const facts = JSON.parse(await new Promise(resolve => {
                let text = ''; process.stdin.on('data', data => text += data);
                process.stdin.on('end', () => resolve(text));
            }));
            const result = facts.map(value => {
                try { return validateObservation(value, 'https://wayfarer.example.org', 'compose-admin'); }
                catch { return null; }
            });
            console.log(JSON.stringify(result));
        """
        good = {'embedUrl': 'https://wayfarer.example.org/Public/Users/Timeline/compose-admin/embed',
                'fullViewPath': '/Public/Users/Timeline/compose-admin', 'leaflet': True,
                'tile': {'path': '/Public/tiles/2/1/1.png', 'status': 200}, 'connections': 2,
                'first': {'connection': 1, 'locationId': 42, 'sse': True, 'refresh': True},
                'second': {'connection': 2, 'locationId': 43, 'sse': True, 'refresh': True},
                'token': 'not-retained'}
        cases = [good]
        for key, value in [('embedUrl', 'https://wayfarer.example.org/fixture'),
                           ('fullViewPath', '/fixture'), ('leaflet', False),
                           ('tile', {'path': '/fixture.png', 'status': 200}), ('connections', 1)]:
            cases.append({**copy.deepcopy(good), key: value})
        for value in [{'connection': 1}, {'sse': False}, {'refresh': False}, {'locationId': 42}]:
            invalid = copy.deepcopy(good)
            invalid['second'].update(value)
            cases.append(invalid)
        result = subprocess.run(['node', '--input-type=module', '-e', script],
                                cwd=helper.parents[2], input=json.dumps(cases),
                                capture_output=True, text=True, check=True)
        observations = json.loads(result.stdout)
        self.assertIsNotNone(observations[0])
        self.assertNotIn('token', observations[0])
        self.assertTrue(all(value is None for value in observations[1:]))


if __name__ == '__main__':
    unittest.main()
