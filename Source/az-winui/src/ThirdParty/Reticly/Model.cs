// MIT licensed Reticly component. Copyright (c) 2026 Jonah.
// Source: https://github.com/corund207/Reticly/blob/ea9364b5ac090106db02cfdf6e5be288c31e1ae3/src/Core/Model.cs
// License: Assets/ThirdParty/Reticly-LICENSE.txt
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;

namespace Reticly.Core
{
    /// <summary>A saved crosshair: metadata plus the Crosshair X compatible layer array.</summary>
    public sealed class CrosshairEntry
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "Untitled";
        public string Folder = "";
        public bool Favorite;
        public string Source = "";          // e.g. "cx:xe4lbu6zh8", "valorant", "cs2", "preset", "designer"
        public DateTime Created = DateTime.UtcNow;
        public DateTime Updated = DateTime.UtcNow;
        public DateTime LastUsed;
        public int Order;
        public List<object> Layers = new List<object>();

        public CrosshairEntry Clone(bool newId = true)
        {
            var c = (CrosshairEntry)MemberwiseClone();
            c.Layers = (List<object>)J.DeepClone(Layers);
            if (newId) c.Id = Guid.NewGuid().ToString("N");
            return c;
        }

        public Dictionary<string, object> ToJson() => J.O(
            "id", Id, "name", Name, "folder", Folder, "favorite", Favorite, "source", Source,
            "created", Created.ToString("o"), "updated", Updated.ToString("o"), "lastUsed", LastUsed.ToString("o"), "order", Order,
            "layers", Layers);

