"""Original procedurally authored command-to-IR corpus; no external text corpus.

Splits are by surface templates. Vocabulary includes held-out characters (a
fixed alphabet), but held-out prompt/answer examples are not used for training.
The grammar intentionally represents one aimed-area action, with preset strength.
"""
import itertools
import json
import re
import unicodedata
from pathlib import Path

SPECIAL = ['<pad>', '<bos>', '<sep>', '<eos>', '<unk>']
OPS = ['heat', 'cool', 'excavate', 'build', 'charge', 'transmute', 'reject']
OUT = ['@' + x for x in OPS] + ['@r1', '@r2', '@r3'] + [
    '@p0', '@p20', '@p50', '@p80', '@p50000', '@p120000', '@p300000'] + [
    '@stone', '@wood', '@iron', '@crystal']

# A size means both area and strength for the limited natural-language prototype.
JP_SIZE = {1: ['小さく', '小規模に', '狭い範囲で'], 2: ['', '通常の強さで', '標準規模で'],
           3: ['広範囲に', '大規模に', '大きく']}
EN_SIZE = {1: ['gently', 'in a small area', 'with low power'],
           2: ['', 'with normal power', 'in a medium area'],
           3: ['strongly', 'in a large area', 'with high power']}
JP_TARGET = ['', '目の前を', '対象を', 'この場所を', '狙った場所を', 'そこを']
EN_TARGET = ['the target', 'this area', 'the aimed spot', 'this location']

JP_ACTIONS = {
 'heat': ['熱して', '加熱して', '温めて', '熱を加えて'],
 'cool': ['冷やして', '冷却して', '熱を取り除いて', '温度を下げて'],
 'excavate': ['掘って', '掘削して', 'くり抜いて', '削って'],
 'charge': ['魔力を充填して', '魔力を蓄えて', '魔力を注いで', '魔力を補給して'],
 'transmute': ['錬成して', '錬金術で変えて', '合金にして', '錬金変換して']}
EN_ACTIONS = {
 'heat': ['heat', 'warm', 'apply heat to', 'raise the temperature of'],
 'cool': ['cool', 'chill', 'remove heat from', 'lower the temperature of'],
 'excavate': ['excavate', 'dig', 'hollow out', 'carve out'],
 'charge': ['charge mana into', 'store mana in', 'infuse mana into', 'recharge'],
 'transmute': ['transmute', 'alchemize', 'turn into alloy', 'perform alchemy on']}
MATERIALS = {'stone': ['石', '石材'], 'wood': ['木', '木材'],
             'iron': ['鉄', '鉄材'], 'crystal': ['結晶', '魔導結晶']}
JP_BUILD = ['{mat}で建てて', '{mat}を置いて', '{mat}を作って', '{mat}で構築して']
EN_BUILD = ['build {mat} at', 'place {mat} at', 'construct {mat} at', 'create {mat} at']
# Deliberately held-out structures; phrases in these sentence orders never trained.
JP_FORMS_TRAIN = ['{target}{size}{act}', '{size}{target}{act}',
                  '{target}{size}{act}ください', '{size}{act}ください',
                  '{target}{size}{act}、お願いします', 'お願いします{size}{act}',
                  '{size}、{target}{act}']
JP_FORMS_TEST = ['お願いします、{target}{size}{act}', '{target}{act}、{size}']
EN_FORMS_TRAIN = ['{act} {target} {size}', 'please {act} {target} {size}',
                  '{size} {act} {target}', '{act} {target} {size} please',
                  'now {act} {target} {size}', 'please {act} {target} {size} now',
                  '{size}, {act} {target}']
EN_FORMS_TEST = ['please {size} {act} {target} now', '{act} {target}, {size}']

REJECT_TRAIN = [
 'こんにちは', 'ありがとう', 'おはよう', '天気を教えて', '名前は何', '時間を止めて',
 '空を飛びたい', '世界を消して', 'すべてを倒して', '未来を見せて', '敵を探して',
 'hello', 'thank you', 'what is your name', 'stop time', 'teleport me', 'find an enemy',
 'make everything disappear', 'create life', 'change gravity', 'tell a story']
REJECT_TEST = ['こんばんは', '世界の時間を止めて', 'この場所へ瞬間移動して',
               'good morning', 'please teleport me now', 'show me the future']

DEFAULT_EXAMPLES = ['目の前を小さく熱して', '広範囲に冷やして', '対象を掘削して',
                    '石で建てて', '魔力を充填して', '錬成して']


