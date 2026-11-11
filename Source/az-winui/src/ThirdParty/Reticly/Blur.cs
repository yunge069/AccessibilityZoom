// MIT licensed Reticly component. Copyright (c) 2026 Jonah.
// Source: https://github.com/corund207/Reticly/blob/ea9364b5ac090106db02cfdf6e5be288c31e1ae3/src/Render/Blur.cs
// License: Assets/ThirdParty/Reticly-LICENSE.txt
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Reticly.Render
{
    /// <summary>Gaussian blur approximation (three box passes) for premultiplied ARGB bitmaps, matching feGaussianBlur stdDeviation.</summary>
    public static class Blur
    {
        public static void Apply(Bitmap bmp, double sigma)
        {
            if (sigma <= 0.05) return;
            int w = bmp.Width, h = bmp.Height;
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            try
            {
                int stride = data.Stride / 4;
                var px = new int[stride * h];
                Marshal.Copy(data.Scan0, px, 0, px.Length);
                var tmp = new int[px.Length];
                foreach (int r in BoxesForGauss(sigma, 3))
                {
                    if (r <= 0) continue;
                    BoxH(px, tmp, w, h, stride, r);
                    BoxV(tmp, px, w, h, stride, r);
                }
                Marshal.Copy(px, 0, data.Scan0, px.Length);
            }
            finally { bmp.UnlockBits(data); }
        }

        static int[] BoxesForGauss(double sigma, int n)
        {
            double wIdeal = Math.Sqrt(12 * sigma * sigma / n + 1);
            int wl = (int)Math.Floor(wIdeal);
            if (wl % 2 == 0) wl--;
            int wu = wl + 2;
            double mIdeal = (12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4 * wl - 4);
            int m = (int)Math.Round(mIdeal);
            var sizes = new int[n];
            for (int i = 0; i < n; i++) sizes[i] = ((i < m ? wl : wu) - 1) / 2;
            return sizes;
        }

        static void BoxH(int[] src, int[] dst, int w, int h, int stride, int r)
        {
            double inv = 1.0 / (r + r + 1);
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                long sa = 0, sr = 0, sg = 0, sb = 0;
                for (int k = -r; k <= r; k++)
                {
                    int x = Math.Min(w - 1, Math.Max(0, k));
                    if (k < 0 || k >= w) continue;
                    Acc(src[row + x], ref sa, ref sr, ref sg, ref sb, 1);
                }
                for (int x = 0; x < w; x++)
                {
                    dst[row + x] = Pack(sa * inv, sr * inv, sg * inv, sb * inv);
                    int add = x + r + 1, rem = x - r;
                    if (add < w) Acc(src[row + add], ref sa, ref sr, ref sg, ref sb, 1);
                    if (rem >= 0) Acc(src[row + rem], ref sa, ref sr, ref sg, ref sb, -1);
                }
            }
        }

        static void BoxV(int[] src, int[] dst, int w, int h, int stride, int r)
        {
            double inv = 1.0 / (r + r + 1);
            for (int x = 0; x < w; x++)
            {
                long sa = 0, sr = 0, sg = 0, sb = 0;
                for (int k = 0; k <= r && k < h; k++)
                    Acc(src[k * stride + x], ref sa, ref sr, ref sg, ref sb, 1);
                for (int y = 0; y < h; y++)
                {
                    dst[y * stride + x] = Pack(sa * inv, sr * inv, sg * inv, sb * inv);
                    int add = y + r + 1, rem = y - r;
                    if (add < h) Acc(src[add * stride + x], ref sa, ref sr, ref sg, ref sb, 1);
                    if (rem >= 0) Acc(src[rem * stride + x], ref sa, ref sr, ref sg, ref sb, -1);
                }
            }
        }

        static void Acc(int c, ref long a, ref long r, ref long g, ref long b, int sign)
        {
            a += sign * ((c >> 24) & 0xFF);
            r += sign * ((c >> 16) & 0xFF);
            g += sign * ((c >> 8) & 0xFF);
            b += sign * (c & 0xFF);
        }

        static int Pack(double a, double r, double g, double b)
        {
            int ia = Clamp(a), ir = Math.Min(ia, Clamp(r)), ig = Math.Min(ia, Clamp(g)), ib = Math.Min(ia, Clamp(b));
            return (ia << 24) | (ir << 16) | (ig << 8) | ib;
        }

        static int Clamp(double v) => v <= 0 ? 0 : v >= 255 ? 255 : (int)(v + 0.5);
    }
}
