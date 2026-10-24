"""Step 6 spike: confirm what the four PnC outputs mean by running one sentence
through ORT and applying the labels the way `punctuators` does, then comparing with
the reference pipeline.

Run: uv run --no-project --with onnxruntime==1.20.1 --with sentencepiece python tools/pnc/spike_forward.py
"""

import pathlib
import sys

import numpy as np
import onnxruntime as ort
from sentencepiece import SentencePieceProcessor

BUNDLE = pathlib.Path(__file__).resolve().parents[2] / "out_refs" / "punctuation_fullstop_truecase_english"
MODEL = BUNDLE / "punct_cap_seg_en.onnx"
SPE = BUNDLE / "spe_32k_lc_en.model"

PRE_LABELS = ["<NULL>", "\u00bf"]
POST_LABELS = ["<NULL>", "<ACRONYM>", ".", ",", "?"]
MAX_LENGTH = 256

TEXT = "hello world the quick brown fox jumped over the lazy dog"


def main():
    if not MODEL.exists() or not SPE.exists():
        print(f"missing files in {BUNDLE}", file=sys.stderr)
        return 1

    sp = SentencePieceProcessor(str(SPE))
    ids = sp.EncodeAsIds(TEXT)
    window = [sp.bos_id()] + ids[: MAX_LENGTH - 2] + [sp.eos_id()]
    batch = np.full((1, len(window)), sp.pad_id(), dtype=np.int64)
    batch[0, : len(window)] = window

    session = ort.InferenceSession(str(MODEL), providers=["CPUExecutionProvider"])
    pre, post, cap, seg = session.run(None, {"input_ids": batch})

    inner = range(1, len(window) - 1)
    print(f"tokens ({len(ids)}): {ids[:12]} ...")
    for i in inner:
        token = sp.IdToPiece(window[i])
        print(f"  {token!r}: pre={PRE_LABELS[pre[0][i]]} post={POST_LABELS[post[0][i]]} "
              f"cap={cap[0][i].tolist()} sbd={bool(seg[0][i])}")

    print("reconstructed:", apply_labels(sp, window, pre, post, cap, seg))
    return 0


def apply_labels(sp, window, pre, post, cap, seg):
    """Mirrors PunctCapSegResultCollector.produce() for a single window."""
    chars = []
    for i in range(1, len(window) - 1):
        token = sp.IdToPiece(window[i])
        if token.startswith("\u2581") and chars:
            chars.append(" ")
        start = 1 if token.startswith("\u2581") else 0
        for offset, char in enumerate(token[start:], start):
            if offset == start and PRE_LABELS[pre[0][i]] != "<NULL>":
                chars.append(PRE_LABELS[pre[0][i]])
            if cap[0][i][offset]:
                char = char.upper()
            chars.append(char)
            label = POST_LABELS[post[0][i]]
            if label == "<ACRONYM>":
                chars.append(".")
            elif offset == len(token) - 1 and label != "<NULL>":
                chars.append(label)
    return "".join(chars)


if __name__ == "__main__":
    sys.exit(main())
