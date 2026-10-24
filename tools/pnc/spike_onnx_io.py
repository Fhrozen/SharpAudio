"""Step 6 spike: load the Phase C PnC ONNX model in ORT and dump IO names, shapes
and dtypes for the four heads, then run one dummy forward to resolve dynamic dims.

Run: uv run --no-project --with onnxruntime==1.20.1 --with onnx==1.16.2 \
        --with protobuf==4.25.3 python tools/pnc/spike_onnx_io.py
"""

import pathlib
import sys

import numpy as np
import onnx
import onnxruntime as ort

BUNDLE = pathlib.Path(__file__).resolve().parents[2] / "out_refs" / "punctuation_fullstop_truecase_english"
MODEL = BUNDLE / "punct_cap_seg_en.onnx"
MAX_LENGTH = 256  # config.yaml max_length (512 positional embeddings, trained to 256)


def describe(session):
    for io in list(session.get_inputs()) + list(session.get_outputs()):
        print(f"  {io.name}: shape={io.shape} dtype={io.type}")


def main():
    if not MODEL.exists():
        print(f"missing model: {MODEL}", file=sys.stderr)
        return 1

    model = onnx.load(str(MODEL), load_external_data=False)
    opset = ", ".join(f"{d.domain or 'ai.onnx'}:{d.version}" for d in model.opset_import)
    print(f"opset: {opset}, nodes: {len(model.graph.node)}")
    describe_from_graph(model)

    session = ort.InferenceSession(str(MODEL), providers=["CPUExecutionProvider"])
    print("ORT session IO:")
    describe(session)

    inputs = {io.name: dummy(io) for io in session.get_inputs()}
    print("dummy input:", {k: (v.shape, v.dtype) for k, v in inputs.items()})
    outputs = session.run(None, inputs)
    print("dummy forward output shapes:")
    for io, value in zip(session.get_outputs(), outputs):
        print(f"  {io.name}: {tuple(value.shape)} {value.dtype}")
        if value.size <= 20:
            print(f"    values: {value.reshape(-1).tolist()}")
    return 0


def describe_from_graph(model):
    for kind, values in (("input", model.graph.input), ("output", model.graph.output)):
        print(f"{kind}s:")
        for io in values:
            dims = [d.dim_param or d.dim_value for d in io.type.tensor_type.shape.dim]
            print(f"  {io.name}: shape={dims} dtype={onnx.TensorProto.DataType.Name(io.type.tensor_type.elem_type)}")


def dummy(io):
    # Dynamic dims are (batch, sequence); resolve to the batch-1 window the engine uses.
    shape = [1 if i == 0 else MAX_LENGTH if isinstance(d, str) or d == 0 else d
            for i, d in enumerate(io.shape)]
    dtype = {"tensor(int64)": np.int64, "tensor(float)": np.float32, "tensor(bool)": np.bool_}[io.type]
    if io.name == "input_ids":
        return np.full(shape, 3, dtype)  # any normal piece id, avoids special ids
    return np.ones(shape, dtype)


if __name__ == "__main__":
    sys.exit(main())
