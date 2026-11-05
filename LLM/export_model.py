"""Export trained RuneLM tensors to a dependency-free, little-endian C# format."""
from pathlib import Path
import argparse
import json
import struct
import hashlib
import torch
from model import Config, RuneLM
from data import normalize, DEFAULT_EXAMPLES

ROOT=Path(__file__).resolve().parent

def load_checkpoint(path):
    saved=torch.load(path,map_location='cpu',weights_only=True)
    model=RuneLM(Config(**saved['config'])); model.load_state_dict(saved['state']); model.eval()
    return model,saved


def export(path,out):
    torch.set_num_threads(2)
    model,saved=load_checkpoint(path)
    vocab=saved['vocab']; cfg=model.cfg
    out=Path(out);out.parent.mkdir(parents=True,exist_ok=True)
    with out.open('wb') as f:
        f.write(b'MWLM0001')
        f.write(struct.pack('<7i',1,cfg.dim,cfg.heads,cfg.layers,cfg.block_size,len(vocab),model.parameter_count()))
        for word in vocab:
            b=word.encode('utf-8');f.write(struct.pack('<i',len(b)));f.write(b)
        for p in model.export_tensors():
            f.write(p.detach().numpy().astype('<f4').tobytes())
    index={w:i for i,w in enumerate(vocab)}
    samples=[]
    for text in DEFAULT_EXAMPLES+['please heat the target with low power']:
        tokens=[index['<bos>']]+[index[c] for c in normalize(text)]+[index['<sep>']]
        with torch.no_grad(): logits=model(torch.tensor([tokens]))[0,-1].tolist()
        samples.append({'text':text,'tokens':tokens,'logits':logits})
    fixture={'format':1,'absolute_tolerance':.003,'source':'PyTorch full causal forward, float32 CPU',
             'model_sha256':hashlib.sha256(out.read_bytes()).hexdigest(),'samples':samples}
    (ROOT/'parity_fixture.json').write_text(json.dumps(fixture,ensure_ascii=False,indent=2),encoding='utf-8')
    meta={'name':'ManaWeave RuneLM Pilot','parameters':model.parameter_count(),'trained':True,
          'profile':saved['profile'],'steps':saved['steps'],'seed':saved['seed'],
          'vocabulary':len(vocab),'context_tokens':cfg.block_size,'architecture':'original pre-norm causal decoder Transformer, tied embedding, learned positions, ReLU MLP',
          'numeric_format':'float32 little endian','sha256':fixture['model_sha256'],'bytes':out.stat().st_size}
    (ROOT/'model_manifest.json').write_text(json.dumps(meta,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(meta),flush=True)

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--checkpoint',default=str(ROOT/'checkpoints/runelm_pilot.pt'))
    p.add_argument('--output',default=str(ROOT.parent/'UnityProject/Assets/Resources/ManaWeave/magic_model.bytes'))
    args=p.parse_args(); export(args.checkpoint,args.output)
