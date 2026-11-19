#!/usr/bin/env python3
"""
Convert a SHARP (apple/ml-sharp) 3D Gaussian .ply into a compact .bytes file for the
PhotoSplat Unity demo, and (optionally) copy the source photo next to it.

    python sharp_to_unity.py scene.ply -o Assets/PhotoSplat/Data/scene.bytes \
        --image photo.jpg --max-splats 150000

Only numpy is required (Pillow only if --image is given).

What it does
  * reads any binary_little_endian PLY with a standard 3DGS "vertex" element
    (x y z, f_dc_0..2, opacity, scale_0..2, rot_0..3); extra elements are read and,
    when SHARP's camera elements are present (intrinsic, image_size), used to find
    the exact pixel each splat came from
  * sigmoid(opacity), exp(scale), SH DC -> RGB
  * drops nearly transparent splats and the farthest few percent (sky / background)
  * randomly downsamples to --max-splats (Quest 3 is comfortable around 100k-200k)
    and grows the kept splats a little so the surface stays closed
  * converts OpenCV camera coords (x right, y down, z forward) to Unity (x right, y up,
    z forward) and stores each splat's 3D covariance (6 floats)

Output layout (little endian):
  header 32 bytes: char[4] "PSPL", int32 version=1, int32 count, float imageAspect (w/h),
                   float medianDepth, float maxDepth, 2 x float reserved
  per splat 48 bytes: float3 pos, float6 cov (xx xy xz yy yz zz), float2 uv (0..1, v up),
                      uint8 rgba
"""
import argparse
import struct
import sys
from pathlib import Path

import numpy as np

PLY_TYPES = {
    "char": "i1", "int8": "i1", "uchar": "u1", "uint8": "u1",
    "short": "i2", "int16": "i2", "ushort": "u2", "uint16": "u2",
    "int": "i4", "int32": "i4", "uint": "u4", "uint32": "u4",
    "float": "f4", "float32": "f4", "double": "f8", "float64": "f8",
}
SH_C0 = 0.28209479177387814


def read_ply(path):
    data = Path(path).read_bytes()
    end = data.find(b"end_header")
    if end < 0:
        sys.exit("not a PLY file (no end_header)")
    header_end = data.find(b"\n", end) + 1
    lines = data[:header_end].decode("ascii", "replace").splitlines()
    if lines[0].strip() != "ply":
        sys.exit("not a PLY file")
    elements = []  # [name, count, [(prop, dtype)]]
    fmt = None
    for ln in lines[1:]:
        tok = ln.split()
        if not tok:
            continue
        if tok[0] == "format":
            fmt = tok[1]
        elif tok[0] == "element":
            elements.append([tok[1], int(tok[2]), []])
        elif tok[0] == "property":
            if tok[1] == "list":
                sys.exit(f"list properties are not supported (element {elements[-1][0]})")
            elements[-1][2].append((tok[2], PLY_TYPES[tok[1]]))
    if fmt != "binary_little_endian":
        sys.exit(f"only binary_little_endian PLY is supported, got {fmt}")

    out, off = {}, header_end
    for name, count, props in elements:
        dt = np.dtype([(p, "<" + t) for p, t in props])
        arr = np.frombuffer(data, dtype=dt, count=count, offset=off)
        off += dt.itemsize * count
        out[name] = arr
    return out


