"""Enforce the shared design source, licensed fonts and native/embedded component contracts."""
import hashlib
import importlib.util
import json
import pathlib
import re
import unittest
ROOT=pathlib.Path(__file__).resolve().parents[2]
class DesignSystemTests(unittest.TestCase):
    def test_generated_tokens_are_current(self):
        spec=importlib.util.spec_from_file_location('design_generator',ROOT/'build/generate-design-system.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        for file,expected in module.outputs().items():self.assertEqual((ROOT/file).read_text(),expected,file)
    def test_bundled_fonts_preserve_upstream_bytes_and_notices(self):
        fonts=ROOT/'src/Meili.Shared/Assets/Fonts'
        for item in json.loads((fonts/'provenance.json').read_text()):self.assertEqual(hashlib.sha256((fonts/item['file']).read_bytes()).hexdigest(),item['sha256'])
        for name in ['SourceSans','SourceSerif','SourceCode']:self.assertIn('SIL OPEN FONT LICENSE',(fonts/(name+'-OFL.txt')).read_text())
    def test_pages_compose_components_without_copying_button_templates(self):
        for platform,extension in [('App','xaml'),('Mac','axaml')]:
            for path in (ROOT/f'src/Meili.{platform}/Views').rglob('*.'+extension):
                source=path.read_text()
                self.assertNotRegex(source,r'<ControlTemplate TargetType="(?:Button|ToggleButton|TextBox|ComboBox)"')
                self.assertNotRegex(source,r'(?:Background|Foreground|BorderBrush)="#[0-9A-Fa-f]{6}')

    def test_wpf_components_load_their_own_metric_dependencies(self):
        # WPF StaticResource cannot rely on a later parent/sibling dictionary during template materialization.
        components=(ROOT/'src/Meili.App/DesignSystem/Components.xaml').read_text()
        self.assertIn('<ResourceDictionary Source="Tokens.xaml"/>',components)
        self.assertLess(components.index('Source="Tokens.xaml"'),components.index('<Style '))

    def test_components_are_used_by_both_desktops(self):
        for platform,sub,ext in [('App','Views/Pages','xaml'),('Mac','Views','axaml')]:
            root=ROOT/f'src/Meili.{platform}'
            for component in ['AppCard','AppDialog','AppDrawer','AppLoading','AppSelect','AppFormHeader']:self.assertTrue((root/f'Views/Controls/{component}.cs').exists())
            chart=(root/f'{sub}/ChartsPage.{ext}').read_text();self.assertIn('controls:AppDrawer',chart);self.assertIn('IsOpen="{Binding ShowInspector, Mode=TwoWay}"',chart);self.assertIn('controls:AppLoading',chart)
            dashboard=(root/f'{sub}/DashboardPage.{ext}').read_text();self.assertIn('ds:AppCard',dashboard)
        server=(ROOT/'src/Meili.Shared/Services/WebChartSession.cs').read_text();self.assertIn("font-src 'self'",server);self.assertIn('"fonts/SourceSans3-Regular.ttf"',server)
if __name__=='__main__':unittest.main()
