#!/usr/bin/env python3
"""Read-only UX component inventory. Baseline remains pinned; --current emits working tree declarations."""
from pathlib import Path
import json,subprocess,sys,xml.etree.ElementTree as ET
from html.parser import HTMLParser
ROOT=Path(__file__).resolve().parents[1]
PAGES=['Dashboard','Watchlist','Charts','Options','Strategy','News','Alerts','DataSources','Settings']
def build(current=False):
    records=[]
    def read(path):return (ROOT/path).read_text(encoding="utf-8") if current else subprocess.check_output(['git','show','67f0357:'+path],cwd=ROOT,text=True,encoding='utf-8')
    for platform,extension,sub in [('App','xaml','Views/Pages'),('Mac','axaml','Views')]:
        for page in PAGES+['Shell']:
            path=f'src/Meili.{platform}/{sub}/{page}Page.{extension}' if page!='Shell' else f'src/Meili.{platform}/MainWindow.{extension}'
            root=ET.fromstring(read(path))
            def visit(node,parents):
                tag=node.tag.split('}')[-1];attrs={key.split('}')[-1]:value for key,value in node.attrib.items()}
                if not any(x in tag for x in ['Setter','Trigger','Style','Definition']) and tag not in ['ResourceDictionary','Styles']:
                    records.append({'platform':platform,'page':page,'type':tag,'name':attrs.get('Name'),'attributes':attrs,'composition':parents,'source':path})
                for child in node:visit(child,parents+[tag])
            visit(root,[])
    class HTML(HTMLParser):
        def __init__(self,file):super().__init__();self.file=file
        def handle_starttag(self,tag,attrs):
            if tag in ['button','input','select','details','summary','nav','aside','footer','svg','canvas']:
                records.append({'platform':'WebView','page':self.file,'type':tag,'attributes':dict(attrs),'source':'src/Meili.Shared/WebCharts/'+self.file})
    for file in ['index.html','workbench.html']:
        parser=HTML(file);parser.feed(read('src/Meili.Shared/WebCharts/'+file))
    return {'baseline':'67f0357','mode':'current' if current else 'baseline','scope':'All native page/shell XML declarations and static bundled HTML. Dynamic/template instances follow the manual report and browser tests.','components':records}
if __name__=='__main__':
    current='--current' in sys.argv;result=build(current);output=ROOT/'docs/ux'/('components-after.json' if current else 'components-before.json');output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print(len(result['components']),output)
