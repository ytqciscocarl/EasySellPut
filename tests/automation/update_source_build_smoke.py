"""Build real upstream/fork/local CLIs and inspect immutable metadata. No HTTP or release writes."""
import json
import os
import pathlib
import subprocess
import tempfile
from check_update_source import ROOT,validate

def run():
    with tempfile.TemporaryDirectory(prefix='update-source-build-') as directory:
        root=pathlib.Path(directory)
        clean=os.environ.copy()
        clean.pop('GITHUB_ACTIONS',None);clean.pop('GITHUB_REPOSITORY',None);clean.pop('UpdateRepository',None)
        command=['dotnet','build',str(ROOT/'src/Meili.Cli/Meili.Cli.csproj'),'-c','Release','--no-restore','-m:3','-v','quiet']
        key=(ROOT/'src/Meili.Application/Updates/update-public-key.pem').read_text()
        for name,repository in [('upstream','ytqciscocarl/EasySellPut'),('fork','two4i6/EasySellPut'),('local','')]:
            env=clean|({'GITHUB_ACTIONS':'true','GITHUB_REPOSITORY':repository} if repository else {})
            output=root/name
            result=subprocess.run(command+['-o',str(output)],env=env,cwd=ROOT,capture_output=True,text=True,timeout=240)
            if result.returncode:raise RuntimeError(result.stdout+result.stderr)
            # Even a hostile runtime environment cannot change the compiled update source.
            process=subprocess.run(['dotnet',str(output/'easysellput.dll'),'update','source','--json'],env=clean|{'GITHUB_ACTIONS':'true','GITHUB_REPOSITORY':'other/RuntimeOverride'},capture_output=True,text=True,check=True,timeout=30)
            report=json.loads(process.stdout)['data']
            if repository:validate(report,repository,key)
            elif report['enabled'] or report['repository'] or report['historyUrl'] is not None:raise ValueError('Local build unexpectedly has an update source')
            print('PASS: real '+name+' build preserves repository binding against runtime environment overrides')
        result=subprocess.run(command+['-o',str(root/'mismatch'),'-p:UpdateRepository=other/Override'],env=clean|{'GITHUB_ACTIONS':'true','GITHUB_REPOSITORY':'two4i6/EasySellPut'},cwd=ROOT,capture_output=True,text=True,timeout=240)
        if result.returncode==0 or 'CI update source must match' not in result.stdout+result.stderr:raise ValueError('CI accepted a conflicting update repository')
        print('PASS: CI build rejects conflicting repository metadata')

if __name__=='__main__':run()
