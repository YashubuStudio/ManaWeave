"""Package original project sources, weights, documentation and test evidence.

No Unity build is performed. Editor caches/test binaries/intermediate logs are
excluded. Stable Unity .meta IDs are generated only for currently untracked
assets. A file manifest allows the delivered archive to be audited.
"""
from pathlib import Path
import hashlib
import json
import uuid
import zipfile

ROOT=Path(__file__).resolve().parents[1]
NAMESPACE=uuid.UUID('3565d707-844a-4710-87c8-31c29dad495d')
SKIP_PARTS={'Library','Temp','Obj','Logs','UserSettings','Builds','bin','obj','__pycache__','.venv','.git'}
SKIP_FILES={'CONTRACT.md','magic-verification.log','training_live.log','evaluation_live.log','acceptance_live.log','PACKAGE_MANIFEST.json'}

def meta(path):
    sidecar=path.with_name(path.name+'.meta')
    if sidecar.exists():return
    relative=path.relative_to(ROOT/'UnityProject').as_posix()
    guid=uuid.uuid5(NAMESPACE,relative).hex
    header=f'fileFormatVersion: 2\nguid: {guid}\n'
    if path.is_dir():
        body='folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n'
    elif path.suffix=='.cs':
        body='MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {fileID: 0}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n'
    elif path.suffix=='.asmdef':
        body='AssemblyDefinitionImporter:\n  externalObjects: {}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n'
    elif path.suffix=='.shader':
        body='ShaderImporter:\n  externalObjects: {}\n  defaultTextures: []\n  nonModifiableTextures: []\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n'
    else:
        body='TextScriptImporter:\n  externalObjects: {}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n'
    sidecar.write_text(header+body,encoding='utf-8')

def main():
    assets=ROOT/'UnityProject/Assets'
    for p in sorted(assets.rglob('*')):
        if p.suffix!='.meta':meta(p)
    files=[]
    for p in sorted(ROOT.rglob('*')):
        rel=p.relative_to(ROOT)
        if not p.is_file() or set(rel.parts)&SKIP_PARTS or p.name in SKIP_FILES:continue
        if p.name.endswith(('.zip','.pyc','.tmp')):continue
        files.append(p)
    manifest={
        'name':'ManaWeave','edition':'Original research sandbox prototype',
        'target_unity':'6000.3.26f1 / Unity 6.3 LTS',
        'unity_editor_run_verified':False,'unity_player_binary_included':False,
        'pretrained_third_party_model_included':False,
        'files':[{'path':p.relative_to(ROOT).as_posix(),'bytes':p.stat().st_size,
                  'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in files]
    }
    mf=ROOT/'PACKAGE_MANIFEST.json'
    mf.write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    files.append(mf)
    out=ROOT.parent/'ManaWeave_Unity63_Prototype.zip'
    with zipfile.ZipFile(out,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as archive:
        for p in files:archive.write(p,ROOT.name+'/'+p.relative_to(ROOT).as_posix())
    with zipfile.ZipFile(out) as archive:
        bad=archive.testzip()
        if bad:raise RuntimeError('CRC failure '+bad)
        entries=set(archive.namelist())
        required=['UnityProject/Assets/Scenes/Sandbox.unity','UnityProject/Assets/Resources/ManaWeave/magic_model.bytes',
                  'UnityProject/Assets/Resources/ManaWeave/wsp_model.bytes','UnityProject/Packages/manifest.json',
                  'UnityProject/ProjectSettings/ProjectVersion.txt','README_JA.md','Documentation/VERIFICATION.md']
        for rel in required:
            if ROOT.name+'/'+rel not in entries:raise RuntimeError('Missing deliverable '+rel)
    print(json.dumps({'archive':str(out),'bytes':out.stat().st_size,'files':len(files),
       'sha256':hashlib.sha256(out.read_bytes()).hexdigest()},ensure_ascii=False))

if __name__=='__main__':main()
