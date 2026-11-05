"""Direct weight-space construction of a bounded magic-language transducer.

No fitting, gradients, external checkpoint or optimizer. This is a small neural
conditional token decoder, NOT a general Japanese LLM. RuneLM provides the
separate trained causal Transformer. All semantic routing below is compiled to
numeric embeddings and affine/ReLU matrices, used unchanged by the C# player.
"""
from pathlib import Path
import json
import struct
import sys
import unicodedata
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'LLM'))
import data

OPS = data.OPS[:-1]
FEATURES = OPS + ['small', 'large', 'stone', 'wood', 'iron', 'crystal', 'reject', 'unknown', 'normal']
F = {n:i for i,n in enumerate(FEATURES)}
STAGES = ['stage'+str(i) for i in range(5)]
INPUTS = FEATURES + STAGES
OUTPUTS = ['reject','eos'] + OPS + ['r1','r2','r3','p0','p20','p50','p80','p50000','p120000','p300000','stone','wood','iron','crystal']

def normalize(s):
    return unicodedata.normalize('NFKC', s).lower().strip()

def construct():
    lex = {}
    def add(word, *features):
        word = normalize(word)
        if not word: return
        vector = tuple(sorted(set(features)))
        if word in lex and lex[word] != vector:
            raise ValueError('conflicting lexeme '+word)
        lex[word] = vector

    for op in OPS:
        if op != 'build':
            for word in data.JP_ACTIONS[op] + data.EN_ACTIONS[op]: add(word, op)
    for mat, jnames in data.MATERIALS.items():
        for word in data.JP_BUILD:
            for name in jnames: add(word.format(mat=name), 'build', mat)
        for word in data.EN_BUILD: add(word.format(mat=mat), 'build', mat)
    for size in [1,2,3]:
        feature = {1:'small',2:'normal',3:'large'}[size]
        for word in data.JP_SIZE[size] + data.EN_SIZE[size]: add(word, feature)
    for word in data.JP_TARGET + data.EN_TARGET + [
        'ください','下さい','お願いします','お願い','please','now',
        '照準の先を','照準の場所を','この地点を','いま','今',
        '。','、',',','.','!','！',' ', '\t', '\n']:
        add(word)
    for word, op in [('加熱','heat'),('温める','heat'),('冷却','cool'),('冷やす','cool'),
                     ('掘削','excavate'),('掘る','excavate'),('魔力充填','charge'),
                     ('錬成','transmute'),('錬金変換','transmute')]: add(word,op)
    for word in ['しない','ないで','やめて','禁止','せず','するな','するなよ',
                 'not',"don't",'never','except','unless','if','まで','ただし','以外','なければ']:
        add(word,'reject')
    # Explicit no-op/rejection words cannot silently disappear as neutral text.
    for word in data.REJECT_TRAIN + data.REJECT_TEST: add(word,'reject')
    vocab = sorted(lex, key=lambda s:(-len(s), s))
    embedding = np.zeros((len(vocab),len(FEATURES)), np.float32)
    for i,w in enumerate(vocab):
        for name in lex[w]: embedding[i,F[name]] = 1.

    # First layer forwards observable bits and derives defaults and conflicts.
    h1names = INPUTS + ['default_size','default_stone','no_op','multi_op','multi_size','multi_material']
    H1 = {n:i for i,n in enumerate(h1names)}
    W1 = np.zeros((len(h1names),len(INPUTS)),np.float32)
    b1 = np.zeros(len(h1names),np.float32)
    for i in range(len(INPUTS)): W1[i,i] = 1
    W1[H1['default_size'],F['small']] = -1
    W1[H1['default_size'],F['large']] = -1
    b1[H1['default_size']] = 1
    for mat in ['wood','iron','crystal']: W1[H1['default_stone'],F[mat]] = -1
    b1[H1['default_stone']] = 1
    for op in OPS:
        W1[H1['no_op'],F[op]] = -1
        W1[H1['multi_op'],F[op]] = 1
    b1[H1['no_op']] = 1
    b1[H1['multi_op']] = -1
    for size in ['small','normal','large']: W1[H1['multi_size'],F[size]] = 1
    b1[H1['multi_size']] = -1
    for mat in ['stone','wood','iron','crystal']: W1[H1['multi_material'],F[mat]] = 1
    W1[H1['multi_material'],F['build']] = 1
    b1[H1['multi_material']] = -2

    # AND(a,b,c)=ReLU(a+b+c-2) for binary features; one numeric gate
    # writes one next-token logit. Invalid input dominates every decode stage.
    gates = []
    def gate(names, output, strength=20.):
        gates.append((names,output,strength))
    for invalid in ['reject','unknown','no_op','multi_op','multi_size','multi_material']:
        gate([invalid],'reject',1000.)
    for op in OPS:
        gate([op,'stage0'],op)
        for s,flag in [(1,'small'),(2,'default_size'),(3,'large')]:
            radius = s if op in ['heat','cool'] else max(1,s-1)
            power = {1:50000,2:120000,3:300000}[s] if op in ['heat','cool'] else (
                {1:20,2:50,3:80}[s] if op == 'charge' else 0)
            gate([op,flag,'stage1'],'r'+str(radius))
            gate([op,flag,'stage2'],'p'+str(power))
        if op == 'build':
            for mat in ['stone','wood','iron','crystal']:
                gate([op,'default_stone' if mat=='stone' else mat,'stage3'],mat)
        else:
            gate([op,'stage3'],'stone')
    gate(['stage4'],'eos')
    W2 = np.zeros((len(gates),len(h1names)),np.float32)
    b2 = np.zeros(len(gates),np.float32)
    W3 = np.zeros((len(OUTPUTS),len(gates)),np.float32)
    b3 = np.full(len(OUTPUTS), -10.,np.float32)
    for i,(names,output,strength) in enumerate(gates):
        for name in names: W2[i,H1[name]] += 1
        b2[i] = -(len(names)-1)
        W3[OUTPUTS.index(output),i] = strength
    return vocab, embedding, W1,b1,W2,b2,W3,b3

