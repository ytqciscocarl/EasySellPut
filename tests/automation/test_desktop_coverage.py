import copy
import unittest
from desktop_coverage import validate_desktop

class DesktopCoverageTests(unittest.TestCase):
    def setUp(self):
        self.manifest={'viewModels':[{'path':'page.cs','scope':'Data query','operations':['query'],'commands':{'Load':{'operations':['query']}}}]}
        self.sources={'page.cs':'[RelayCommand] private async Task Load() {}'}
    def test_reviewed_mapping_passes(self): self.assertEqual(validate_desktop({'query'},self.manifest,self.sources),(1,1))
    def test_new_page_requires_review(self):
        with self.assertRaisesRegex(AssertionError,'inventory'):validate_desktop({'query'},self.manifest,{**self.sources,'new.cs':''})
    def test_new_command_requires_review(self):
        with self.assertRaisesRegex(AssertionError,'command inventory'):validate_desktop({'query'},self.manifest,{'page.cs':self.sources['page.cs']+'[RelayCommand] private void NewFeature() {}'})
    def test_missing_tool_fails(self):
        with self.assertRaisesRegex(AssertionError,'missing MCP'):validate_desktop(set(),self.manifest,self.sources)
    def test_blank_exclusion_is_not_coverage(self):
        entry=self.manifest['viewModels'][0]['commands']['Load'];entry.clear();entry['uiOnly']=''
        with self.assertRaisesRegex(AssertionError,'specific UI'):validate_desktop({'query'},self.manifest,self.sources)
