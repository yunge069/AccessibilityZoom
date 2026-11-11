// MIT licensed Reticly component. Copyright (c) 2026 Jonah.
// Source: https://github.com/corund207/Reticly/blob/ea9364b5ac090106db02cfdf6e5be288c31e1ae3/src/Render/Renderer.cs
// License: Assets/ThirdParty/Reticly-LICENSE.txt
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using Reticly.Core;

namespace Reticly.Render
{
    public sealed class RenderResult : IDisposable
    {
        public Bitmap Bitmap;
        /// <summary>Pixel in <see cref="Bitmap"/> that corresponds to design coordinate (0,0) — the screen center.</summary>
        public int OriginX, OriginY;
        /// <summary>True if the frame depends on time (GIF images), so the overlay must keep redrawing.</summary>
        public bool TimeDependent;
        public void Dispose() { Bitmap?.Dispose(); Bitmap = null; }
    }

    /// <summary>
    /// Renders Crosshair X layer models. Geometry follows the original designer exactly:
    /// design coordinate (0,0) is the top-left corner of the screen's center pixel, odd thickness lines
    /// cover that pixel, and gaps/thickness use the same "crisp" pixel snapping rules.
    /// </summary>
    public static class CrosshairRenderer
    {
        const double Deg = Math.PI / 180, Rad2Deg = 180 / Math.PI;

        // ---------- JS-compatible numeric helpers ----------
        public static double JsRound(double v) => Math.Floor(v + 0.5);
        static double R2(double v) => Math.Floor(100 * v + 0.5) / 100;
        public static double Crisp(double t) => Math.Max(1, JsRound(t));
        static double SymGap(double g) => JsRound(g / 2);

        static void CrispGap(double gap, double thick, out double gapNeg, out double gapPos)
        {
            if (gap == 0 && thick % 2 == 1) { gapNeg = 0; gapPos = 0; return; }
            if (gap % 2 == 1) { gapNeg = gapPos = Math.Floor((gap + 1) / 2); return; }
            gapNeg = Math.Floor(gap / 2) + 1;
            gapPos = Math.Floor(gap / 2);
        }

        static double AdjustedGap(double thick, double gap)
        {
            double n = double.IsNaN(gap) || double.IsInfinity(gap) ? 0 : gap;
            if ((n == 0 && thick % 2 == 1) || thick % 2 == n % 2) return n;
            var cands = new[] { n - 1, n + 1 }.Where(x => x >= 0).ToList();
            if (cands.Count == 0) return n;
            double best = cands[0];
            foreach (var c in cands.Skip(1)) if (!(Math.Abs(best - n) <= Math.Abs(c - n))) best = c;
            return best;
        }

        static PointF Rot(double x, double y, double deg)
        {
            if (deg == 0) return new PointF((float)x, (float)y);
            double a = deg * Deg, c = Math.Cos(a), s = Math.Sin(a);
            return new PointF((float)(Math.Floor(100 * (x * c - y * s) + 0.5) / 100), (float)(Math.Floor(100 * (x * s + y * c) + 0.5) / 100));
        }

        static double DirAngle(string dir)
        {
            switch (dir) { case "right": return 90; case "bottom": return 180; case "left": return 270; default: return 0; }
        }

        static bool Alive(double opacity, double length, double thickness) => opacity != 0 && length != 0 && thickness != 0;

        // ---------- Public API ----------

        public static RenderResult Render(List<object> layers, double scale = 1, long timeMs = 0, int fixedHalf = 0)
        {
            var res = new RenderResult();
            if (layers == null) layers = new List<object>();
            int half = fixedHalf > 0 ? fixedHalf : ComputeCanvasHalf(layers, scale);
            int size = half * 2;
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            res.Bitmap = bmp;
            res.OriginX = half;
            res.OriginY = half;
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                var ctx = new Ctx { Main = g, Size = size, Scale = scale, TimeMs = timeMs };
                using var baseM = new Matrix();
                baseM.Translate(half, half);
                baseM.Scale((float)scale, (float)scale);
                for (int i = 0; i < layers.Count; i++)
                {
                    if (!(layers[i] is Dictionary<string, object> layer)) continue;
                    if (J.Bool(layer, "hidden")) continue;
                    try
                    {
                        switch (Defaults.LayerType(layer))
                        {
                            case "model": RenderModel(ctx, layer, baseM); break;
                            case "image": RenderImageLayer(ctx, layer, baseM, false); break;
                            case "draw": RenderImageLayer(ctx, layer, baseM, true); break;
                            case "shape": RenderShapeLayer(ctx, layer, baseM); break;
                            case "text": RenderTextLayer(ctx, layer, baseM); break;
                        }
                    }
                    catch { /* a broken layer must never take down the overlay */ }
                }
                res.TimeDependent = ctx.TimeDependent;
            }
            return res;
        }

        public static int ComputeCanvasHalf(List<object> layers, double scale)
        {
            double r = 0, blur = 0;
            foreach (var l in layers)
            {
                if (!(l is Dictionary<string, object> d)) continue;
                r = Math.Max(r, ComputeMaxBound(d));
                blur = Math.Max(blur, MaxBlur(d));
            }
            if (double.IsNaN(r) || double.IsInfinity(r)) r = 0;
            r = Math.Max(1, Math.Ceiling(r));
            double half = (2 * r + 3 * blur) * scale + 4;
            return (int)Math.Min(2400, Math.Max(16, Math.Ceiling(half)));
        }

        static double MaxBlur(Dictionary<string, object> l)
        {
            double b = J.Num(l, "blur");
            foreach (var k in new[] { "dot", "line", "outline" }) b = Math.Max(b, J.Num(J.Obj(l, k), "blur"));
            return b * Math.Max(1, J.Num(J.Obj(l, "layer"), "size", 1));
        }

