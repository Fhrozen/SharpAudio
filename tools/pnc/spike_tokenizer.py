"""Unigram SentencePiece tokenizer spike for 1-800-BAD-CODE/punctuation_fullstop_truecase_english.

Reads `spe_32k_lc_en.model` (SentencePiece protobuf), reimplements the segmentation the C#
tokenizer will use, and asserts byte-for-byte parity with the Python `sentencepiece` reference.
Facts recorded here are what `UnigramSentencePieceTokenizer` in C# must reproduce.
"""

import struct
import sys

import sentencepiece as sp

MODEL = sys.argv[1] if len(sys.argv) > 1 else "/tmp/spe_32k_lc_en.model"

UNKNOWN = 2
BYTE = 3
NORMAL = 4


def read_fields(buf):
    """Minimal protobuf reader: yields (field_number, wire_type, value)."""
    i = 0
    while i < len(buf):
        key, i = read_varint(buf, i)
        wire = key & 7
        if wire == 0:
            value, i = read_varint(buf, i)
        elif wire == 2:
            length, i = read_varint(buf, i)
            value = buf[i:i + length]
            i += length
        elif wire == 5:
            value = struct.unpack("<f", buf[i:i + 4])[0]
            i += 4
        else:
            raise ValueError(f"unsupported wire type {wire}")
        yield key >> 3, wire, value


def read_varint(buf, i):
    value = 0
    shift = 0
    while True:
        byte = buf[i]
        i += 1
        value |= (byte & 0x7F) << shift
        shift += 7
        if not byte & 0x80:
            return value, i


def read_pieces(buf):
    pieces = []
    for field, wire, value in read_fields(buf):
        if field != 1 or wire != 2:
            continue
        entry = {f: v for f, w, v in read_fields(value)}
        pieces.append((entry.get(1, b"").decode("utf-8"), entry.get(2, 0.0), entry.get(3, NORMAL)))
    return pieces


PIECES = read_pieces(open(MODEL, "rb").read())
UNK_ID = next(i for i, p in enumerate(PIECES) if p[2] == UNKNOWN)
BOS_ID = next(i for i, p in enumerate(PIECES) if p[0] == "<s>")
EOS_ID = next(i for i, p in enumerate(PIECES) if p[0] == "</s>")
WORD_START = "\u2581"


def normalize(text):
    """No NFKC, no case folding: only runs of ASCII spaces collapse and the ends are trimmed."""
    out = []
    prev_space = False
    for ch in text:
        if ch == " ":
            if not prev_space:
                out.append(ch)
            prev_space = True
        else:
            out.append(ch)
            prev_space = False
    return "".join(out).strip(" ")


def encode(text):
    ids = []
    for word in normalize(text).split(" "):
        if not word:
            continue
        word = WORD_START + word
        n = len(word)
        best = [0.0] * (n + 1)
        choice = [None] * (n + 1)
        for i in range(n - 1, -1, -1):
            best_score = None
            best_piece = None
            for idx, (piece, score, kind) in enumerate(PIECES):
                # Special pieces never match input text, and byte pieces are only used when byte
                # fallback is enabled. This vocab has no byte pieces, so byte fallback is covered by
                # the C# unit tests with a synthetic fixture rather than by parity here.
                if kind == BYTE or not piece or not word.startswith(piece, i):
                    continue
                total = score + best[i + len(piece)]
                if best_score is None or total > best_score:
                    best_score = total
                    best_piece = idx
            if best_piece is None:
                # <unk> scores 0 and consumes one character, so the rest of the word still counts
                best[i] = best[i + 1]
                choice[i] = UNK_ID
                continue
            best[i] = best_score
            choice[i] = best_piece
        i = 0
        while i < n:
            picked = choice[i]
            ids.append(picked)
            if PIECES[picked][2] == UNKNOWN:
                # A run of characters with no matching piece becomes one <unk>, not one per char.
                i += 1
                while i < n and choice[i] == UNK_ID:
                    i += 1
            else:
                i += len(PIECES[picked][0])
    return ids


CORPUS = [
    "hello world",
    "it's 5 a.m. and i'm tired",
    "the quick brown fox jumps over the lazy dog",
    "when i got home i trained this model on the lambda cloud on an a100 gpu",
    "george hw bush was the president of the us for 8 years",
    "i saw mr smith at the store he was shopping for a new lawn mower",
    "café naïve",
    "Hello World",
    "a  b c",
    "U.S. and NATO and McDonald's",
    "3.5 miles",
    "well, so, um, i think so",
    "it's that man again itma was a radio comedy programme",
    "Despite being mid March, it snowed.",
    "the cat sat on the mat. then the dog ran.",
    "i don't know what to say about that",
    "she said hello there friend",
    "one two three four five six seven eight nine ten",
    "weather is nice today and we are going outside",
    "they're here, they're there, they're everywhere",
    "hello\tworld",
    "hello\nworld",
    "  leading and trailing  ",
    "a—b",
    "the £5 note, the ½ cup, the ﬁne ﬂow",
    "emoji 😀 test",
    "no spaceshere",
    "mixed CASE Words here",
    # unknown character followed by pieces: the unk score must not reset the rest
    "xabc",
    "qbe",
    "éabc",
    "abcé",
    "zz the quick brown fox",
    "Héllo, wörld.",
]

reference = sp.SentencePieceProcessor()
reference.load(MODEL)

mismatches = 0
for text in CORPUS:
    mine = encode(text)
    theirs = reference.encode(text)
    if mine != theirs:
        mismatches += 1
        print(f"MISMATCH {text!r}\n  spike {mine}\n  ref   {theirs}")

print(f"vocab {len(PIECES)} unk {UNK_ID} bos {BOS_ID} eos {EOS_ID}")
print(f"parity: {len(CORPUS) - mismatches}/{len(CORPUS)}")
sys.exit(1 if mismatches else 0)
