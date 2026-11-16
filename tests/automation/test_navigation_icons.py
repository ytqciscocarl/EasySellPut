#!/usr/bin/env python3
"""Protect the pinned icon subset, original canvas and generated desktop geometry."""
import hashlib
import importlib.util
import json
import pathlib
import re
import unittest
import xml.etree.ElementTree as ET

ROOT=pathlib.Path(__file__).resolve().parents[2]
ASSETS=ROOT/'src/Meili.Shared/Assets/NavigationIcons'
spec=importlib.util.spec_from_file_location('navigation_icon_importer',ROOT/'build/import-navigation-icons.py')
importer=importlib.util.module_from_spec(spec);spec.loader.exec_module(importer)

class NavigationIconsTests(unittest.TestCase):
    def test_each_source_path_preserves_its_own_relative_origin(self):
        svg='<svg viewBox="0 0 24 24" stroke-width="2" fill="none"><path d="m5 8 6 6"/><path d="m4 14 6-6 2-3"/></svg>'
        self.assertEqual(importer.geometry(svg),'M 5 8 l 6 6 M 4 14 l 6 -6 2 -3')
    def test_all_app_owned_icons_use_the_pinned_subset(self):
        for platform,extension in [('App','xaml'),('Mac','axaml')]:
            for path in (ROOT/f'src/Meili.{platform}').rglob('*'):
                if path.suffix not in ('.'+extension,'.cs') or any(x in path.parts for x in ('obj','bin','Resources')):continue
                source=path.read_text(encoding='utf-8-sig')
                self.assertNotIn('SymbolIcon',source,str(path));self.assertNotIn('SymbolRegular',source,str(path))
                if path.suffix=='.'+extension:
                    self.assertNotIn('<Path ',source,str(path))
                    self.assertNotRegex(source,r'<lucide:LucideIcon[^>]*Style="\{StaticResource (?:Caption|Body|Secondary)\}"')
                    self.assertNotRegex(source,r'(?:Content|Text)="[↵×?↑↓↻→≈+]"')
                for kind in re.findall(r'(?:Kind="|Kind = ")([A-Za-z]+)',source):self.assertIn(kind,importer.MAP,str(path))
        for asset in ('index.html','workbench.html'):
            self.assertIn('<script src="icons.js" defer></script>',(ROOT/'src/Meili.Shared/WebCharts'/asset).read_text())
        self.assertIn('LogicalName="WebCharts/icons.js"',(ROOT/'src/Meili.Shared/Meili.Shared.csproj').read_text())

    def test_pinned_svg_geometry_canvas_and_license(self):
        manifest=json.loads((ASSETS/'provenance.json').read_text())
        self.assertEqual(manifest['package'],'lucide-static');self.assertEqual(manifest['repository'],'https://github.com/lucide-icons/lucide.git')
        self.assertTrue(manifest['integrity'].startswith('sha512-'))
        generated=(ROOT/'src/Meili.Shared/Services/NavigationIcons.cs').read_text()
        for name,icon in importer.MAP.items():
            raw=(ASSETS/(icon+'.svg')).read_bytes();self.assertEqual(hashlib.sha256(raw).hexdigest(),manifest['icons'][icon])
            svg=ET.fromstring(raw);self.assertEqual(svg.attrib['viewBox'],'0 0 24 24')
            self.assertEqual(svg.attrib['stroke-width'],'2');self.assertEqual(svg.attrib['stroke-linecap'],'round');self.assertEqual(svg.attrib['stroke-linejoin'],'round')
            self.assertIn(f'public const string {name} = "{importer.geometry(raw)}";',generated)
        license_text=(ASSETS/'LICENSE').read_text();self.assertIn('Lucide Icons and Contributors',license_text);self.assertIn('Cole Bemis',license_text)
        self.assertIn('LogicalName="NavigationIcons/LICENSE"',(ROOT/'src/Meili.Shared/Meili.Shared.csproj').read_text())

if __name__=='__main__':unittest.main()