        /// <summary>Maximum distance from the origin any pixel of this layer can reach (including its animation targets).</summary>
        public static double ComputeMaxBound(Dictionary<string, object> l)
        {
            var type = Defaults.LayerType(l);
            var fo = J.ObjOrEmpty(l, "firingOptions");
            var stages = (J.List(fo, "stages") ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
            var tl = J.Obj(fo, "triggerTimelines");
            var timelines = new List<Dictionary<string, object>> { fo };
            timelines.AddRange(stages);
            if (tl != null)
                foreach (var v in tl.Values.OfType<Dictionary<string, object>>())
                {
                    timelines.Add(v);
                    timelines.AddRange((J.List(v, "stages") ?? new List<object>()).OfType<Dictionary<string, object>>());
                }

            switch (type)
            {
                case "model":
                    {
                        Func<double, double> e = t => Math.Max(1, JsRound(t));
                        var line = J.ObjOrEmpty(l, "line");
                        var dot = J.ObjOrEmpty(l, "dot");
                        var pos = J.ObjOrEmpty(l, "position");
                        double ol = e(J.Num(J.Obj(l, "outline"), "thickness"));
                        double th = J.Num(line, "thickness"), off = J.Num(line, "offset"), len = J.Num(line, "length");
                        double size = J.Num(J.Obj(l, "layer"), "size", 1);
                        double px = Math.Abs(J.Num(pos, "x")), py = Math.Abs(J.Num(pos, "y"));
                        double capT = J.Num(line, "capThickness"), capL = J.Num(line, "capLength"), ang = J.Num(line, "angle", 60);
                        double dd = J.Num(dot, "diameter");
                        foreach (var t in timelines)
                        {
                            var tlin = J.Obj(t, "line");
                            if (J.Obj(t, "outline") is var to && J.NumOpt(to, "thickness") is double ot) ol = Math.Max(ol, e(ot));
                            if (J.NumOpt(tlin, "thickness") is double a1) th = Math.Max(th, a1);
                            if (J.NumOpt(tlin, "offset") is double a2) off = Math.Max(off, Math.Abs(a2));
                            if (J.NumOpt(tlin, "length") is double a3) len = Math.Max(len, a3);
                            if (J.NumOpt(J.Obj(t, "layer"), "size") is double a4) size = Math.Max(size, a4);
                            if (J.NumOpt(J.Obj(t, "position"), "x") is double a5) px = Math.Max(px, Math.Abs(a5));
                            if (J.NumOpt(J.Obj(t, "position"), "y") is double a6) py = Math.Max(py, Math.Abs(a6));
                            if (J.NumOpt(tlin, "capThickness") is double a7) capT = Math.Max(capT, a7);
                            if (J.NumOpt(tlin, "capLength") is double a8) capL = Math.Max(capL, a8);
                            if (J.NumOpt(tlin, "angle") is double a9) ang = Math.Max(ang, a9);
                            if (J.NumOpt(J.Obj(t, "dot"), "diameter") is double a10) dd = Math.Max(dd, a10);
                            if (J.NumOpt(t, "firingOffset") is double fof) off = Math.Max(off, J.Num(line, "offset") + Math.Abs(fof));
                        }
                        string shape = J.Str(line, "shape", "rectangle");
                        double u;
                        if (shape == "text")
                        {
                            double fs = J.Num(line, "fontSize", 10);
                            double tw = EstimateTextWidth(J.Str(line, "text", ""), fs, J.Num(line, "letterSpacing"), J.Num(line, "fontWeight", 400));
                            u = off + Math.Max(tw / 2, fs / 2) + ol;
                        }
                        else if (shape == "arc") u = Math.Ceiling(off / 2) + th + ol;
                        else if (shape == "tShape") u = Math.Max(off + len + capT + ol + th / 2, capL / 2 + ol);
                        else if (shape == "chevronIn" || shape == "chevronOut")
                        {
                            double a = Math.Min(170, ang) / 2 * Deg;
                            double ci = len * Math.Cos(a), cs = len * Math.Sin(a), tt = (th + 2 * ol) / 2;
                            u = Math.Max(off + ci + tt, cs + tt);
                        }
                        else if (shape == "image") u = off + len + ol + th;
                        else u = off + len + ol + th / 2;
                        double dotR = Math.Sqrt(2 * dd * dd) / 2 + Math.Sqrt(2 * ol * ol) + J.Num(dot, "thickness");
                        double ph = Math.Sqrt(px * px + py * py);
                        return Math.Max(u * size + ph, dotR * size + ph);
                    }
                case "image":
                case "draw":
                    {
                        double size = J.Num(J.Obj(l, "layer"), "size", 1);
                        double w = J.Num(l, "width"), h = J.Num(l, "height");
                        double px = Math.Abs(J.Num(J.Obj(l, "position"), "x")), py = Math.Abs(J.Num(J.Obj(l, "position"), "y"));
                        foreach (var t in timelines)
                        {
                            if (J.NumOpt(t, "width") is double a) w = Math.Max(w, a);
                            if (J.NumOpt(t, "height") is double b) h = Math.Max(h, b);
                            if (J.NumOpt(J.Obj(t, "layer"), "size") is double c) size = Math.Max(size, c);
                            if (J.NumOpt(J.Obj(t, "position"), "x") is double x) px = Math.Max(px, Math.Abs(x));
                            if (J.NumOpt(J.Obj(t, "position"), "y") is double y) py = Math.Max(py, Math.Abs(y));
                        }
                        return Math.Sqrt(w * w + h * h) * size / 2 + Math.Sqrt(px * px + py * py) + 1;
                    }
                case "shape":
                    {
                        double w = J.Num(l, "width"), h = J.Num(l, "height");
                        double px = Math.Abs(J.Num(J.Obj(l, "position"), "x")), py = Math.Abs(J.Num(J.Obj(l, "position"), "y"));
                        foreach (var t in timelines)
                        {
                            if (J.NumOpt(t, "width") is double a) w = Math.Max(w, a);
                            if (J.NumOpt(t, "height") is double b) h = Math.Max(h, b);
                            if (J.NumOpt(J.Obj(t, "position"), "x") is double x) px = Math.Max(px, Math.Abs(x));
                            if (J.NumOpt(J.Obj(t, "position"), "y") is double y) py = Math.Max(py, Math.Abs(y));
                        }
                        double ot = Crisp(J.Num(J.Obj(l, "outline"), "thickness"));
                        // anchors can push the shape a full width/height away from its position
                        return Math.Sqrt(w * w + h * h) + ot + Math.Sqrt(px * px + py * py) + 1;
                    }
                case "text":
                    {
                        double fs = J.Num(l, "fontSize", 12);
                        double px = Math.Abs(J.Num(J.Obj(l, "position"), "x")), py = Math.Abs(J.Num(J.Obj(l, "position"), "y"));
                        double t = Crisp(J.Num(J.Obj(l, "outline"), "thickness")) * 2;
                        foreach (var tm in timelines)
                        {
                            if (J.NumOpt(tm, "fontSize") is double a) fs = Math.Max(fs, a);
                            if (J.NumOpt(J.Obj(tm, "position"), "x") is double x) px = Math.Max(px, Math.Abs(x));
                            if (J.NumOpt(J.Obj(tm, "position"), "y") is double y) py = Math.Max(py, Math.Abs(y));
                        }
                        if (J.Str(l, "layout") == "circle")
                            return Math.Max(px, py) + J.Num(l, "radius", 30) + fs + t;
                        double tw = EstimateTextWidth(J.Str(l, "text", ""), fs, J.Num(l, "letterSpacing"), J.Num(l, "fontWeight", 400));
                        return Math.Sqrt(px * px + py * py) + Math.Sqrt(tw * tw / 4 + fs * fs / 4) + t + 2;
                    }
            }
            return 0;
        }

        public static double EstimateTextWidth(string text, double fontSize, double letterSpacing, double weight)
        {
            text = text ?? "";
            return text.Length * fontSize * (weight >= 700 ? 0.75 : 0.7) + Math.Max(0, text.Length - 1) * letterSpacing;
        }

        /// <summary>Approximate bounding box of a layer in design coordinates (used for designer selection and hit testing).</summary>
        public static RectangleF LayerBounds(Dictionary<string, object> l)
        {
            var pos = J.ObjOrEmpty(l, "position");
            float px = (float)J.Num(pos, "x"), py = (float)J.Num(pos, "y");
            switch (Defaults.LayerType(l))
            {
                case "image":
                case "draw":
                    {
                        double s = J.Num(J.Obj(l, "layer"), "size", 1);
                        double w = JsRound(J.Num(l, "width") * s), h = JsRound(J.Num(l, "height") * s);
                        double rot = J.Num(l, "rotation") * Deg;
                        double bw = Math.Abs(w * Math.Cos(rot)) + Math.Abs(h * Math.Sin(rot));
                        double bh = Math.Abs(w * Math.Sin(rot)) + Math.Abs(h * Math.Cos(rot));
                        return new RectangleF(px - (float)bw / 2, py - (float)bh / 2, (float)bw, (float)bh);
                    }
                case "shape":
                    {
                        float w = (float)J.Num(l, "width"), h = (float)J.Num(l, "height");
                        AnchorOffset(J.ObjOrEmpty(l, "anchor"), w, h, false, out double ox, out double oy);
                        if (J.Num(l, "rotation") != 0)
                        {
                            float m = (float)(Math.Sqrt(w * w + h * h) + Math.Max(Math.Abs(ox), Math.Abs(oy)));
                            return new RectangleF(px - m, py - m, 2 * m, 2 * m);
                        }
                        return new RectangleF(px + (float)ox, py + (float)oy, w, h);
                    }
                case "text":
                    {
                        double fs = J.Num(l, "fontSize", 12);
                        if (J.Str(l, "layout") == "circle")
                        {
                            float rr = (float)(J.Num(l, "radius", 30) + fs);
                            return new RectangleF(px - rr, py - rr, rr * 2, rr * 2);
                        }
                        float tw = (float)EstimateTextWidth(J.Str(l, "text", ""), fs, J.Num(l, "letterSpacing"), J.Num(l, "fontWeight", 400));
                        return new RectangleF(px - tw / 2, py - (float)fs / 2, tw, (float)fs);
                    }
                default:
                    {
                        var copy = J.CloneObj(l);
                        copy["position"] = J.O("x", 0, "y", 0);
                        copy["firingOptions"] = J.O();
                        float r = (float)Math.Max(2, ComputeMaxBound(copy));
                        return new RectangleF(px - r, py - r, 2 * r, 2 * r);
                    }
            }
        }

        // ---------- Rendering context ----------

        sealed class Ctx
        {
            public Graphics Main;
            public int Size;
            public double Scale;
            public long TimeMs;
            public bool TimeDependent;
        }

        static void Setup(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
        }

        /// <summary>Draws a group of primitives, optionally through a gaussian blur, like an SVG &lt;g filter&gt;.</summary>
        static void Group(Ctx ctx, Matrix m, double blur, double blurScale, Action<Graphics> draw)
        {
            double sigma = blur * blurScale;
            if (sigma > 0.05)
            {
                using (var tmp = new Bitmap(ctx.Size, ctx.Size, PixelFormat.Format32bppPArgb))
                {
                    using (var g2 = Graphics.FromImage(tmp))
                    {
                        g2.Clear(Color.Transparent);
                        Setup(g2);
                        g2.Transform = m;
                        draw(g2);
                    }
                    Blur.Apply(tmp, sigma);
                    ctx.Main.ResetTransform();
                    ctx.Main.CompositingMode = CompositingMode.SourceOver;
                    ctx.Main.DrawImageUnscaled(tmp, 0, 0);
                }
            }
            else
            {
                Setup(ctx.Main);
                ctx.Main.Transform = m;
                draw(ctx.Main);
            }
        }

        static Matrix Mul(Matrix baseM, Action<Matrix> ops)
        {
            var m = baseM.Clone();
            ops(m);
            return m;
        }

        static void FillRect(Graphics g, Color c, double x, double y, double w, double h)
        {
            if (c.A == 0 || w <= 0 || h <= 0) return;
            using (var b = new SolidBrush(c)) g.FillRectangle(b, (float)x, (float)y, (float)w, (float)h);
        }

        static void FillPoly(Graphics g, Color c, PointF[] pts)
        {
            if (c.A == 0 || pts.Length < 3) return;
            using (var b = new SolidBrush(c)) g.FillPolygon(b, pts);
        }

        static void FillRotRect(Graphics g, Color c, double x, double y, double w, double h, double deg)
        {
            if (c.A == 0) return;
            if (deg == 0) { FillRect(g, c, x, y, w, h); return; }
            FillPoly(g, c, new[] { Rot(x, y, deg), Rot(x + w, y, deg), Rot(x + w, y + h, deg), Rot(x, y + h, deg) });
        }

        // ---------- Model layer ----------

        sealed class Arm
        {
            public Dictionary<string, object> Line;
            public double Opacity, Length, Thickness, Offset, Rotation;
            public string Shape;
            public Color Color;
            public double OutlineThickness, OutlineOpacity;
            public Color OutlineColor;
        }

        static void RenderModel(Ctx ctx, Dictionary<string, object> l, Matrix baseM)
        {
            var line = J.ObjOrEmpty(l, "line");
            var dot = J.ObjOrEmpty(l, "dot");
            var outline = J.ObjOrEmpty(l, "outline");
            var pos = J.ObjOrEmpty(l, "position");
            double size = J.Num(J.Obj(l, "layer"), "size", 1);
            if (size == 0) return;
            string shape = J.Str(line, "shape", "rectangle");
            double rot = J.Num(line, "rotation");
            bool chevron = shape == "chevronIn" || shape == "chevronOut";

            // whole-pixel positions keep moving layers (recoil trackers, animated offsets) crisp
            var layerM = Mul(baseM, m => { m.Translate((float)JsRound(J.Num(pos, "x")), (float)JsRound(J.Num(pos, "y"))); m.Scale((float)size, (float)size); });
            var lineM = Mul(layerM, m => { if (!chevron && rot != 0) m.Rotate((float)rot); });
            double bs = ctx.Scale * size;

            var arm = new Arm
            {
                Line = line,
                Opacity = J.Num(line, "opacity", 1),
                Length = J.Num(line, "length"),
                Thickness = J.Num(line, "thickness"),
                Offset = J.Num(line, "offset"),
                Rotation = rot,
                Shape = shape,
                Color = ColorUtil.Parse(J.Str(line, "color")),
                OutlineThickness = J.Num(outline, "thickness"),
                OutlineOpacity = J.Num(outline, "opacity"),
                OutlineColor = ColorUtil.Parse(J.Str(outline, "color"), Color.Black)
            };

            int armCount = J.Has(line, "armCount") ? (int)JsRound(J.Num(line, "armCount", 4)) : 4;
            bool radial = armCount != 4 && armCount >= 3 && armCount <= 8;
            bool lineAlive = Alive(arm.Opacity, arm.Length, arm.Thickness);

            // 1. line outlines
            if (lineAlive && arm.OutlineOpacity != 0 && shape != "image")
                Group(ctx, lineM, J.Num(outline, "blur"), bs, g => DrawArms(g, arm, radial, armCount, true));
            // 2. dot outline, 3. dot
            string dotShape = J.Str(dot, "shape", "");
            double dotOpacity = J.Num(dot, "opacity"), dotDiameter = J.Num(dot, "diameter");
            bool dotAlive = !string.IsNullOrEmpty(dotShape) && dotOpacity != 0 && dotDiameter != 0;
            if (dotAlive && arm.OutlineOpacity != 0)
                Group(ctx, layerM, J.Num(outline, "blur"), bs, g => DrawDot(g, dot, outline, true));
            if (dotAlive)
                Group(ctx, layerM, J.Num(dot, "blur"), bs, g => DrawDot(g, dot, outline, false));
            // 4. lines
            if (lineAlive)
                Group(ctx, lineM, J.Num(line, "blur"), bs, g => DrawArms(g, arm, radial, armCount, false));

            layerM.Dispose();
            lineM.Dispose();
        }

        /// <summary>Per-arm visibility multiplier; animations write fractional values into "_vis" / "_visArms".</summary>
        static double ArmAlpha(Dictionary<string, object> line, string dir)
        {
            if (J.Obj(line, "_vis") is Dictionary<string, object> v && v.TryGetValue(dir, out var a)) return J.ToNum(a, 0);
            var vis = J.List(line, "visible");
            if (vis == null) return 1;
            foreach (var x in vis) if (x as string == dir) return 1;
            return 0;
        }

        static double RadialArmAlpha(Dictionary<string, object> line, int k)
        {
            if (J.Obj(line, "_visArms") is Dictionary<string, object> v && v.TryGetValue(k.ToString(), out var a)) return J.ToNum(a, 0);
            var vis = J.List(line, "visibleArms");
            if (vis == null) return 1;
            foreach (var x in vis) if ((int)J.ToNum(x, -1) == k) return 1;
            return 0;
        }

        static void DrawArms(Graphics g, Arm arm, bool radial, int armCount, bool outline)
        {
            if (!radial)
            {
                double offset = arm.Offset;
                var vis = J.List(arm.Line, "visible");
                if (vis != null && vis.Count > 1) offset = AdjustedGap(arm.Thickness, offset);
                foreach (var dir in Defaults.AllDirections)
                {
                    double a = ArmAlpha(arm.Line, dir);
                    if (a <= 0) continue;
                    DrawArm(g, arm, offset, dir, null, a, outline);
                }
                return;
            }

            string s = arm.Shape;
            bool rectOrTri = s == "triangle" || s == "rectangle";
            bool zeroGapShape = (s == "chevronIn" || s == "chevronOut" || s == "text" || rectOrTri) && arm.Offset == 0;
            double off = rectOrTri ? arm.Offset : zeroGapShape ? 0 : Math.Max(2 * SymGap(arm.Offset), 1);
            double step = 360.0 / armCount;
            bool chev = s == "chevronIn" || s == "chevronOut";
            bool anglePassed = chev || s == "arc" || s == "rectangle" || s == "tShape" || s == "text";
            for (int k = 0; k < armCount; k++)
            {
                double a = RadialArmAlpha(arm.Line, k);
                if (a <= 0) continue;
                double A = step * k;
                if (chev) DrawArm(g, arm, off, "top", A + arm.Rotation, a, outline, chevronAngle: true);
                else if (anglePassed) DrawArm(g, arm, off, "top", A - 90 + (s == "text" ? arm.Rotation : 0), a, outline);
                else
                {
                    var st = g.Save();
                    g.RotateTransform((float)A);
                    DrawArm(g, arm, off, "top", null, a, outline, smooth: true);
                    g.Restore(st);
                }
            }
        }

        static void DrawArm(Graphics g, Arm arm, double offset, string dir, double? angle, double alpha, bool outline, bool smooth = false, bool chevronAngle = false)
        {
            double T = arm.Thickness, L = arm.Length, G = offset;
            var body = ColorUtil.WithOpacity(arm.Color, arm.Opacity * alpha);
            var oc = ColorUtil.WithOpacity(arm.OutlineColor, arm.OutlineOpacity * alpha);
            double e = Crisp(arm.OutlineThickness);

            switch (arm.Shape)
            {
                case "triangle": DrawTriangleArm(g, T, L, G, dir, smooth || angle.HasValue, body, oc, e, outline); return;
                case "chevronIn":
                case "chevronOut":
                    {
                        double ang = angle ?? (DirAngle(dir) + arm.Rotation);
                        DrawChevronArm(g, arm, G, ang, body, oc, e, outline);
                        return;
                    }
                case "arc": DrawArcArm(g, arm, G, dir, angle, body, oc, e, outline); return;
                case "tShape": DrawTShapeArm(g, arm, G, dir, angle.HasValue ? angle.Value + 90 : 0, body, oc, e, outline); return;
                case "image": if (!outline) DrawImageArm(g, arm, G, dir, alpha); return;
                case "text": DrawTextArm(g, arm, G, dir, angle, alpha, outline); return;
                default: DrawRectArm(g, T, L, G, dir, angle, body, oc, e, outline); return;
            }
        }

        static void DrawRectArm(Graphics g, double T, double L, double G, string dir, double? angle, Color body, Color oc, double e, bool outline)
        {
            if (angle.HasValue)
            {
                double n = Math.Max(.001, T), r = Math.Max(.001, L), t = G / 2, i = n / 2, deg = angle.Value + 90;
                if (!outline)
                {
                    FillPoly(g, body, new[] { Rot(-i, -t - r, deg), Rot(i, -t - r, deg), Rot(i, -t, deg), Rot(-i, -t, deg) });
                }
                else
                {
                    FillRotRect(g, oc, -i - e, -t - r - e, n + 2 * e, e, deg);
                    FillRotRect(g, oc, -i - e, -t, n + 2 * e, e, deg);
                    FillRotRect(g, oc, -i - e, -t - r, e, r, deg);
                    FillRotRect(g, oc, i, -t - r, e, r, deg);
                }
                return;
            }
            CrispGap(G, T, out double gn, out double gp);
            double a = Math.Floor(T / 2);
            double x, y, w, h;
            switch (dir)
            {
                case "top": x = -a; y = -gn - L + 1; w = T; h = L; break;
                case "bottom": x = -a; y = gp; w = T; h = L; break;
                case "left": x = -gn - L + 1; y = -a; w = L; h = T; break;
                default: x = gp; y = -a; w = L; h = T; break;
            }
            if (!outline) FillRect(g, body, x, y, w, h);
            else OutlineRects(g, oc, x, y, w, h, e);
        }

        static void OutlineRects(Graphics g, Color oc, double x, double y, double w, double h, double e)
        {
            FillRect(g, oc, x - e, y - e, w + 2 * e, e);
            FillRect(g, oc, x - e, y + h, w + 2 * e, e);
            FillRect(g, oc, x - e, y, e, h);
            FillRect(g, oc, x + w, y, e, h);
        }

        static void DrawTriangleArm(Graphics g, double T, double L, double G, string dir, bool smooth, Color body, Color oc, double e, bool outline)
        {
            PointF[] pts;
            if (smooth)
            {
                double n = T / 2, r = G / 2;
                pts = new[] { P(0, R2(-r)), P(R2(-n), R2(-r - L)), P(R2(n), R2(-r - L)) };
            }
            else
            {
                double l = Math.Floor(T / 2), s = Math.Ceiling(T / 2);
                CrispGap(G, T, out double gn, out double gp);
                double f = gn - 1, p = gp;
                switch (dir)
                {
                    case "top": pts = new[] { P(0, -f), P(-l, -f - L), P(s, -f - L) }; break;
                    case "bottom": pts = new[] { P(0, p), P(-l, p + L), P(s, p + L) }; break;
                    case "left": pts = new[] { P(-f, 0), P(-f - L, -l), P(-f - L, s) }; break;
                    default: pts = new[] { P(p, 0), P(p + L, -l), P(p + L, s) }; break;
                }
            }
            if (!outline) FillPoly(g, body, pts);
            else if (oc.A > 0)
                using (var pen = new Pen(oc, (float)e) { LineJoin = LineJoin.Miter })
                    g.DrawPolygon(pen, pts);
        }

        static PointF P(double x, double y) => new PointF((float)x, (float)y);

        static void DrawChevronArm(Graphics g, Arm arm, double G, double angleDeg, Color body, Color oc, double e, bool outline)
        {
            bool isIn = arm.Shape == "chevronIn";
            double angle = J.Num(arm.Line, "angle", 60);
            if (!isIn) angle = Math.Min(170, angle);
            double half = angle / 2 * Deg;
            double o = arm.Length * Math.Cos(half), ex = arm.Length * Math.Sin(half);
            double sw = Math.Max(1, JsRound(arm.Thickness));
            double sg = SymGap(G);
            PointF[] v;
            if (isIn)
            {
                double t = sg == 0 ? 0 : sg + JsRound(sw / 2);
                v = new[] { Rot(-ex, -t - o, angleDeg), Rot(0, -t, angleDeg), Rot(ex, -t - o, angleDeg) };
            }
            else
            {
                double r = sg + JsRound(sw / 2);
                v = new[] { Rot(-ex, -r, angleDeg), Rot(0, -r - o, angleDeg), Rot(ex, -r, angleDeg) };
            }
            double roundness = Math.Max(0, Math.Min(1, J.Num(arm.Line, "roundness")));
            using (var path = ChevronPath(v[0], v[1], v[2], roundness))
            {
                var c = outline ? oc : body;
                if (c.A == 0) return;
                using (var pen = new Pen(c, (float)(outline ? sw + 2 * e : sw)) { LineJoin = LineJoin.Round, StartCap = LineCap.Square, EndCap = LineCap.Square })
                    g.DrawPath(pen, path);
            }
        }

        static GraphicsPath ChevronPath(PointF p0, PointF p1, PointF p2, double r)
        {
            var path = new GraphicsPath();
            var a = new PointF((float)R2(p1.X + r * (p0.X - p1.X)), (float)R2(p1.Y + r * (p0.Y - p1.Y)));
            var b = new PointF((float)R2(p1.X + r * (p2.X - p1.X)), (float)R2(p1.Y + r * (p2.Y - p1.Y)));
            path.AddLine(p0, a);
            // quadratic (a, p1, b) as cubic bezier
            var c1 = new PointF(a.X + 2f / 3f * (p1.X - a.X), a.Y + 2f / 3f * (p1.Y - a.Y));
            var c2 = new PointF(b.X + 2f / 3f * (p1.X - b.X), b.Y + 2f / 3f * (p1.Y - b.Y));
            path.AddBezier(a, c1, c2, b);
            path.AddLine(b, p2);
            return path;
        }

        static readonly Dictionary<string, double> ArcDirAngle = new Dictionary<string, double> { { "top", -90 }, { "bottom", 90 }, { "left", 180 }, { "right", 0 } };

        static void DrawArcArm(Graphics g, Arm arm, double offsetArg, string dir, double? angle, Color body, Color oc, double e, bool outline)
        {
            int arms = J.Has(arm.Line, "armCount") ? (int)Math.Max(3, Math.Min(8, JsRound(J.Num(arm.Line, "armCount", 4)))) : 4;
            double sw = Math.Max(.5, arm.Thickness);
            double gap = Math.Max(.5, offsetArg / 2);
            double full = 360.0 / arms;
            double o = Math.Min(full, arm.Length / gap * Rad2Deg);
            bool isFull = full - .01 <= o;
            double Ro = gap + sw;
            string cap = J.Str(arm.Line, "capStyle", "flat");
            double tOuter = isFull || cap == "angled" ? o : full - gap / Ro * (full - o);
            double pad = Ro > 0 ? .5 / Ro * Rad2Deg : 0;
            double renderSweep = Math.Min(isFull ? o + 2 * pad : o, 179.9);
            double renderOuter = Math.Min(isFull ? tOuter + 2 * pad : tOuter, 179.9);
            double ang = angle ?? ArcDirAngle[dir];
            if (renderSweep <= 0) return;

            if (!outline)
            {
                if (body.A == 0) return;
                using (var path = Sector(Ro, gap, renderSweep, renderOuter, ang))
                using (var b = new SolidBrush(body)) g.FillPath(b, path);
                return;
            }
            if (oc.A == 0 || arm.Thickness <= 0) return;
            double a = Ro + e, r = Math.Max(0, gap - e);
            double inner, outer;
            if (isFull) { inner = renderSweep; outer = renderOuter; }
            else
            {
                inner = Math.Min(renderSweep + 2 * (r > 1 ? e / r * Rad2Deg : e * Rad2Deg), full);
                outer = cap == "angled" ? inner : Math.Min(renderOuter + 2 * (a > 1 ? e / a * Rad2Deg : e * Rad2Deg), full);
            }
            using (var path = new GraphicsPath(FillMode.Alternate))
            {
                AddSector(path, a, r, inner, outer, ang);
                AddSector(path, Ro, gap, renderSweep, renderOuter, ang);
                using (var b = new SolidBrush(oc)) g.FillPath(b, path);
            }
        }

        static GraphicsPath Sector(double outerR, double innerR, double innerSweep, double outerSweep, double angle)
        {
            var p = new GraphicsPath(FillMode.Winding);
            AddSector(p, outerR, innerR, innerSweep, outerSweep, angle);
            return p;
        }

        static void AddSector(GraphicsPath p, double outerR, double innerR, double innerSweep, double outerSweep, double angle)
        {
            innerSweep = Math.Max(0, Math.Min(innerSweep, 359.98));
            outerSweep = Math.Max(0, Math.Min(outerSweep, 359.98));
            if (outerR <= 0 || outerSweep <= 0) return;
            p.StartFigure();
            p.AddArc((float)-outerR, (float)-outerR, (float)(2 * outerR), (float)(2 * outerR), (float)(angle - outerSweep / 2), (float)outerSweep);
            if (innerR > 0 && innerSweep > 0)
                p.AddArc((float)-innerR, (float)-innerR, (float)(2 * innerR), (float)(2 * innerR), (float)(angle + innerSweep / 2), (float)-innerSweep);
            else
                p.AddLine(Rot(outerR, 0, angle + outerSweep / 2), new PointF(0, 0));
            p.CloseFigure();
        }

        struct TGeo { public double sl, sr, st, sb, cl, cr, ct, cb; public bool vertical, tipAtStart; }

        static TGeo TShapeGeo(Arm arm, double G, string dir)
        {
            double i = arm.Thickness / 2, r = G / 2, o = arm.Length;
            double capL = J.Num(arm.Line, "capLength"), a = J.Num(arm.Line, "capThickness"), c = capL / 2;
            switch (dir)
            {
                case "top": { double u = -r - o; return new TGeo { sl = -i, sr = i, st = u, sb = u + o, cl = -c, cr = c, ct = u - a, cb = u, vertical = true, tipAtStart = true }; }
                case "bottom": return new TGeo { sl = -i, sr = i, st = r, sb = r + o, cl = -c, cr = c, ct = r + o, cb = r + o + a, vertical = true, tipAtStart = false };
                case "left": { double u = -r - o; return new TGeo { sl = u, sr = u + o, st = -i, sb = i, cl = u - a, cr = u, ct = -c, cb = c, vertical = false, tipAtStart = true }; }
                default: return new TGeo { sl = r, sr = r + o, st = -i, sb = i, cl = r + o, cr = r + o + a, ct = -c, cb = c, vertical = false, tipAtStart = false };
            }
        }

        static void DrawTShapeArm(Graphics g, Arm arm, double G, string dir, double rotDeg, Color body, Color oc, double e, bool outline)
        {
            var t = TShapeGeo(arm, G, dir);
            bool capped = J.Num(arm.Line, "capLength") > 0 && J.Num(arm.Line, "capThickness") > 0;
            if (!outline)
            {
                FillRotRect(g, body, t.sl, t.st, t.sr - t.sl, t.sb - t.st, rotDeg);
                if (capped) FillRotRect(g, body, t.cl, t.ct, t.cr - t.cl, t.cb - t.ct, rotDeg);
                return;
            }
            var rects = new List<double[]>();
            double n = t.sl, r = t.sr, o = t.st, i = t.sb, a = t.cl, c = t.cr, u = t.ct, l = t.cb;
            if (!capped)
            {
                rects.Add(new[] { n - e, o - e, r - n + 2 * e, e });
                rects.Add(new[] { n - e, i, r - n + 2 * e, e });
                rects.Add(new[] { n - e, o, e, i - o });
                rects.Add(new[] { r, o, e, i - o });
            }
            else if (t.vertical)
            {
                if (t.tipAtStart)
                {
                    double T = o;
                    rects.Add(new[] { a - e, u - e, c - a + 2 * e, e });
                    rects.Add(new[] { a - e, u, e, l - u });
                    rects.Add(new[] { c, u, e, l - u });
                    rects.Add(new[] { a - e, T, n - a + e, e });
                    rects.Add(new[] { r, T, c - r + e, e });
                    rects.Add(new[] { n - e, T + e, e, i - T - e });
                    rects.Add(new[] { r, T + e, e, i - T - e });
                    rects.Add(new[] { n - e, i, r - n + 2 * e, e });
                }
                else
                {
                    double T = i;
                    rects.Add(new[] { n - e, o - e, r - n + 2 * e, e });
                    rects.Add(new[] { n - e, o, e, T - o - e });
                    rects.Add(new[] { r, o, e, T - o - e });
                    rects.Add(new[] { a - e, T - e, n - a + e, e });
                    rects.Add(new[] { r, T - e, c - r + e, e });
                    rects.Add(new[] { a - e, u, e, l - u });
                    rects.Add(new[] { c, u, e, l - u });
                    rects.Add(new[] { a - e, l, c - a + 2 * e, e });
                }
            }
            else
            {
                if (t.tipAtStart)
                {
                    double T = n;
                    rects.Add(new[] { a - e, u - e, e, l - u + 2 * e });
                    rects.Add(new[] { a, u - e, c - a, e });
                    rects.Add(new[] { a, l, c - a, e });
                    rects.Add(new[] { T, u - e, e, o - u + e });
                    rects.Add(new[] { T, i, e, l - i + e });
                    rects.Add(new[] { T + e, o - e, r - T - e, e });
                    rects.Add(new[] { T + e, i, r - T - e, e });
                    rects.Add(new[] { r, o - e, e, i - o + 2 * e });
                }
                else
                {
                    double T = r;
                    rects.Add(new[] { n - e, o - e, e, i - o + 2 * e });
                    rects.Add(new[] { n, o - e, T - n - e, e });
                    rects.Add(new[] { n, i, T - n - e, e });
                    rects.Add(new[] { T - e, u - e, e, o - u + e });
                    rects.Add(new[] { T - e, i, e, l - i + e });
                    rects.Add(new[] { a, u - e, c - a, e });
                    rects.Add(new[] { a, l, c - a, e });
                    rects.Add(new[] { c, u - e, e, l - u + 2 * e });
                }
            }
            foreach (var rc in rects)
                if (rc[2] > 0 && rc[3] > 0) FillRotRect(g, oc, rc[0], rc[1], rc[2], rc[3], rotDeg);
        }

        static void DrawImageArm(Graphics g, Arm arm, double G, string dir, double alpha)
        {
            string href = J.Str(arm.Line, "image");
            if (string.IsNullOrEmpty(href)) return;
            var img = ImageCache.Get(href);
            var frame = img?.FrameAt(Environment.TickCount);
            if (frame == null) return;
            double T = arm.Thickness, L = arm.Length;
            CrispGap(G, T, out double gn, out _);
            float x = (float)-Math.Floor(T / 2), y = (float)(-gn - L + 1), w = (float)T, h = (float)L;
            var st = g.Save();
            double dirRot = DirAngle(dir);
            if (dirRot != 0) g.RotateTransform((float)dirRot);
            if (J.Bool(arm.Line, "flipImage"))
            {
                g.TranslateTransform(x + w / 2, y + h / 2);
                g.RotateTransform(180);
                g.TranslateTransform(-(x + w / 2), -(y + h / 2));
            }
            double op = Alive(arm.Opacity, L, T) ? arm.Opacity * alpha : 0;
            DrawBitmap(g, frame, x, y, w, h, op, true);
            g.Restore(st);
        }

        static void DrawTextArm(Graphics g, Arm arm, double G, string dir, double? angle, double alpha, bool outline)
        {
            var line = arm.Line;
            string text = J.Str(line, "text", "");
            double fs = J.Num(line, "fontSize", 10);
            if (text.Length == 0 || fs <= 0) return;
            double a = JsRound(G / 2);
            double c = EstimateTextWidth(text, fs, J.Num(line, "letterSpacing"), J.Num(line, "fontWeight", 400)) / 2;
            double x, y, rotate;
            if (angle.HasValue)
            {
                double r = a + c, o = angle.Value + 90;
                var pt = Rot(0, -r, o);
                x = pt.X; y = pt.Y; rotate = o + 90;
            }
            else
            {
                switch (dir)
                {
                    case "top": x = 0; y = -a - c; rotate = 90; break;
                    case "bottom": x = 0; y = a + c; rotate = 90; break;
                    case "left": x = -a - c; y = 0; rotate = 0; break;
                    default: x = a + c; y = 0; rotate = 0; break;
                }
            }
            var st = g.Save();
            g.TranslateTransform((float)x, (float)y);
            if (rotate != 0) g.RotateTransform((float)rotate);
            using (var path = TextPath(text, J.Str(line, "fontFamily", "Segoe UI"), fs, J.Num(line, "fontWeight", 400), J.Num(line, "letterSpacing")))
            {
                if (outline)
                {
                    var oc = ColorUtil.WithOpacity(arm.OutlineColor, arm.OutlineOpacity * alpha);
                    if (oc.A > 0 && arm.OutlineThickness > 0)
                        using (var pen = new Pen(oc, (float)(2 * arm.OutlineThickness)) { LineJoin = LineJoin.Round })
                            g.DrawPath(pen, path);
                }
                else
                {
                    var c2 = ColorUtil.WithOpacity(arm.Color, arm.Opacity * alpha);
                    if (c2.A > 0) using (var b = new SolidBrush(c2)) g.FillPath(b, path);
                }
            }
            g.Restore(st);
        }

        static void DrawDot(Graphics g, Dictionary<string, object> dot, Dictionary<string, object> outline, bool isOutline)
        {
            string shape = J.Str(dot, "shape", "");
            double r = JsRound(J.Num(dot, "diameter"));
            var color = ColorUtil.WithOpacity(ColorUtil.Parse(J.Str(dot, "color")), J.Num(dot, "opacity"));
            var oc = ColorUtil.WithOpacity(ColorUtil.Parse(J.Str(outline, "color"), Color.Black), J.Num(outline, "opacity"));
            double e = Crisp(J.Num(outline, "thickness"));
            switch (shape)
            {
                case "circle":
                    {
                        double rad = isOutline ? r / 2 + e : r / 2;
                        var c = isOutline ? oc : color;
                        if (c.A == 0 || rad <= 0) return;
                        using (var b = new SolidBrush(c)) g.FillEllipse(b, (float)-rad, (float)-rad, (float)(2 * rad), (float)(2 * rad));
                        return;
                    }
                case "square":
                    {
                        double x0 = JsRound(-r / 2);
                        if (!isOutline) FillRect(g, color, x0, x0, r, r);
                        else OutlineRects(g, oc, x0, x0, r, r, e);
                        return;
                    }
                case "diamond":
                    {
                        double u = R2(r / Math.Sqrt(2));
                        if (!isOutline)
                        {
                            FillPoly(g, color, new[] { P(0, -u), P(u, 0), P(0, u), P(-u, 0) });
                            return;
                        }
                        double u2 = R2((r + 2 * e) / Math.Sqrt(2));
                        if (oc.A == 0) return;
                        using (var path = new GraphicsPath(FillMode.Alternate))
                        {
                            path.AddPolygon(new[] { P(0, -u2), P(u2, 0), P(0, u2), P(-u2, 0) });
                            path.AddPolygon(new[] { P(0, -u), P(u, 0), P(0, u), P(-u, 0) });
                            using (var b = new SolidBrush(oc)) g.FillPath(b, path);
                        }
                        return;
                    }
                case "ring":
                    {
                        double th = J.Num(dot, "thickness") != 0 ? JsRound(J.Num(dot, "thickness")) : 1;
                        if (!isOutline)
                        {
                            if (color.A == 0 || th <= 0) return;
                            double rad = r / 2;
                            using (var pen = new Pen(color, (float)th))
                                g.DrawEllipse(pen, (float)-rad, (float)-rad, (float)(2 * rad), (float)(2 * rad));
                            return;
                        }
                        double inner = r / 2 + th / 2, outer = inner + e;
                        if (oc.A == 0) return;
                        using (var path = new GraphicsPath(FillMode.Alternate))
                        {
                            path.AddEllipse((float)-outer, (float)-outer, (float)(2 * outer), (float)(2 * outer));
                            path.AddEllipse((float)-inner, (float)-inner, (float)(2 * inner), (float)(2 * inner));
                            using (var b = new SolidBrush(oc)) g.FillPath(b, path);
                        }
                        return;
                    }
            }
        }

        // ---------- Image / drawing layers ----------

        static void RenderImageLayer(Ctx ctx, Dictionary<string, object> l, Matrix baseM, bool isDraw)
        {
            string href = J.Str(l, "image");
            if (string.IsNullOrEmpty(href)) return;
            double size = J.Num(J.Obj(l, "layer"), "size", 1);
            double w = JsRound(J.Num(l, "width") * size), h = JsRound(J.Num(l, "height") * size);
            double opacity = J.Num(l, "opacity", 1);
            if (opacity == 0 || w == 0 || h == 0 || size == 0) return;
            var img = ImageCache.Get(href);
            if (img == null || img.Failed) return;
            if (img.IsAnimated) ctx.TimeDependent = true;
            var frame = img.FrameAt(ctx.TimeMs);
            if (frame == null) return;
            var pos = J.ObjOrEmpty(l, "position");
            double rot = isDraw ? 0 : J.Num(l, "rotation");
            bool pixelated = isDraw || J.Str(l, "name") == "Drawing";
            if (!pixelated && J.NumOpt(l, "aspectRatio") is double ar && J.Num(l, "width") > 0)
                pixelated = Math.Abs(J.Num(l, "height") / J.Num(l, "width") - ar) > .01;
            var m = Mul(baseM, mm => { mm.Translate((float)J.Num(pos, "x"), (float)J.Num(pos, "y")); if (rot != 0) mm.Rotate((float)rot); });
            Group(ctx, m, 0, 1, g => DrawBitmap(g, frame, (float)(-w / 2), (float)(-h / 2), (float)w, (float)h, opacity, pixelated));
            m.Dispose();
        }

        static void DrawBitmap(Graphics g, Bitmap bmp, float x, float y, float w, float h, double opacity, bool pixelated)
        {
            if (opacity <= 0) return;
            var oldI = g.InterpolationMode;
            g.InterpolationMode = pixelated ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            using (var ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);
                if (opacity < 1)
                {
                    var cm = new ColorMatrix { Matrix33 = (float)Math.Max(0, Math.Min(1, opacity)) };
                    ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                }
                var pts = new[] { new PointF(x, y), new PointF(x + w, y), new PointF(x, y + h) };
                g.DrawImage(bmp, pts, new RectangleF(0, 0, bmp.Width, bmp.Height), GraphicsUnit.Pixel, ia);
            }
            g.InterpolationMode = oldI;
        }

