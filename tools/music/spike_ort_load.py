"""Step 17 spike: load each Stable Audio ONNX graph in ORT and dump IO names, shapes and dtypes."""

import argparse
import collections
import pathlib
import sys

import numpy as np
import onnx
import onnxruntime as ort

BUNDLE = pathlib.Path(__file__).resolve().parents[2] / "out_refs" / "stable-audio-3-small-music-onnx"
GRAPHS = ["text_encoder_q4", "dit_q4", "decoder_q4", "number_conditioner"]
T_LAT = 16  # concrete value for the dynamic latent dim

DUMMY = {
    "text_encoder_q4": {"input_ids": np.zeros((1, 256), np.int64), "attention_mask": np.ones((1, 256), np.int64)},
    "number_conditioner": {"seconds": np.array([10.0], np.float32)},
    "dit_q4": {
        "x": np.zeros((1, 256, T_LAT), np.float32),
        "t": np.array([0.5], np.float32),
        "cross_attn_cond": np.zeros((1, 257, 768), np.float32),
        "global_embed": np.zeros((1, 768), np.float32),
        "local_add_cond": np.zeros((1, 257, T_LAT), np.float32),
        "padding_mask": np.ones((1, T_LAT), np.bool_),
    },
    "decoder_q4": {"latents": np.zeros((1, 256, T_LAT), np.float32)},
}


def describe(session):
    for io in list(session.get_inputs()) + list(session.get_outputs()):
        print(f"  {io.name}: shape={io.shape} dtype={io.type}")


def graph_info(model_path):
    counts = collections.Counter()
    external = collections.Counter()
    model = onnx.load(str(model_path), load_external_data=False)
    graph = model.graph
    for node in graph.node:
        counts[node.op_type] += 1
    for init in graph.initializer:
        for entry in init.external_data:
            if entry.key == "location":
                external[entry.value] += 1
    return counts, external, model


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle", type=pathlib.Path, default=BUNDLE)
    parser.add_argument("--run", action="store_true", help="also run a dummy forward to prove the ops execute")
    args = parser.parse_args()

    print(f"onnxruntime {ort.__version__} · python {sys.version.split()[0]}")
    print(f"available providers: {ort.get_available_providers()}")

    for name in GRAPHS:
        model_path = args.bundle / "onnx" / f"{name}.onnx"
        print(f"\n=== {name} ===")
        counts, external, model = graph_info(model_path)
        print(f"  opset {model.opset_import[0].version}, nodes {len(model.graph.node)}, quantized ops: "
              + ", ".join(f"{op} x{counts[op]}" for op in ("MatMulNBits", "GatherBlockQuantized") if counts[op]))
        print(f"  external data: {dict(external) or 'none'}")
        try:
            session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
        except Exception as exc:
            print(f"  LOAD FAILED: {exc}")
            continue
        describe(session)
        if args.run:
            outputs = session.get_outputs()
            try:
                results = session.run([o.name for o in outputs], DUMMY[name])
            except Exception as exc:
                print(f"  RUN FAILED: {exc}")
                continue
            for io, value in zip(outputs, results):
                print(f"  ran {io.name} -> {value.shape}")


if __name__ == "__main__":
    main()
