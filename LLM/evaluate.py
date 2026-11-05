"""Autoregressive constrained decoding on disjoint template holdout.

Reports semantic exact match and selective accuracy. Grammar validity alone is
not a measure of meaning preservation. Synthetic results are not evidence of
unrestricted Japanese understanding.
"""
import argparse
from pathlib import Path
import json
import time
import random
import torch
from data import allowed, normalize, DEFAULT_EXAMPLES
from export_model import load_checkpoint

ROOT=Path(__file__).resolve().parent

@torch.no_grad()
def generate_batch(model,vocab,records):
    idx={w:i for i,w in enumerate(vocab)}
    seqs=[[idx['<bos>']]+[idx.get(c,idx['<unk>']) for c in normalize(r['text'])]+[idx['<sep>']] for r in records]
    histories=[[] for _ in records]; conf=[1. for _ in records]; finished=[False]*len(records)
    for _ in range(5):
        n=max(map(len,seqs));x=torch.zeros(len(seqs),n,dtype=torch.long)
        for j,seq in enumerate(seqs): x[j,:len(seq)]=torch.tensor(seq)
        out=model(x)
        for j in range(len(records)):
            if finished[j]: continue
            logits=out[j,len(seqs[j])-1]
            valid=allowed(vocab,histories[j])
            scores=logits[valid].softmax(dim=-1)
            choice=int(scores.argmax())
            token=valid[choice]
            if len(valid)>1: conf[j]=min(conf[j],float(scores[choice]))
            histories[j].append(token);seqs[j].append(token)
            if token==idx['<eos>']:finished[j]=True
    return [([vocab[t] for t in h],c) for h,c in zip(histories,conf)]


def score(model,vocab,rows,threshold=.72,batch_size=48):
    results=[]
    for i in range(0,len(rows),batch_size):
        part=rows[i:i+batch_size]
        pred=generate_batch(model,vocab,part)
        for row,(output,c) in zip(part,pred):
            results.append({'text':row['text'],'expected':row['answer'],'actual':output,
                            'confidence':c,'exact':output==row['answer'],
                            'accepted':output[0]!='@reject' and c>=threshold})
    valid=[r for r in results if r['expected'][0]!='@reject']
    rejects=[r for r in results if r['expected'][0]=='@reject']
    accepted=[r for r in valid if r['accepted']]
    return {'count':len(results),'exact':sum(r['exact'] for r in results),
            'exact_match':sum(r['exact'] for r in results)/max(1,len(results)),
            'valid_count':len(valid),'valid_exact_match':sum(r['exact'] for r in valid)/max(1,len(valid)),
            'accepted_valid_count':len(accepted),'coverage':len(accepted)/max(1,len(valid)),
            'accepted_exact_match':sum(r['exact'] for r in accepted)/max(1,len(accepted)),
            'reject_count':len(rejects),'reject_false_accept_count':sum(r['accepted'] for r in rejects),
            'failures':[r for r in results if not r['exact']][:40]},results


def main():
    ap=argparse.ArgumentParser();ap.add_argument('--checkpoint',default=str(ROOT/'checkpoints/runelm_pilot.pt'))
    ap.add_argument('--threshold',type=float,default=.72);ap.add_argument('--threads',type=int,default=3)
    args=ap.parse_args();torch.set_num_threads(args.threads)
    model,saved=load_checkpoint(args.checkpoint);vocab=saved['vocab']
    train=[json.loads(s) for s in (ROOT/'data/train.jsonl').read_text(encoding='utf-8').splitlines()]
    test=[json.loads(s) for s in (ROOT/'data/heldout.jsonl').read_text(encoding='utf-8').splitlines()]
    start=time.time();report={}
    random.seed(731);sample=random.sample(train,min(256,len(train)))
    report['train_sample'],_=score(model,vocab,sample,args.threshold)
    report['heldout'],results=score(model,vocab,test,args.threshold)
    examples=generate_batch(model,vocab,[{'text':s} for s in DEFAULT_EXAMPLES])
    report['examples']=[{'text':t,'tokens':x,'confidence':c} for t,(x,c) in zip(DEFAULT_EXAMPLES,examples)]
    report['threshold']=args.threshold;report['seconds']=time.time()-start
    report['parameters']=model.parameter_count()
    report['measurement']='Actual autoregressive decoding, constrained output grammar, unseen surface-template split. Does not measure unrestricted natural language.'
    report['no_validation_tuning_claim']='This holdout is development evaluation, not a blind independent production acceptance set.'
    (ROOT/'evaluation_report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    (ROOT/'evaluation_predictions.jsonl').write_text(''.join(json.dumps(r,ensure_ascii=False)+'\n' for r in results),encoding='utf-8')
    print(json.dumps(report,ensure_ascii=False,indent=2),flush=True)

if __name__=='__main__': main()