def scalar_values(arr):
    """Flatten a small extra element (e.g. intrinsic: 9 rows x 1 prop) into a float list."""
    return np.array([float(v) for row in arr for v in row], dtype=np.float64)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("ply")
    ap.add_argument("-o", "--out", required=True, help="output .bytes path")
    ap.add_argument("--image", help="source photo; saved next to the output as <name>.jpg (max 2048 px)")
    ap.add_argument("--max-splats", type=int, default=150000)
    ap.add_argument("--min-opacity", type=float, default=0.05)
    ap.add_argument("--depth-percentile", type=float, default=97.0,
                    help="drop splats farther than this depth percentile (sky, far background)")
    ap.add_argument("--no-compensate", action="store_true",
                    help="don't enlarge splats after downsampling")
    ap.add_argument("--linear-to-srgb", action="store_true",
                    help="use if colors look too dark/washed out: treats SHARP colors as linear")
    ap.add_argument("--seed", type=int, default=0)
    a = ap.parse_args()

    ply = read_ply(a.ply)
    if "vertex" not in ply:
        sys.exit("no 'vertex' element in PLY")
    v = ply["vertex"]
    names = v.dtype.names
    need = ["x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity",
            "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3"]
    missing = [n for n in need if n not in names]
    if missing:
        sys.exit(f"vertex element is missing {missing}; has {names}")
    extra = [k for k in ply if k != "vertex"]
    print(f"read {len(v):,} gaussians; extra elements: {extra or 'none'}")

    pos = np.stack([v["x"], v["y"], v["z"]], 1).astype(np.float64)
    col = SH_C0 * np.stack([v["f_dc_0"], v["f_dc_1"], v["f_dc_2"]], 1) + 0.5
    if a.linear_to_srgb:
        c = np.clip(col, 0, 1)
        col = np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1 / 2.4) - 0.055)
    opa = 1.0 / (1.0 + np.exp(-v["opacity"].astype(np.float64)))
    scl = np.exp(np.stack([v["scale_0"], v["scale_1"], v["scale_2"]], 1).astype(np.float64))
    q = np.stack([v["rot_0"], v["rot_1"], v["rot_2"], v["rot_3"]], 1).astype(np.float64)  # w x y z
    q /= np.linalg.norm(q, axis=1, keepdims=True) + 1e-12

    # ---- which pixel each splat came from -------------------------------------------------
    z = pos[:, 2]
    keep = (z > 1e-4) & (opa >= a.min_opacity)
    img_w = img_h = None
    if "intrinsic" in ply and "image_size" in ply:
        K = scalar_values(ply["intrinsic"])
        size = scalar_values(ply["image_size"])
        if K.size >= 9 and size.size >= 2:
            fx, cx, fy, cy = K[0], K[2], K[4], K[5]
            img_w, img_h = size[0], size[1]
            u = (fx * pos[:, 0] / np.maximum(z, 1e-6) + cx) / img_w
            vv = (fy * pos[:, 1] / np.maximum(z, 1e-6) + cy) / img_h
            print(f"camera from PLY: fx={fx:.1f} fy={fy:.1f} cx={cx:.1f} cy={cy:.1f} image={img_w:.0f}x{img_h:.0f}")
    if img_w is None:
        # no intrinsics: assume a pinhole at the origin and fit the projected extent to [0,1]
        px, py = pos[:, 0] / np.maximum(z, 1e-6), pos[:, 1] / np.maximum(z, 1e-6)
        lo_x, hi_x = np.percentile(px[keep], [0.5, 99.5])
        lo_y, hi_y = np.percentile(py[keep], [0.5, 99.5])
        u, vv = (px - lo_x) / (hi_x - lo_x), (py - lo_y) / (hi_y - lo_y)
        img_w, img_h = hi_x - lo_x, hi_y - lo_y
        print("no intrinsics in PLY: image plane fitted from the splats")
    for k in ("color_space", "version"):
        if k in ply:
            print(f"  {k}: {scalar_values(ply[k]).tolist()}")
    keep &= (u > -0.02) & (u < 1.02) & (vv > -0.02) & (vv < 1.02)

    max_depth = np.percentile(z[keep], a.depth_percentile)
    keep &= z <= max_depth
    idx = np.nonzero(keep)[0]
    print(f"after opacity/depth filter: {len(idx):,} (depth <= {max_depth:.2f})")

    rng = np.random.default_rng(a.seed)
    grow = 1.0
    if len(idx) > a.max_splats:
        w = opa[idx] / opa[idx].sum()
        idx = np.sort(rng.choice(idx, size=a.max_splats, replace=False, p=w))
        if not a.no_compensate:
            grow = min(np.sqrt(len(np.nonzero(keep)[0]) / a.max_splats), 2.5)
        print(f"downsampled to {len(idx):,}, splat size x{grow:.2f}")
    n = len(idx)

    # ---- covariance R S S^T R^T, then OpenCV -> Unity (flip y) ---------------------------
    w_, x_, y_, z_ = q[idx].T
    R = np.empty((n, 3, 3))
    R[:, 0, 0] = 1 - 2 * (y_ * y_ + z_ * z_); R[:, 0, 1] = 2 * (x_ * y_ - w_ * z_); R[:, 0, 2] = 2 * (x_ * z_ + w_ * y_)
    R[:, 1, 0] = 2 * (x_ * y_ + w_ * z_); R[:, 1, 1] = 1 - 2 * (x_ * x_ + z_ * z_); R[:, 1, 2] = 2 * (y_ * z_ - w_ * x_)
    R[:, 2, 0] = 2 * (x_ * z_ - w_ * y_); R[:, 2, 1] = 2 * (y_ * z_ + w_ * x_); R[:, 2, 2] = 1 - 2 * (x_ * x_ + y_ * y_)
    M = R * (scl[idx] * grow)[:, None, :]
    C = M @ np.transpose(M, (0, 2, 1))
    cov = np.stack([C[:, 0, 0], -C[:, 0, 1], C[:, 0, 2], C[:, 1, 1], -C[:, 1, 2], C[:, 2, 2]], 1)

    p = pos[idx].copy()
    p[:, 1] *= -1
    uv = np.stack([u[idx], 1.0 - vv[idx]], 1)  # v up, like a Unity texture
    rgba = np.concatenate([np.clip(col[idx], 0, 1), opa[idx, None]], 1)
    rgba8 = np.round(rgba * 255).astype(np.uint8)

    rec = np.zeros(n, dtype=np.dtype([("pos", "<f4", 3), ("cov", "<f4", 6), ("uv", "<f4", 2), ("rgba", "u1", 4)]))
    rec["pos"], rec["cov"], rec["uv"], rec["rgba"] = p, cov, uv, rgba8
    assert rec.dtype.itemsize == 48

    aspect = float(img_w / img_h)
    median_depth = float(np.median(z[idx]))
    header = b"PSPL" + struct.pack("<ii5f", 1, n, aspect, median_depth, float(max_depth), 0.0, 0.0)
    out = Path(a.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(header + rec.tobytes())
    print(f"wrote {out} ({out.stat().st_size / 1e6:.1f} MB): {n:,} splats, aspect {aspect:.3f}, "
          f"median depth {median_depth:.2f}")

    if a.image:
        from PIL import Image
        im = Image.open(a.image).convert("RGB")
        im.thumbnail((2048, 2048))
        img_out = out.with_suffix(".jpg")
        im.save(img_out, quality=92)
        print(f"wrote {img_out} ({im.width}x{im.height})")


if __name__ == "__main__":
    main()
