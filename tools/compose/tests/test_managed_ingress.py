"""Focused negative proof for #749's ACME identities and joined browser observations."""
import copy
import json
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from qualify import BUNDLE, Stack, managed_config, assert_tls_transition


def tls_facts():
    """Only non-secret storage hashes and validated leaf identities are evidence."""
    return {'account': {'/data/caddy/acme/ca/users/default/default.json': 'a' * 64,
                        '/data/caddy/acme/ca/users/default/default.key': 'b' * 64},
            'certificate': {'/data/caddy/certificates/ca/wayfarer.example.org.crt': 'c' * 64},
            'volumes': {'/data': 'project_caddy-data', '/config': 'project_caddy-config'},
            'leaf': {'fingerprint': 'd' * 64, 'serial': '01', 'notAfter': 'Oct  3 15:00:00 2026 GMT'}}


class ManagedIngressTests(unittest.TestCase):
    """Prove failure-closed joins without repeating issuer or product endpoint matrices."""
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
