import base64
import hashlib
import pathlib
import re
import sys
import unittest
sys.path.insert(0,str(pathlib.Path(__file__).parent))
from check_update_source import validate,ROOT

class UpdateSourceTests(unittest.TestCase):
    def test_upstream_and_fork_packages_are_validated_against_their_own_source(self):
        key=(ROOT/'src/Meili.Application/Updates/update-public-key.pem').read_text()
        der=base64.b64decode(''.join(re.search(r'-----BEGIN PUBLIC KEY-----([A-Za-z0-9+/=\s]+)-----END PUBLIC KEY-----',key)[1].split()))
        for repo in ('ytqciscocarl/EasySellPut','two4i6/EasySellPut'):
            report=dict(enabled=True,repository=repo,publicKeySha256=hashlib.sha256(der).hexdigest(),historyUrl='https://github.com/'+repo+'/releases')
            validate(report,repo,key)
            for patch in (dict(repository='other/EasySellPut'),dict(publicKeySha256='0'*64),dict(enabled=False),dict(historyUrl='https://github.com/other/EasySellPut/releases')):
                with self.assertRaises(ValueError):validate(report|patch,repo,key)
