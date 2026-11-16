"""Regression tests proving incomplete future-operation coverage is rejected."""
import unittest
from check_capability_coverage import validate_cases


class CapabilityGuardTests(unittest.TestCase):
    def case(self, name):
        return {'operation': name, 'arguments': {}, 'expect': {'/data': {'equals': 1}}, 'compare': ['/data']}

    def test_new_operation_without_case_fails(self):
        with self.assertRaisesRegex(AssertionError, 'missing cases'):
            validate_cases({'old': {}, 'new_feature': {}}, [self.case('old')])

    def test_removed_operation_with_stale_case_fails(self):
        with self.assertRaisesRegex(AssertionError, 'stale cases'):
            validate_cases({'old': {}}, [self.case('old'), self.case('removed')])

    def test_case_without_assertions_or_parity_fails(self):
        for field in ['expect', 'compare']:
            case = self.case('feature')
            case[field] = {}
            with self.assertRaisesRegex(AssertionError, 'meaningful expectations'):
                validate_cases({'feature': {}}, [case])

    def test_duplicate_case_cannot_mask_missing_feature(self):
        with self.assertRaisesRegex(AssertionError, 'Duplicate'):
            validate_cases({'feature': {}}, [self.case('feature'), self.case('feature')])


if __name__ == '__main__':
    unittest.main()
