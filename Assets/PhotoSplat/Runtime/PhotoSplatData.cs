using System;
using UnityEngine;

namespace PhotoSplat
{
    /// <summary>A SHARP scene converted by tools/sharp_to_unity.py (a .bytes TextAsset).</summary>
    public sealed class PhotoSplatData
    {
        public int Count;
        public float ImageAspect;   // width / height of the source photo
        public float MedianDepth;   // SHARP units (roughly metres)
        public float MaxDepth;
        public Vector3[] Positions; // scene space, Unity axes (x right, y up, z away from the camera)
        public float[] Cov;         // 6 per splat: xx xy xz yy yz zz
        public Vector2[] UV;        // where the splat sits on the photo, 0..1, v up
        public Color32[] Colors;    // sRGB + opacity

        const int HeaderSize = 32, RecordSize = 48;

        public static PhotoSplatData Load(byte[] b)
        {
            if (b == null || b.Length < HeaderSize || b[0] != 'P' || b[1] != 'S' || b[2] != 'P' || b[3] != 'L')
                throw new FormatException("Not a PhotoSplat file (run tools/sharp_to_unity.py)");
            int version = BitConverter.ToInt32(b, 4);
            if (version != 1) throw new FormatException("Unsupported PhotoSplat version " + version);
            var d = new PhotoSplatData
            {
                Count = BitConverter.ToInt32(b, 8),
                ImageAspect = BitConverter.ToSingle(b, 12),
                MedianDepth = BitConverter.ToSingle(b, 16),
                MaxDepth = BitConverter.ToSingle(b, 20),
            };
            int n = d.Count;
            if (b.Length < HeaderSize + n * RecordSize) throw new FormatException("PhotoSplat file is truncated");
            d.Positions = new Vector3[n];
            d.Cov = new float[n * 6];
            d.UV = new Vector2[n];
            d.Colors = new Color32[n];
            int o = HeaderSize;
            for (int i = 0; i < n; i++, o += RecordSize)
            {
                d.Positions[i] = new Vector3(BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4), BitConverter.ToSingle(b, o + 8));
                for (int k = 0; k < 6; k++) d.Cov[i * 6 + k] = BitConverter.ToSingle(b, o + 12 + 4 * k);
                d.UV[i] = new Vector2(BitConverter.ToSingle(b, o + 36), BitConverter.ToSingle(b, o + 40));
                d.Colors[i] = new Color32(b[o + 44], b[o + 45], b[o + 46], b[o + 47]);
            }
            return d;
        }
    }
}
