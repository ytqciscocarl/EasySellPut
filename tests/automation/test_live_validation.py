"""Offline negative checks for the real-market acceptance criteria."""
import datetime as dt
import unittest
from live_cli_mcp_smoke import fresh_sourced, verify


class LiveValidationTests(unittest.TestCase):
    def quote_data(self):
        now = dt.datetime.now(dt.timezone.utc).isoformat()
        return {'missingSymbols': [], 'quotes': [
            {'symbol': symbol, 'price': 100, 'source': 'test', 'asOf': now,
             'fetchedAt': now, 'isRealtime': False, 'delayMinutes': 15}
            for symbol in ['AAPL', 'MSFT', 'SPY']]}

    def test_missing_symbol_is_not_a_successful_release_check(self):
        data = self.quote_data()
        data['missingSymbols'] = ['MSFT']
        with self.assertRaises(AssertionError):
            verify('get_quotes', data)

    def test_old_market_snapshot_is_rejected(self):
        data = self.quote_data()
        data['quotes'][0]['asOf'] = '2000-01-01T00:00:00Z'
        with self.assertRaisesRegex(AssertionError, 'Stale'):
            verify('get_quotes', data)

    def test_quote_without_market_time_is_rejected(self):
        data = self.quote_data()
        data['quotes'][0]['asOf'] = None
        with self.assertRaisesRegex(AssertionError, 'market timestamp'):
            verify('get_quotes', data)

    def test_offline_cache_does_not_prove_live_access(self):
        now = dt.datetime.now(dt.timezone.utc).isoformat()
        with self.assertRaisesRegex(AssertionError, 'cached offline'):
            verify('get_bars', {'source': 'test', 'fetchedAt': now, 'offlineCache': True})

    def test_fiscal_period_is_not_mistaken_for_a_stale_quote(self):
        fresh_sourced({'source': 'test', 'fetchedAt': dt.datetime.now(dt.timezone.utc).isoformat(),
                       'asOf': '2000-01-01T00:00:00Z'}, market_time=False)


if __name__ == '__main__':
    unittest.main()