        public static CrosshairEntry FromJson(object o)
        {
            var e = new CrosshairEntry
            {
                Id = J.Str(o, "id") ?? Guid.NewGuid().ToString("N"),
                Name = J.Str(o, "name", "Untitled"),
                Folder = J.Str(o, "folder", ""),
                Favorite = J.Bool(o, "favorite"),
                Source = J.Str(o, "source", ""),
                Order = (int)J.Num(o, "order"),
                Layers = J.List(o, "layers") ?? new List<object>()
            };
            DateTime.TryParse(J.Str(o, "created"), null, DateTimeStyles.RoundtripKind, out e.Created);
            DateTime.TryParse(J.Str(o, "updated"), null, DateTimeStyles.RoundtripKind, out e.Updated);
            DateTime.TryParse(J.Str(o, "lastUsed"), null, DateTimeStyles.RoundtripKind, out e.LastUsed);
            Defaults.NormalizeLayers(e.Layers);
            return e;
        }
    }

    public static class Defaults
    {
        public static readonly string[] AllDirections = { "top", "bottom", "left", "right" };

        public static Dictionary<string, object> FiringOptions() => J.O(
            "duration", 0, "startDelay", 0, "easing", "linear", "mouseButton", "left", "pressType", "press",
            "releaseBehavior", "reset", "direction", "normal", "loop", false, "tShapeWhenFiring", false,
            "firingOffset", 0, "finalOpacity", 1, "bloomDirection", "outward",
            "dot", J.O(), "line", J.O(), "outline", J.O(), "position", J.O(), "layer", J.O(), "version", 2);

        public static Dictionary<string, object> ModelLayer() => J.O(
            "dot", J.O("diameter", 2, "opacity", 0, "color", "#FFFFFF", "shape", "square", "blur", 0),
            "line", J.O("length", 6, "thickness", 2, "offset", 6, "opacity", 1, "color", "#00FF66",
                        "visible", new[] { "top", "right", "bottom", "left" }, "shape", "rectangle", "blur", 0, "rotation", 0),
            "outline", J.O("thickness", 1, "opacity", 1, "color", "#000000", "blur", 0),
            "firingOptions", FiringOptions(),
            "position", J.O("x", 0, "y", 0),
            "layer", J.O("size", 1),
            "type", "model");

        public static Dictionary<string, object> ImageLayer(string dataUrl, int w, int h, string name = "Image") => J.O(
            "type", "image", "name", name, "image", dataUrl, "width", w, "height", h,
            "aspectRatio", w > 0 ? (double)h / w : 1, "lockAspectRatio", true, "opacity", 1, "rotation", 0,
            "position", J.O("x", 0, "y", 0), "layer", J.O("size", 1), "firingOptions", FiringOptions());

        public static Dictionary<string, object> ShapeLayer(string shape = "square") => J.O(
            "type", "shape", "shape", shape, "width", 12, "height", 12, "color", "#FFFFFF", "opacity", 1,
            "anchor", J.O("x", "center", "y", "center"), "cornerRadius", 0, "rotation", 0, "blur", 0,
            "position", J.O("x", 0, "y", 0),
            "outline", J.O("thickness", 1, "opacity", 0, "color", "#000000", "blur", 0),
            "firingOptions", FiringOptions());

        public static Dictionary<string, object> TextLayer(string text = "+") => J.O(
            "type", "text", "text", text, "fontSize", 16, "fontFamily", "Segoe UI", "fontWeight", 700, "letterSpacing", 0,
            "color", "#FFFFFF", "opacity", 1, "rotation", 0, "blur", 0, "layout", "straight", "radius", 30,
            "repeatToFill", false, "repeatPadding", 0,
            "position", J.O("x", 0, "y", 0),
            "outline", J.O("thickness", 1, "opacity", 1, "color", "#000000", "blur", 0),
            "firingOptions", FiringOptions());

        public static Dictionary<string, object> DrawLayer(string dataUrl, int w, int h) => J.O(
            "type", "draw", "name", "Drawing", "image", dataUrl, "width", w, "height", h, "opacity", 1,
            "position", J.O("x", 0, "y", 0), "layer", J.O("size", 1), "firingOptions", FiringOptions());

        public static string LayerType(object layer) => J.Str(layer, "type", "model") ?? "model";

        public static string LayerName(object layer, int index)
        {
            var n = J.Str(layer, "layerName") ?? J.Str(layer, "label");
            if (!string.IsNullOrWhiteSpace(n)) return n;
            switch (LayerType(layer))
            {
                case "image": return J.Str(layer, "name") is string s && s.Length > 0 && s != "Drawing" ? s : "Image " + (index + 1);
                case "shape": return Cap(J.Str(layer, "shape", "shape")) + " " + (index + 1);
                case "text": return "Text “" + Trunc(J.Str(layer, "text", ""), 12) + "”";
                case "draw": return "Drawing " + (index + 1);
                default: return "Crosshair " + (index + 1);
            }
        }

        static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

        /// <summary>Fills in missing fields so older Crosshair X designs (v1 share links) render and edit correctly.</summary>
        public static void NormalizeLayers(List<object> layers)
        {
            if (layers == null) return;
            for (int i = 0; i < layers.Count; i++)
            {
                if (!(layers[i] is Dictionary<string, object> l)) continue;
                var type = LayerType(l);
                if (type == "model")
                {
                    var dot = J.EnsureObj(l, "dot");
                    Fill(dot, "diameter", 0.0); Fill(dot, "opacity", 0.0); Fill(dot, "color", "#FFFFFF"); Fill(dot, "blur", 0.0);
                    if (!dot.ContainsKey("shape")) dot["shape"] = "square";
                    var line = J.EnsureObj(l, "line");
                    Fill(line, "length", 0.0); Fill(line, "thickness", 0.0); Fill(line, "offset", 0.0); Fill(line, "opacity", 0.0);
                    Fill(line, "color", "#FFFFFF"); Fill(line, "shape", "rectangle"); Fill(line, "blur", 0.0); Fill(line, "rotation", 0.0);
                    if (!(line.TryGetValue("visible", out var vis) && vis is List<object>))
                        line["visible"] = J.A("top", "right", "bottom", "left");
                    var outline = J.EnsureObj(l, "outline");
                    Fill(outline, "thickness", 0.0); Fill(outline, "opacity", 0.0); Fill(outline, "color", "#000000"); Fill(outline, "blur", 0.0);
                    var pos = J.EnsureObj(l, "position");
                    Fill(pos, "x", 0.0); Fill(pos, "y", 0.0);
                    var layer = J.EnsureObj(l, "layer");
                    Fill(layer, "size", 1.0);
                    var fo = J.EnsureObj(l, "firingOptions");
                    foreach (var k in new[] { "dot", "line", "outline", "position", "layer" }) J.EnsureObj(fo, k);
                    if (!l.ContainsKey("type")) l["type"] = "model";
                }
                else
                {
                    var pos = J.EnsureObj(l, "position");
                    Fill(pos, "x", 0.0); Fill(pos, "y", 0.0);
                    if (type == "image" || type == "draw")
                    {
                        var layer = J.EnsureObj(l, "layer");
                        Fill(layer, "size", 1.0);
                        Fill(l, "opacity", 1.0);
                        Fill(l, "width", 32.0); Fill(l, "height", 32.0);
                    }
                    if (type == "shape" || type == "text")
                    {
                        var o = J.EnsureObj(l, "outline");
                        Fill(o, "thickness", 0.0); Fill(o, "opacity", 0.0); Fill(o, "color", "#000000"); Fill(o, "blur", 0.0);
                        Fill(l, "opacity", 1.0); Fill(l, "color", "#FFFFFF");
                    }
                    if (type == "shape") { Fill(l, "shape", "square"); Fill(l, "width", 10.0); Fill(l, "height", 10.0); if (!(J.Get(l, "anchor") is Dictionary<string, object>)) l["anchor"] = J.O("x", "center", "y", "center"); }
                    if (type == "text") { Fill(l, "text", "Text"); Fill(l, "fontSize", 12.0); Fill(l, "fontFamily", "Segoe UI"); Fill(l, "fontWeight", 400.0); }
                    J.EnsureObj(l, "firingOptions");
                }
            }
        }

        static void Fill(Dictionary<string, object> d, string key, object def)
        {
            if (!d.TryGetValue(key, out var v) || v == null) d[key] = def;
        }

        public static List<object> NewCrosshairLayers() => new List<object> { ModelLayer() };
    }

    public static class ColorUtil
    {
        /// <summary>Parses the color formats Crosshair X stores: #rgb, #rrggbb, #rrggbbaa, rgb(), rgba(), "r,g,b" and CSS names.</summary>
        public static Color Parse(string s, Color? fallback = null)
        {
            var fb = fallback ?? Color.White;
            if (string.IsNullOrWhiteSpace(s)) return fb;
            s = s.Trim();
            try
            {
                if (s[0] == '#')
                {
                    var h = s.Substring(1);
                    if (h.Length == 3 || h.Length == 4)
                        h = string.Concat(h.Select(c => new string(c, 2)));
                    if (h.Length == 6)
                        return Color.FromArgb(255, Hex(h, 0), Hex(h, 2), Hex(h, 4));
                    if (h.Length == 8)
                        return Color.FromArgb(Hex(h, 6), Hex(h, 0), Hex(h, 2), Hex(h, 4));
                    return fb;
                }
                if (s.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return Color.FromArgb(0, 0, 0, 0);
                string inner = s;
                int p = s.IndexOf('(');
                if (p >= 0) inner = s.Substring(p + 1).TrimEnd(')', ' ');
                if (inner.Contains(","))
                {
                    var parts = inner.Split(',').Select(x => x.Trim()).ToArray();
                    int r = ClampByte(parts[0]), g = ClampByte(parts[1]), b = ClampByte(parts[2]);
                    int a = 255;
                    if (parts.Length > 3 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var ad))
                        a = (int)Math.Round(Math.Max(0, Math.Min(1, ad)) * 255);
                    return Color.FromArgb(a, r, g, b);
                }
                var named = Color.FromName(s);
                if (named.IsKnownColor) return Color.FromArgb(named.A, named.R, named.G, named.B);
            }
            catch { }
            return fb;
        }

        static int Hex(string h, int i) => int.Parse(h.Substring(i, 2), NumberStyles.HexNumber);

        static int ClampByte(string v)
        {
            v = v.Trim();
            bool pct = v.EndsWith("%");
            double d = double.Parse(v.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture);
            if (pct) d = d * 2.55;
            return (int)Math.Max(0, Math.Min(255, Math.Round(d)));
        }

        public static string ToHex(Color c, bool alpha = false) =>
            alpha && c.A != 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        public static Color WithOpacity(Color c, double opacity)
        {
            double a = c.A / 255.0 * Math.Max(0, Math.Min(1, opacity));
            return Color.FromArgb((int)Math.Round(a * 255), c.R, c.G, c.B);
        }

        public static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
            (int)Math.Round(a.A + (b.A - a.A) * t), (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));

        public static bool LooksLikeColor(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            return s.StartsWith("#") || s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase);
        }
    }
}
