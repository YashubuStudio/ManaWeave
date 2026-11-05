"""Train an original RuneLM checkpoint on authored synthetic spell instructions.
Usage: python train.py --steps 1600 --threads 4
CPU pilot defaults, or --profile research_0_1b --device cuda for the untrained
larger configuration. Never download pretrained model weights.
"""
import argparse
from dataclasses import asdict
from pathlib import Path
import json
import math
import random
import time
import hashlib
import torch
from torch.nn import functional as F
from model import Config, RuneLM
from data import write_dataset, normalize

ROOT=Path(__file__).resolve().parent

def make_rows(records, index):
    rows=[]
    for rec in records:
        p=[index['<bos>']]+[index[c] for c in rec['text']]+[index['<sep>']]
        a=[index[c] for c in rec['answer']]
        seq=p+a
        labels=[-100]*(len(p)-1)+a
        rows.append((seq[:-1], labels))
    return rows


def batch(rows, indices, device):
    chosen=[rows[i] for i in indices]
    n=max(len(x[0]) for x in chosen)
    x=torch.zeros(len(chosen), n, dtype=torch.long)
    y=torch.full_like(x,-100)
    for i,(a,b) in enumerate(chosen):
        x[i,:len(a)]=torch.tensor(a)
        y[i,:len(b)]=torch.tensor(b)
    return x.to(device),y.to(device)


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--steps', type=int,default=1600)
    parser.add_argument('--batch', type=int,default=64)
    parser.add_argument('--threads',type=int,default=4)
    parser.add_argument('--seed',type=int,default=731)
    parser.add_argument('--device',default='cpu')
    parser.add_argument('--profile', choices=['pilot','research_0_1b'],default='pilot')
    parser.add_argument('--resume',default='')
    args=parser.parse_args()
    torch.set_num_threads(args.threads)
    random.seed(args.seed); torch.manual_seed(args.seed)
    train,test,vocab=write_dataset(ROOT/'data')
    idx={x:i for i,x in enumerate(vocab)}
    cfg=Config(vocab_size=len(vocab)) if args.profile=='pilot' else Config(
        vocab_size=len(vocab),dim=896,heads=14,layers=10,block_size=256)
    model=RuneLM(cfg).to(args.device)
    if args.resume:
        saved=torch.load(args.resume,map_location=args.device,weights_only=True)
        if saved['config']!=asdict(cfg): raise ValueError('resume model config differs')
        model.load_state_dict(saved['state'])
    model.train()
    optim=torch.optim.AdamW(model.parameters(),lr=0.0015,weight_decay=.01,betas=(.9,.95))
    rows=make_rows(train,idx)
    # Extra reject samples balance a small out-of-domain subset against command templates.
    rejects=[i for i,r in enumerate(train) if r['answer'][0]=='@reject']
    start=time.time(); log=[]
    print(json.dumps({'config':asdict(cfg),'parameters':model.parameter_count(),
                      'train':len(train),'heldout':len(test),'torch':torch.__version__}),flush=True)
    for step in range(1,args.steps+1):
        n_reject=max(1,args.batch//12)
        indices=[random.randrange(len(rows)) for _ in range(args.batch-n_reject)] + random.choices(rejects,k=n_reject)
        x,y=batch(rows,indices,args.device)
        lr=0.0015*(.15+.85*.5*(1+math.cos(math.pi*step/args.steps)))
        if step<50: lr*=step/50
        for p in optim.param_groups: p['lr']=lr
        logits=model(x)
        loss=F.cross_entropy(logits.reshape(-1,cfg.vocab_size),y.reshape(-1),ignore_index=-100)
        optim.zero_grad(set_to_none=True)
        loss.backward(); torch.nn.utils.clip_grad_norm_(model.parameters(),1.)
        optim.step()
        if step==1 or step%100==0 or step==args.steps:
            entry={'step':step,'loss':float(loss.detach()),'elapsed_seconds':round(time.time()-start,2),'learning_rate':lr}
            print(json.dumps(entry),flush=True); log.append(entry)
        if step%400==0 or step==args.steps:
            (ROOT/'checkpoints').mkdir(exist_ok=True)
            target=ROOT/'checkpoints'/('runelm_'+args.profile+'.pt')
            torch.save({'config':asdict(cfg),'state':model.cpu().state_dict(),'vocab':vocab,
                        'seed':args.seed,'steps':step,'profile':args.profile,
                        'original_random_initialization':True,'training_log':log},target)
            model.to(args.device)
    report={'seed':args.seed,'steps':args.steps,'batch_size':args.batch,'threads':args.threads,
            'device':args.device,'seconds':time.time()-start,'parameters':model.parameter_count(),
            'config':asdict(cfg),'train_examples':len(train),'heldout_examples':len(test),
            'train_sha256':hashlib.sha256((ROOT/'data/train.jsonl').read_bytes()).hexdigest(),
            'heldout_sha256':hashlib.sha256((ROOT/'data/heldout.jsonl').read_bytes()).hexdigest(),
            'train_test_prompt_overlap':0,'heldout_protocol':'disjoint surface templates; fixed alphabet includes evaluation chars',
            'initialization':'random; no external or pretrained weights','log':log}
    (ROOT/'training_report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')

if __name__=='__main__': main()
