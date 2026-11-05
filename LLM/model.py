"""ManaWeave RuneLM: original compact causal Transformer, trained from scratch.

No downloaded/pretrained parameters. PyTorch supplies tensor/autograd primitives.
The same forward equations are implemented in pure C# for the Unity player.
"""
from dataclasses import dataclass, asdict
import math
import torch
from torch import nn
from torch.nn import functional as F

@dataclass
class Config:
    vocab_size: int = 256
    dim: int = 96
    heads: int = 4
    layers: int = 2
    block_size: int = 96

class Attention(nn.Module):
    def __init__(self, cfg):
        super().__init__()
        self.heads = cfg.heads
        self.qkv = nn.Linear(cfg.dim, 3 * cfg.dim, bias=False)
        self.proj = nn.Linear(cfg.dim, cfg.dim, bias=False)

    def forward(self, x):
        b, t, d = x.shape
        q, k, v = self.qkv(x).split(d, dim=-1)
        q, k, v = [z.view(b, t, self.heads, d // self.heads).transpose(1, 2)
                   for z in (q, k, v)]
        y = F.scaled_dot_product_attention(q, k, v, is_causal=True)
        return self.proj(y.transpose(1, 2).contiguous().view(b, t, d))

class Block(nn.Module):
    def __init__(self, cfg):
        super().__init__()
        self.ln1 = nn.LayerNorm(cfg.dim, eps=1e-5)
        self.attn = Attention(cfg)
        self.ln2 = nn.LayerNorm(cfg.dim, eps=1e-5)
        self.up = nn.Linear(cfg.dim, 4 * cfg.dim, bias=False)
        self.down = nn.Linear(4 * cfg.dim, cfg.dim, bias=False)

    def forward(self, x):
        x = x + self.attn(self.ln1(x))
        return x + self.down(F.relu(self.up(self.ln2(x))))

class RuneLM(nn.Module):
    def __init__(self, cfg):
        super().__init__()
        self.cfg = cfg
        self.token = nn.Embedding(cfg.vocab_size, cfg.dim)
        self.position = nn.Embedding(cfg.block_size, cfg.dim)
        self.blocks = nn.ModuleList([Block(cfg) for _ in range(cfg.layers)])
        self.final_norm = nn.LayerNorm(cfg.dim, eps=1e-5)
        self.apply(self._init)

    @staticmethod
    def _init(m):
        if isinstance(m, (nn.Linear, nn.Embedding)):
            nn.init.normal_(m.weight, mean=0., std=.02)

    def forward(self, tokens):
        t = tokens.shape[1]
        if t > self.cfg.block_size:
            raise ValueError('input exceeds model context')
        x = self.token(tokens) + self.position(torch.arange(t, device=tokens.device))
        for block in self.blocks:
            x = block(x)
        return F.linear(self.final_norm(x), self.token.weight)

    def parameter_count(self):
        return sum(p.numel() for p in self.parameters())

    def export_tensors(self):
        yield self.token.weight
        yield self.position.weight
        for b in self.blocks:
            yield b.ln1.weight
            yield b.ln1.bias
            yield b.attn.qkv.weight
            yield b.attn.proj.weight
            yield b.ln2.weight
            yield b.ln2.bias
            yield b.up.weight
            yield b.down.weight
        yield self.final_norm.weight
        yield self.final_norm.bias
