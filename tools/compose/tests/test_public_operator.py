"""Offline checks of #764's sanitized public evidence and expected command results."""
import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import qualify_public_operator as operator

ARCHIVE = '22222222-2222-4222-8222-222222222222'
SECRET = 'private-password-cookie-log-MUST-NOT-APPEAR'


class PublicOperatorTests(unittest.TestCase):
    """Missing, contradictory or unexpected evidence must prevent public PASS."""
    def test_acquisition_requires_exact_public_ready_identity_and_projects_no_path(self):
        """Standalone acquisition cannot report candidate, foreign or unavailable retained bytes."""
        path = '/owned/releases/v1.9.22'
        acquired = {'Path': path, 'Fingerprint': 'a' * 64, 'Version': '1.9.22',
                    'Images': 'execution-ready', 'Publisher': 'public-stable-GitHub',
                    'Integrity': 'GitHub-asset-SHA256', 'Private': SECRET}
        projected = operator.acquired_facts(json.dumps(acquired), Path(path))
        self.assertEqual(projected, {'fingerprint': 'a' * 64, 'version': '1.9.22'})
        for field, value in [('Path', '/foreign/' + SECRET), ('Version', '1.9.23'),
                             ('Images', 'not-qualified'), ('Publisher', 'candidate'),
                             ('Integrity', 'unchecked'), ('Fingerprint', 'invalid')]:
            with self.subTest(field=field), self.assertRaises(ValueError):
                operator.acquired_facts(json.dumps(dict(acquired, **{field: value})), Path(path))

    def test_verification_requires_selected_uuid_and_both_integrity_results(self):
        """A default verify must join the explicitly captured archive, not another successful archive."""
        verified = f'Archive {ARCHIVE}\nIntegrity: True; compatible: True\n'
        operator.verified_archive(verified, ARCHIVE)
        for output in ['', verified.replace(ARCHIVE, '33333333-3333-4333-8333-333333333333'),
                       verified.replace('True', 'False', 1), verified.replace('compatible: True', 'compatible: False'),
                       verified + f'Archive {ARCHIVE}\n']:
            with self.subTest(output=output), self.assertRaises(ValueError):
                operator.verified_archive(output, ARCHIVE)

    def test_toggle_requires_new_generation_and_preserves_all_other_policy_and_identity(self):
        """Disable and supported reconfigure may change only enabled state and generation."""
        before = {'Project': 'wayfarer-764-0123456789', 'Release': {'Fingerprint': 'c' * 64},
                  'Backup': {'Enabled': True, 'Generation': 'a' * 64,
                             'Destination': '/owned/' + SECRET, 'Source': {'ReleaseStatus': 'released'}}}
        after = copy.deepcopy(before)
        after['Backup'].update(Enabled=False, Generation='b' * 64)
        operator.policy_transition(before, after, False)
        enabled = copy.deepcopy(after)
        enabled['Backup'].update(Enabled=True, Generation='d' * 64)
        operator.policy_transition(after, enabled, True)
        for change in ['same-generation', 'enabled', 'source', 'release', 'destination']:
            altered = copy.deepcopy(after)
            if change == 'same-generation':
                altered['Backup']['Generation'] = before['Backup']['Generation']
            elif change == 'enabled':
                altered['Backup']['Enabled'] = True
            elif change == 'source':
                altered['Backup']['Source']['ReleaseStatus'] = 'candidate'
            elif change == 'release':
                altered['Release']['Fingerprint'] = 'd' * 64
            else:
                altered['Backup']['Destination'] = '/foreign'
            with self.subTest(change=change), self.assertRaises(ValueError):
                operator.policy_transition(before, altered, False)

    def test_every_required_observation_must_be_present_and_true(self):
        """Projection removes extra private facts and rejects partial or falsely green scenarios."""
        for scenario, names in operator.CHECKS.items():
            complete = dict.fromkeys(names, True)
            projected = operator.required_checks(dict(complete, Private=SECRET), names)
            self.assertEqual(projected, complete)
            self.assertNotIn(SECRET, json.dumps(projected))
            for name in names:
                for value in [False, None, 1]:
                    with self.subTest(scenario=scenario, missing=name, value=value), self.assertRaises(ValueError):
                        operator.required_checks(dict(complete, **{name: value}), names)
                with self.assertRaises(ValueError):
                    operator.required_checks({key: True for key in names if key != name}, names)

    def test_expected_refusal_is_logged_but_an_unexpected_exit_prevents_pass(self):
        """Command artifacts contain only the bounded command identity and expected/actual result."""
        with tempfile.TemporaryDirectory() as directory:
            journey = operator.OperatorJourney.__new__(operator.OperatorJourney)
            journey.log = Path(directory) / 'commands.jsonl'
            journey.stage, journey.command_count, journey.password = 'backup-toggle', 0, SECRET
            result = subprocess.CompletedProcess([], 2, 'raw log and cookie', 'Backup disabled.')
            with patch('qualify_ctl.Journey.ctl', return_value=result):
                journey.ctl('dispatch', 'backup', expected=2)
                with self.assertRaises(ValueError):
                    journey.ctl('dispatch', 'backup')
            records = [json.loads(line) for line in journey.log.read_text().splitlines()]
            self.assertEqual(records[0], {'stage': 'backup-toggle', 'command': 'dispatch backup',
                                        'exitCode': 2, 'expectedExitCode': 2, 'protectedStdin': False})
            self.assertEqual(records[1]['expectedExitCode'], 0)
            self.assertNotIn('raw log', journey.log.read_text())
            self.assertNotIn(SECRET, journey.log.read_text())


if __name__ == '__main__':
    unittest.main()
