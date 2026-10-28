namespace ApexHighlights;

/// <summary>BGRA8 → NV12，BT.709 有限范围（16..235 / 16..240），整数运算 + 指针，无边界检查。</summary>
public static unsafe class Nv12
{
    /// <summary>把 w*h 的 BGRA（行步长 stride）转成 NV12 写入 dst（长度必须 w*h*3/2）。</summary>
    public static void FromBgra(ReadOnlySpan<byte> bgra, int stride, int w, int h, Span<byte> dst)
    {
        fixed (byte* ps = bgra)
        fixed (byte* pd = dst)
        {
            byte* src = ps;
            byte* yPlane = pd;
            byte* uvPlane = pd + (long)w * h;

            for (int y = 0; y < h; y++)
            {
                byte* s = src + (long)y * stride;
                byte* d = yPlane + (long)y * w;
                int x = 0;
                for (; x + 4 <= w; x += 4)
                {
                    byte* p = s + x * 4;
                    d[x + 0] = Y(p[0], p[1], p[2]);
                    d[x + 1] = Y(p[4], p[5], p[6]);
                    d[x + 2] = Y(p[8], p[9], p[10]);
                    d[x + 3] = Y(p[12], p[13], p[14]);
                }
                for (; x < w; x++)
                {
                    byte* p = s + x * 4;
                    d[x] = Y(p[0], p[1], p[2]);
                }
            }

            int cw = w / 2;
            for (int cy = 0; cy < h / 2; cy++)
            {
                byte* r0 = src + (long)(cy * 2) * stride;
                byte* r1 = src + (long)(cy * 2 + 1) * stride;
                byte* o = uvPlane + (long)cy * cw * 2;
                for (int cx = 0; cx < cw; cx++)
                {
                    int o8 = cx * 8;
                    int b = (r0[o8] + r0[o8 + 4] + r1[o8] + r1[o8 + 4] + 2) >> 2;
                    int g = (r0[o8 + 1] + r0[o8 + 5] + r1[o8 + 1] + r1[o8 + 5] + 2) >> 2;
                    int r = (r0[o8 + 2] + r0[o8 + 6] + r1[o8 + 2] + r1[o8 + 6] + 2) >> 2;
                    int u = ((-26 * r - 86 * g + 112 * b + 128) >> 8) + 128;
                    int v = ((112 * r - 102 * g - 10 * b + 128) >> 8) + 128;
                    o[cx * 2] = (byte)(u < 0 ? 0 : u > 255 ? 255 : u);
                    o[cx * 2 + 1] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                }
            }
        }
    }

    /// <summary>Y' = 0.2126R + 0.7152G + 0.0722B，压到 16..235。</summary>
    private static byte Y(int b, int g, int r)
    {
        int y = ((47 * r + 157 * g + 16 * b + 128) >> 8) + 16;
        return (byte)(y < 16 ? 16 : y > 235 ? 235 : y);
    }
}