        // ---------- Shape layers ----------

        static void AnchorOffset(Dictionary<string, object> anchor, double w, double h, bool round, out double ox, out double oy)
        {
            string ax = J.Str(anchor, "x", "center"), ay = J.Str(anchor, "y", "center");
            ox = ax == "left" ? 0 : ax == "right" ? -w : -w / 2;
            oy = ay == "top" ? 0 : ay == "bottom" ? -h : -h / 2;
            if (round)
            {
                if (ax == "center") ox = JsRound(ox);
                if (ay == "center") oy = JsRound(oy);
            }
        }

        static void RenderShapeLayer(Ctx ctx, Dictionary<string, object> l, Matrix baseM)
        {
            string shape = J.Str(l, "shape", "square");
            double w = J.Num(l, "width"), h = J.Num(l, "height");
            var color = ColorUtil.WithOpacity(ColorUtil.Parse(J.Str(l, "color")), J.Num(l, "opacity", 1));
            var outline = J.ObjOrEmpty(l, "outline");
            var oc = ColorUtil.WithOpacity(ColorUtil.Parse(J.Str(outline, "color"), Color.Black), J.Num(outline, "opacity"));
            bool hasOutline = J.Num(outline, "thickness") > 0 && J.Num(outline, "opacity") > 0;
            double t = Crisp(J.Num(outline, "thickness"));
            double rot = J.Num(l, "rotation");
            var pos = J.ObjOrEmpty(l, "position");
            var anchor = J.ObjOrEmpty(l, "anchor");
            var m = Mul(baseM, mm => { mm.Translate((float)J.Num(pos, "x"), (float)J.Num(pos, "y")); if (rot != 0) mm.Rotate((float)rot); });

            Func<GraphicsPath> bodyPath = null, outlinePath = null;
            switch (shape)
            {
                case "square":
                    {
                        AnchorOffset(anchor, w, h, rot == 0, out double ox, out double oy);
                        double cr = J.Num(l, "cornerRadius");
                        bodyPath = () => RoundRect(ox, oy, w, h, cr);
                        outlinePath = () => RoundRect(ox - t / 2, oy - t / 2, w + t, h + t, cr > 0 ? cr + t / 2 : 0);
                        break;
                    }
                case "circle":
                    {
                        AnchorOffset(anchor, w, h, true, out double ox, out double oy);
                        double cx = ox + w / 2, cy = oy + h / 2;
                        bodyPath = () => { var p = new GraphicsPath(); p.AddEllipse((float)(cx - w / 2), (float)(cy - h / 2), (float)w, (float)h); return p; };
                        outlinePath = () => { var p = new GraphicsPath(); p.AddEllipse((float)(cx - w / 2 - t / 2), (float)(cy - h / 2 - t / 2), (float)(w + t), (float)(h + t)); return p; };
                        break;
                    }
                case "triangle":
                    {
                        AnchorOffset(anchor, w, h, true, out double ox, out double oy);
                        var pts = new[] { P(ox + w / 2, oy), P(ox, oy + h), P(ox + w, oy + h) };
                        bodyPath = () => { var p = new GraphicsPath(); p.AddPolygon(pts); return p; };
                        outlinePath = () => { var p = new GraphicsPath(); p.AddPolygon(OffsetTriangle(pts, t / 2)); return p; };
                        break;
                    }
                default: m.Dispose(); return;
            }
            double bs = ctx.Scale;
            if (hasOutline && oc.A > 0)
                Group(ctx, m, J.Num(outline, "blur"), bs, g =>
                {
                    using (var p = outlinePath())
                    using (var pen = new Pen(oc, (float)t) { LineJoin = LineJoin.Miter }) g.DrawPath(pen, p);
                });
            if (color.A > 0 && w > 0 && h > 0)
                Group(ctx, m, J.Num(l, "blur"), bs, g =>
                {
                    using (var p = bodyPath())
                    using (var b = new SolidBrush(color)) g.FillPath(b, p);
                });
            m.Dispose();
        }

