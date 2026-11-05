# RuneLM binary format v1

All numbers are little-endian; tensors are float32, row-major. UTF-8 strings use an explicit signed int32 byte length, not .NET's BinaryWriter string encoding.

| Field | Type |
|---|---|
| Magic | 8 ASCII bytes `MWLM0001` |
| Version | int32 = 1 |
| Model dimension D | int32 |
| Attention heads H | int32 |
| Layer count L | int32 |
| Context length C | int32 |
| Vocabulary count V | int32 |
| Parameter count | int32 |
| V vocabulary entries | int32 byte length, then UTF-8 bytes |
| Token embedding | V × D float32 |
| Learned position embedding | C × D float32 |

For each layer in order:

1. LayerNorm1 scale D, bias D.
2. QKV weight 3D × D (concatenated Q, K, V).
3. Attention output weight D × D.
4. LayerNorm2 scale D, bias D.
5. Feed-forward up weight 4D × D.
6. Feed-forward down weight D × 4D.

Finally LayerNorm scale D, bias D. The output projection is tied to the token embedding; it is not stored a second time. No trailing data is permitted.

The forward pass is pre-normalized. LayerNorm population variance uses epsilon 1e-5. Attention has a strict causal mask, head scale 1/sqrt(D/H), and softmax. MLP activation is ReLU. Linear layers have no biases. Residual branches are additive. Decoder is greedy within the permitted IR grammar, and it does not modify model weights at runtime.

The number of trainable parameters is:

`V*D + C*D + L*(12*D*D + 4*D) + 2*D`

For the included pilot V=171,D=96,C=96,L=2: 247,776 parameters.