def normalize(text):
    text = unicodedata.normalize('NFKC', text).strip().lower()
    return re.sub(r'\s+', ' ', text).rstrip('。.!！')


def ir(op, size=2, material='stone'):
    if op == 'reject':
        return ['@reject', '<eos>']
    power = {1:50000, 2:120000, 3:300000}[size] if op in ('heat','cool') else (
            {1:20, 2:50, 3:80}[size] if op == 'charge' else 0)
    # Natural-language default for volume-editing operations is radius 1.
    radius = size if op in ('heat','cool') else max(1, size - 1)
    return ['@'+op, '@r'+str(radius), '@p'+str(power), '@'+material, '<eos>']


def build_corpus():
    train, test = {}, {}
    def add(dst, text, answer):
        text = normalize(text)
        if len(text) > 80: return
        if text in dst and dst[text] != answer:
            raise RuntimeError('ambiguous dataset: '+text)
        dst[text] = answer
    for op in list(JP_ACTIONS) + ['build']:
        mats = list(MATERIALS) if op == 'build' else ['stone']
        for size, material in itertools.product([1,2,3], mats):
            jacts = [a.format(mat=m) for a in JP_BUILD for m in MATERIALS[material]] if op == 'build' else JP_ACTIONS[op]
            eacts = [a.format(mat=material) for a in EN_BUILD] if op == 'build' else EN_ACTIONS[op]
            for sz, tgt, act, frm in itertools.product(JP_SIZE[size], JP_TARGET, jacts, JP_FORMS_TRAIN):
                add(train, frm.format(target=tgt,size=sz,act=act), ir(op,size,material))
            for sz, tgt, act, frm in itertools.product(EN_SIZE[size], EN_TARGET, eacts, EN_FORMS_TRAIN):
                add(train, frm.format(target=tgt,size=sz,act=act), ir(op,size,material))
            # Holdout lexical phrases are composed from known words, not memorized full sentences.
            for sz, tgt, act, frm in itertools.product(JP_SIZE[size], JP_TARGET[1:3], jacts[0:2], JP_FORMS_TEST):
                if not sz: continue
                add(test, frm.format(target=tgt,size=sz,act=act), ir(op,size,material))
            for sz, tgt, act, frm in itertools.product(EN_SIZE[size], EN_TARGET[:2], eacts[:2], EN_FORMS_TEST):
                if not sz: continue
                add(test, frm.format(target=tgt,size=sz,act=act), ir(op,size,material))
    for s in REJECT_TRAIN: add(train, s, ir('reject'))
    for s in REJECT_TEST: add(test, s, ir('reject'))
    if set(train) & set(test):
        raise RuntimeError('train/test leakage')
    return [{'text':k,'answer':v} for k,v in sorted(train.items())], \
           [{'text':k,'answer':v} for k,v in sorted(test.items())]


def vocabulary(train, test):
    alphabet = sorted(set(''.join(x['text'] for x in train+test)))
    return SPECIAL + alphabet + OUT


def allowed(vocab, history):
    index = {x:i for i,x in enumerate(vocab)}
    if not history: return [index['@'+o] for o in OPS]
    op = vocab[history[0]][1:]
    if op == 'reject': return [index['<eos>']]
    pos = len(history)
    if pos == 1: return [index['@r'+str(i)] for i in (1,2,3)]
    if pos == 2:
        vals = (50000,120000,300000) if op in ('heat','cool') else ((20,50,80) if op=='charge' else (0,))
        return [index['@p'+str(v)] for v in vals]
    if pos == 3: return [index['@'+m] for m in (MATERIALS if op=='build' else ('stone',))]
    return [index['<eos>']]


def write_dataset(path):
    train,test = build_corpus()
    path = Path(path); path.mkdir(parents=True, exist_ok=True)
    for name, rows in [('train',train),('heldout',test)]:
        (path/(name+'.jsonl')).write_text(''.join(json.dumps(r,ensure_ascii=False)+'\n' for r in rows),encoding='utf-8')
    vocab = vocabulary(train,test)
    (path/'vocab.json').write_text(json.dumps(vocab,ensure_ascii=False,indent=2),encoding='utf-8')
    return train,test,vocab

if __name__=='__main__':
    a,b,v = write_dataset(Path(__file__).parent/'data')
    print({'train':len(a),'heldout':len(b),'vocab':len(v)})