def encode(text,vocab,embedding):
    text = normalize(text)
    pooled = np.zeros(len(FEATURES),np.float32)
    offset = 0
    if not text or len(text)>192: pooled[F['unknown']] = 1
    while offset < len(text):
        if text[offset].isspace(): offset += 1; continue
        for i,token in enumerate(vocab):
            if text.startswith(token,offset):
                end = offset+len(token)
                if token[-1].isascii() and token[-1].isalpha() and end<len(text) and text[end].isascii() and text[end].isalpha():
                    continue
                pooled = np.maximum(pooled,embedding[i]); offset=end; break
        else:
            pooled[F['unknown']]=1; offset+=1
    return pooled

def decode(text, model, trace=False):
    vocab,emb,w1,b1,w2,b2,w3,b3=model
    features=encode(text,vocab,emb)
    output=[]; traces=[]
    for stage in range(5):
        x=np.zeros(len(INPUTS),np.float32);x[:len(FEATURES)]=features;x[len(FEATURES)+stage]=1
        h1=np.maximum(0,w1@x+b1);h2=np.maximum(0,w2@h1+b2)
        logits=w3@h2+b3
        token=OUTPUTS[int(np.argmax(logits))];output.append(token)
        traces.append(logits.tolist())
        if token in ['reject','eos']: break
    return (output,traces) if trace else output

def write_binary(path, model):
    vocab,emb,w1,b1,w2,b2,w3,b3=model
    path.parent.mkdir(parents=True,exist_ok=True)
    with path.open('wb') as f:
        f.write(b'MWSP');f.write(struct.pack('<7i',1,len(vocab),len(FEATURES),len(INPUTS),len(b1),len(b2),len(OUTPUTS)))
        for text in vocab+OUTPUTS:
            b=text.encode('utf-8');f.write(struct.pack('<i',len(b)));f.write(b)
        for tensor in [emb,w1,b1,w2,b2,w3,b3]: f.write(tensor.astype('<f4').tobytes())

def main():
    model=construct()
    dest=ROOT/'UnityProject/Assets/Resources/ManaWeave/wsp_model.bytes'
    write_binary(dest,model)
    train,heldout=data.build_corpus()
    # Evaluation only: no numbers above are fitted to these examples.
    allrows=heldout+[
      {'text':'照準の先を広範囲に加熱','answer':data.ir('heat',3)},
      {'text':'この地点を小さく冷却','answer':data.ir('cool',1)},
      {'text':'加熱しない','answer':data.ir('reject')},
      {'text':'熱して冷やして','answer':data.ir('reject')},
      {'text':'小さく大きく熱して','answer':data.ir('reject')},
      {'text':'未知の対象だけを熱して','answer':data.ir('reject')},
      {'text':'半径1000mを熱して','answer':data.ir('reject')},
      {'text':'熱して、ただし容器は壊すな','answer':data.ir('reject')},
      {'text':'please do not heat the target','answer':data.ir('reject')},
      {'text':'heat the target unless frozen','answer':data.ir('reject')},
    ]
    failures=[]
    for row in allrows:
        want=[t.replace('@','').replace('<','').replace('>','') for t in row['answer']]
        got=decode(row['text'],model)
        if want[:1]==['reject']: want=['reject']
        if got!=want:failures.append({'text':row['text'],'expected':want,'actual':got})
    count=sum(t.size for t in model[1:])
    report={'method':'direct numeric construction; no training/optimization','parameters':int(count),'file_bytes':dest.stat().st_size,
       'examples':len(allrows),'exact_matches':len(allrows)-len(failures),'accuracy':(len(allrows)-len(failures))/len(allrows),
       'evaluation_scope':'Author-defined bounded lexicon and held-out combinations; not general Japanese understanding.',
       'failures':failures[:30]}
    (Path(__file__).parent/'evaluation.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    tests=[]
    for s in ['目の前を小さく熱して','広範囲に冷やして','石で建てて','魔力を充填して','錬成して','熱して冷やして','未知の命令']:
        seq,logits=decode(s,model,True);tests.append({'text':s,'tokens':seq,'logits':logits})
    (Path(__file__).parent/'parity_fixture.json').write_text(json.dumps({'output_vocabulary':OUTPUTS,'cases':tests},ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(report,ensure_ascii=False))

if __name__=='__main__': main()