        static GraphicsPath RoundRect(double x, double y, double w, double h, double r)
        {
            var p = new GraphicsPath();
            r = Math.Max(0, Math.Min(r, Math.Min(w, h) / 2));
            if (r <= 0.01) { p.AddRectangle(new RectangleF((float)x, (float)y, (float)w, (float)h)); return p; }
            float d = (float)(2 * r);
            p.AddArc((float)x, (float)y, d, d, 180, 90);
            p.AddArc((float)(x + w) - d, (float)y, d, d, 270, 90);
            p.AddArc((float)(x + w) - d, (float)(y + h) - d, d, d, 0, 90);
            p.AddArc((float)x, (float)(y + h) - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static PointF[] OffsetTriangle(PointF[] c, double a)
        {
            int u = c.Length;
            var normals = new double[u][];
            for (int s = 0; s < u; s++)
            {
                double dx = c[(s + 1) % u].X - c[s].X, dy = c[(s + 1) % u].Y - c[s].Y, f = Math.Sqrt(dx * dx + dy * dy);
                normals[s] = new[] { -dy / f, dx / f };
            }
            var res = new PointF[u];
            for (int d = 0; d < u; d++)
            {
                var b = normals[(d - 1 + u) % u];
                var m = normals[d];
                double vx = b[0] + m[0], vy = b[1] + m[1], O = Math.Sqrt(vx * vx + vy * vy);
                if (O < 1e-6) { res[d] = P(c[d].X + b[0] * a, c[d].Y + b[1] * a); continue; }
                vx /= O; vy /= O;
                double dot = b[0] * vx + b[1] * vy;
                double k = dot > 1e-6 ? a / dot : a;
                res[d] = P(R2(c[d].X + vx * k), R2(c[d].Y + vy * k));
            }
            return res;
        }

        // ---------- Text layers ----------

        static readonly HashSet<string> installedFonts = new HashSet<string>(new InstalledFontCollection().Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        static FontFamily ResolveFamily(string family)
        {
            if (!string.IsNullOrWhiteSpace(family))
            {
                foreach (var part in family.Split(','))
                {
                    var name = part.Trim().Trim('\'', '"');
                    if (name.Equals("sans-serif", StringComparison.OrdinalIgnoreCase)) name = "Segoe UI";
                    else if (name.Equals("serif", StringComparison.OrdinalIgnoreCase)) name = "Times New Roman";
                    else if (name.Equals("monospace", StringComparison.OrdinalIgnoreCase)) name = "Consolas";
                    if (installedFonts.Contains(name)) return new FontFamily(name);
                }
            }
            return new FontFamily("Segoe UI");
        }

        /// <summary>Builds a text outline path centered horizontally on x=0 with its em box centered on y=0 (SVG "middle"/"central").</summary>
        public static GraphicsPath TextPath(string text, string family, double size, double weight, double letterSpacing)
        {
            var path = new GraphicsPath(FillMode.Winding);
            if (string.IsNullOrEmpty(text) || size <= 0) return path;
            using (var ff = ResolveFamily(family))
            {
                var style = weight >= 600 ? FontStyle.Bold : FontStyle.Regular;
                if (!ff.IsStyleAvailable(style)) style = FontStyle.Regular;
                float em = (float)size;
                float cell = em * (ff.GetCellAscent(style) + ff.GetCellDescent(style)) / ff.GetEmHeight(style);
                using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
                {
                    fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
                    if (Math.Abs(letterSpacing) < 0.001)
                    {
                        path.AddString(text, ff, (int)style, em, new PointF(0, 0), fmt);
                    }
                    else
                    {
                        float x = 0;
                        using (var bmp = new Bitmap(1, 1))
                        using (var gm = Graphics.FromImage(bmp))
                        using (var font = new Font(ff, em, style, GraphicsUnit.Pixel))
                        {
                            gm.TextRenderingHint = TextRenderingHint.AntiAlias;
                            foreach (char ch in text)
                            {
                                var s = ch.ToString();
                                path.AddString(s, ff, (int)style, em, new PointF(x, 0), fmt);
                                float adv = ch == ' ' ? em * 0.28f : gm.MeasureString(s, font, PointF.Empty, fmt).Width;
                                x += adv + (float)letterSpacing;
                            }
                        }
                    }
                }
                var bounds = path.GetBounds();
                float width = 0;
                if (path.PointCount > 0) width = bounds.Right;
                using (var mtx = new Matrix())
                {
                    float left = path.PointCount > 0 ? Math.Min(0, bounds.Left) : 0;
                    mtx.Translate(-(left + width) / 2f, -cell / 2f);
                    path.Transform(mtx);
                }
            }
            return path;
        }

        static void RenderTextLayer(Ctx ctx, Dictionary<string, object> l, Matrix baseM)
        {
            string text = J.Str(l, "text", "");
            double fs = J.Num(l, "fontSize", 12);
            if (text.Length == 0 || fs <= 0) return;
            var color = ColorUtil.WithOpacity(ColorUtil.Parse(J.Str(l, "color")), J.Num(l, "opacity", 1));
            var outline = J.ObjOrEmpty(l, "outline");
            double d = Crisp(J.Num(outline, "thickness"));
            var oc = ColorUtil.WithOpacity(ColorUtil.Parse(J.Str(outline, "color"), Color.Black), J.Num(outline, "opacity"));
            bool hasOutline = J.Num(outline, "thickness") > 0 && J.Num(outline, "opacity") > 0 && oc.A > 0;
            var pos = J.ObjOrEmpty(l, "position");
            double rot = J.Num(l, "rotation");
            string family = J.Str(l, "fontFamily", "Segoe UI");
            double weight = J.Num(l, "fontWeight", 400), ls = J.Num(l, "letterSpacing");
            var m = Mul(baseM, mm => { mm.Translate((float)J.Num(pos, "x"), (float)J.Num(pos, "y")); if (rot != 0) mm.Rotate((float)rot); });

            Func<GraphicsPath> build;
            if (J.Str(l, "layout") == "circle")
            {
                double radius = J.Num(l, "radius", 30);
                bool repeat = J.Bool(l, "repeatToFill");
                double padding = J.Num(l, "repeatPadding");
                build = () => CircleTextPath(text, family, fs, weight, ls, radius, repeat, padding);
            }
            else build = () => TextPath(text, family, fs, weight, ls);

            double bs = ctx.Scale;
            if (hasOutline)
                Group(ctx, m, J.Num(outline, "blur"), bs, g =>
                {
                    using (var p = build())
                    using (var pen = new Pen(oc, (float)(2 * d)) { LineJoin = LineJoin.Round }) g.DrawPath(pen, p);
                });
            if (color.A > 0)
                Group(ctx, m, J.Num(l, "blur"), bs, g =>
                {
                    using (var p = build())
                    using (var b = new SolidBrush(color)) g.FillPath(b, p);
                });
            m.Dispose();
        }

        /// <summary>Lays glyphs along a circle starting at its leftmost point and running clockwise over the top (SVG textPath behaviour).</summary>
        static GraphicsPath CircleTextPath(string text, string family, double fs, double weight, double ls, double radius, bool repeat, double padding)
        {
            var result = new GraphicsPath(FillMode.Winding);
            double circ = 2 * Math.PI * radius;
            int copies = 1;
            if (repeat)
            {
                double unit = 1.25 * EstimateTextWidth(text, fs, ls, weight);
                double gap = Math.Max(.3 * fs, padding);
                copies = Math.Max(1, (int)Math.Floor(circ / (unit + gap)));
            }
            using (var ff = ResolveFamily(family))
            using (var bmp = new Bitmap(1, 1))
            using (var gm = Graphics.FromImage(bmp))
            {
                var style = weight >= 600 ? FontStyle.Bold : FontStyle.Regular;
                if (!ff.IsStyleAvailable(style)) style = FontStyle.Regular;
                float em = (float)fs;
                float ascent = em * ff.GetCellAscent(style) / ff.GetEmHeight(style);
                using (var font = new Font(ff, em, style, GraphicsUnit.Pixel))
                using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
                {
                    fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                    for (int cp = 0; cp < copies; cp++)
                    {
                        double dist = circ * cp / copies;
                        foreach (char ch in text)
                        {
                            var s = ch.ToString();
                            float adv = ch == ' ' ? em * 0.28f : gm.MeasureString(s, font, PointF.Empty, fmt).Width;
                            double mid = dist + adv / 2;
                            if (mid > circ) break;
                            // angle measured from the leftmost point (180deg) going clockwise
                            double theta = Math.PI + mid / radius;
                            float px = (float)(radius * Math.Cos(theta)), py = (float)(radius * Math.Sin(theta));
                            using (var gp = new GraphicsPath())
                            {
                                gp.AddString(s, ff, (int)style, em, new PointF(-adv / 2, -ascent), fmt);
                                using (var mtx = new Matrix())
                                {
                                    mtx.Translate(px, py);
                                    mtx.Rotate((float)(theta * Rad2Deg + 90));
                                    gp.Transform(mtx);
                                }
                                result.AddPath(gp, false);
                            }
                            dist += adv + ls;
                        }
                    }
                }
            }
            return result;
        }
    }
}
