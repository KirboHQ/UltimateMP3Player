using System.Numerics;
using System.Runtime.CompilerServices;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Descriptors.Specialized;
using Jint.Runtime.Interop;

namespace UltimateMP3Player.Core.Mods;

// The `ctx` a visualizer draws on: close to HTML's CanvasRenderingContext2D (what most drawing code out there is written
// for), recording into a DrawList. Not there: clearRect (every frame starts empty), shadows, patterns, image data; of the
// filters only blur(). Layers as the canvas' newer beginLayer()/endLayer(): what's drawn inside is put down at once, with the
// alpha, blending and filter there were at beginLayer (a slider body of overlapping strokes fading as one).
// One more property: imageTint (a CSS colour multiplying the pictures drawn, or null). Runs on the mod's thread only.
public sealed class ModCanvasRecorder
{
    private struct State
    {
        public Matrix3x2 M;
        public float Alpha;
        public uint Fill, Stroke;
        public ModGradient? FillGradient, StrokeGradient;
        public JsValue FillValue, StrokeValue;
        public float LineWidth;
        public LineCap Cap;
        public LineJoin Join;
        public float FontSize;
        public bool Bold;
        public string Font;
        public TextAlign Align;
        public TextBaseline Baseline;
        public ModBlend Blend;
        public float Blur;
        public string Filter;
        public uint Tint;
        public int Clips;
        // Made by beginLayer: restore() doesn't go past it, endLayer() closes it.
        public bool Layer;
    }

    private static readonly (string Name, ModBlend Blend)[] Blends =
    {
        ("source-over", ModBlend.SourceOver), ("lighter", ModBlend.Lighter), ("destination-out", ModBlend.DestinationOut),
        ("destination-in", ModBlend.DestinationIn), ("source-atop", ModBlend.SourceAtop), ("multiply", ModBlend.Multiply),
        ("screen", ModBlend.Screen), ("copy", ModBlend.Copy),
    };

    private readonly Engine _e;
    private readonly ModRuntime _mod;
    private State _s;
    private readonly Stack<State> _stack = new();
    private ModPath _path = new();
    private DrawList _list = new();
    private int _commands;
    public const int MaxCommands = 20_000;

    public JsObject Ctx { get; }

    internal ModCanvasRecorder(Engine e, ModRuntime mod)
    {
        _e = e;
        _mod = mod;
        Ctx = new JsObject(e);
        Reset();
        Build();
    }

    private void Reset()
    {
        _s = new State
        {
            M = Matrix3x2.Identity, Alpha = 1, Fill = 0xFF000000, Stroke = 0xFF000000, FillValue = "#000000", StrokeValue = "#000000",
            LineWidth = 1, FontSize = 10, Font = "10px sans-serif", Baseline = TextBaseline.Alphabetic, Filter = "none",
        };
        _stack.Clear();
        _path = new ModPath();
        _origin = Matrix3x2.Identity;
    }

    // Where (0, 0) is for setTransform and resetTransform (ctx.setOrigin, not in browsers): code made for a canvas of its own draws
    // in a part of a bigger one (a "page" visualizer's playfield in the cover's place). Back to the corner at each frame.
    private Matrix3x2 _origin = Matrix3x2.Identity;

    public void Begin(float width, float height, int frame)
    {
        Reset();
        _list = new DrawList { Width = width, Height = height, Frame = frame };
        _commands = 0;
        Ctx.Set("width", width);
        Ctx.Set("height", height);
    }

    public DrawList End()
    {
        // Clips and layers left open are closed, the latest first: the renderers pair every push with a pop.
        while (true)
        {
            while (_s.Clips-- > 0) _list.Cmds.Add(new DrawCmd { Op = DrawOp.ClipPop });
            if (_s.Layer) _list.Cmds.Add(new DrawCmd { Op = DrawOp.LayerPop });
            if (_stack.Count == 0) break;
            _s = _stack.Pop();
        }
        return _list;
    }

    private void Add(DrawCmd c)
    {
        if (++_commands > MaxCommands) throw new JavaScriptException($"A frame can have at most {MaxCommands} drawing commands.");
        _list.Cmds.Add(c);
    }

    private DrawCmd Base(DrawOp op, bool stroke) => new()
    {
        Op = op, Stroke = stroke, M = _s.M, Alpha = _s.Alpha, Blend = _s.Blend, Blur = _s.Blur,
        Color = stroke ? _s.Stroke : _s.Fill, Gradient = stroke ? _s.StrokeGradient : _s.FillGradient,
        LineWidth = _s.LineWidth, Cap = _s.Cap, Join = _s.Join,
    };

    private static float F(JsValue[] a, int i) => i < a.Length ? (float)TypeConverter.ToNumber(a[i]) : 0;
    private static bool Finite(params float[] v) => v.All(float.IsFinite);

    private void Fn(string name, int length, Action<JsValue[]> f) =>
        Ctx.Set(name, new ClrFunction(_e, name, (_, a) => { f(a); return JsValue.Undefined; }, length));

    private void Prop(string name, Func<JsValue> get, Action<JsValue> set) =>
        Ctx.DefineOwnProperty(name, new GetSetPropertyDescriptor(
            new ClrFunction(_e, "get " + name, (_, _) => get()),
            new ClrFunction(_e, "set " + name, (_, a) => { if (a.Length > 0) set(a[0]); return JsValue.Undefined; }),
            enumerable: true, configurable: false));

    // A Path2D, a gradient, a picture: what's behind the script's object (its own only: nothing to forge).
    internal static readonly ConditionalWeakTable<ObjectInstance, ModPath> Paths = new();
    internal static readonly ConditionalWeakTable<ObjectInstance, ModGradient> Gradients = new();
    internal static readonly ConditionalWeakTable<ObjectInstance, ModImage> Images = new();

    private bool Paint(JsValue v, out uint color, out ModGradient? gradient)
    {
        gradient = null;
        color = 0;
        if (v is ObjectInstance o && Gradients.TryGetValue(o, out var g)) { gradient = g; return true; }
        if (CssColor.Parse(v.IsString() ? v.AsString() : null) is { } c) { color = c; return true; }
        return false; // (canvas: a wrong value is ignored)
    }

    private void Build()
    {
        Fn("save", 0, _ => { Push(); _s.Layer = false; });
        Fn("restore", 0, _ =>
        {
            if (_stack.Count == 0 || _s.Layer) return;
            for (int i = 0; i < _s.Clips; i++) Add(new DrawCmd { Op = DrawOp.ClipPop });
            _s = _stack.Pop();
        });
        // A layer begins with the state as it is (a save), its alpha, blending and filter kept for when it's put down; inside, those
        // start again from their defaults.
        Fn("beginLayer", 0, _ =>
        {
            Add(Base(DrawOp.LayerPush, false));
            Push();
            (_s.Layer, _s.Alpha, _s.Blend, _s.Blur, _s.Filter) = (true, 1, ModBlend.SourceOver, 0, "none");
        });
        Fn("endLayer", 0, _ =>
        {
            if (!_s.Layer && !_stack.Any(s => s.Layer)) return;
            while (true)
            {
                for (int i = 0; i < _s.Clips; i++) Add(new DrawCmd { Op = DrawOp.ClipPop });
                bool layer = _s.Layer;
                _s = _stack.Pop();
                if (layer) { Add(new DrawCmd { Op = DrawOp.LayerPop }); return; }
            }
        });
        Fn("translate", 2, a => { var (x, y) = (F(a, 0), F(a, 1)); if (Finite(x, y)) _s.M = Matrix3x2.CreateTranslation(x, y) * _s.M; });
        Fn("rotate", 1, a => { var r = F(a, 0); if (Finite(r)) _s.M = Matrix3x2.CreateRotation(r) * _s.M; });
        Fn("scale", 2, a => { var (x, y) = (F(a, 0), F(a, 1)); if (Finite(x, y)) _s.M = Matrix3x2.CreateScale(x, y) * _s.M; });
        Fn("transform", 6, a =>
        {
            var m = new Matrix3x2(F(a, 0), F(a, 1), F(a, 2), F(a, 3), F(a, 4), F(a, 5));
            if (Finite(m.M11, m.M12, m.M21, m.M22, m.M31, m.M32)) _s.M = m * _s.M;
        });
        Fn("setTransform", 6, a =>
        {
            var m = new Matrix3x2(F(a, 0), F(a, 1), F(a, 2), F(a, 3), F(a, 4), F(a, 5));
            if (a.Length == 0) m = Matrix3x2.Identity;
            if (Finite(m.M11, m.M12, m.M21, m.M22, m.M31, m.M32)) _s.M = m * _origin;
        });
        Fn("resetTransform", 0, _ => _s.M = _origin);
        Fn("setOrigin", 2, a =>
        {
            var (x, y) = (F(a, 0), F(a, 1));
            if (!Finite(x, y)) return;
            _origin = Matrix3x2.CreateTranslation(x, y);
            _s.M = _origin;
        });

        // The current path.
        Fn("beginPath", 0, _ => _path = new ModPath());
        Fn("moveTo", 2, a => { if (Finite(F(a, 0), F(a, 1))) _path.MoveTo(F(a, 0), F(a, 1)); });
        Fn("lineTo", 2, a => { if (Finite(F(a, 0), F(a, 1))) _path.LineTo(F(a, 0), F(a, 1)); });
        Fn("quadraticCurveTo", 4, a => _path.QuadTo(F(a, 0), F(a, 1), F(a, 2), F(a, 3)));
        Fn("bezierCurveTo", 6, a => _path.CubicTo(F(a, 0), F(a, 1), F(a, 2), F(a, 3), F(a, 4), F(a, 5)));
        Fn("arc", 6, a => _path.Ellipse(F(a, 0), F(a, 1), F(a, 2), F(a, 2), 0, F(a, 3), F(a, 4), a.Length > 5 && TypeConverter.ToBoolean(a[5])));
        Fn("ellipse", 8, a => _path.Ellipse(F(a, 0), F(a, 1), F(a, 2), F(a, 3), F(a, 4), F(a, 5), F(a, 6), a.Length > 7 && TypeConverter.ToBoolean(a[7])));
        Fn("rect", 4, a => _path.Rect(F(a, 0), F(a, 1), F(a, 2), F(a, 3)));
        Fn("roundRect", 5, a => _path.RoundRect(F(a, 0), F(a, 1), F(a, 2), F(a, 3), a.Length > 4 && a[4].IsNumber() ? F(a, 4) : 0));
        Fn("closePath", 0, _ => _path.Close());
        ModPath PathOf(JsValue[] a) => a.Length > 0 && a[0] is ObjectInstance o && Paths.TryGetValue(o, out var p) ? p : _path;
        Fn("fill", 1, a => { var c = Base(DrawOp.Path, false); c.Path = PathOf(a); Add(c); });
        Fn("stroke", 1, a => { var c = Base(DrawOp.Path, true); c.Path = PathOf(a); Add(c); });
        Fn("clip", 1, a =>
        {
            var c = Base(DrawOp.ClipPush, false);
            c.Path = PathOf(a);
            Add(c);
            _s.Clips++;
        });
        Fn("fillRect", 4, a => Rect(a, false));
        Fn("strokeRect", 4, a => Rect(a, true));
        Fn("clearRect", 4, _ => { }); // every frame starts empty

        Fn("drawImage", 9, a =>
        {
            if (a.Length < 3 || a[0] is not ObjectInstance o || !Images.TryGetValue(o, out var img)) return;
            var c = Base(DrawOp.Image, false);
            c.Image = img;
            c.Tint = _s.Tint;
            if (a.Length >= 9) (c.SX, c.SY, c.SW, c.SH, c.X, c.Y, c.W, c.H) = (F(a, 1), F(a, 2), F(a, 3), F(a, 4), F(a, 5), F(a, 6), F(a, 7), F(a, 8));
            else if (a.Length >= 5) (c.X, c.Y, c.W, c.H) = (F(a, 1), F(a, 2), F(a, 3), F(a, 4));
            else (c.X, c.Y, c.W, c.H) = (F(a, 1), F(a, 2), img.Width, img.Height);
            if (!Finite(c.X, c.Y, c.W, c.H, c.SX, c.SY, c.SW, c.SH) || c.W == 0 || c.H == 0) return;
            Add(c);
        });

        Fn("fillText", 3, a => Text(a, false));
        Fn("strokeText", 3, a => Text(a, true));
        Ctx.Set("measureText", new ClrFunction(_e, "measureText", (_, a) =>
        {
            var text = a.Length > 0 ? TypeConverter.ToString(a[0]) : "";
            var r = new JsObject(_e);
            r.Set("width", _mod.MeasureText(text, _s.FontSize, _s.Bold));
            return r;
        }, 1));

        Ctx.Set("createLinearGradient", new ClrFunction(_e, "createLinearGradient", (_, a) =>
            Gradient(new ModGradient { X0 = F(a, 0), Y0 = F(a, 1), X1 = F(a, 2), Y1 = F(a, 3) }), 4));
        Ctx.Set("createRadialGradient", new ClrFunction(_e, "createRadialGradient", (_, a) =>
            Gradient(new ModGradient { Radial = true, X0 = F(a, 0), Y0 = F(a, 1), R0 = F(a, 2), X1 = F(a, 3), Y1 = F(a, 4), R1 = F(a, 5) }), 6));

        Prop("fillStyle", () => _s.FillValue, v => { if (Paint(v, out var c, out var g)) (_s.Fill, _s.FillGradient, _s.FillValue) = (c, g, v); });
        Prop("strokeStyle", () => _s.StrokeValue, v => { if (Paint(v, out var c, out var g)) (_s.Stroke, _s.StrokeGradient, _s.StrokeValue) = (c, g, v); });
        Prop("lineWidth", () => _s.LineWidth, v => { var w = (float)TypeConverter.ToNumber(v); if (float.IsFinite(w) && w > 0) _s.LineWidth = w; });
        Prop("lineCap", () => _s.Cap.ToString().ToLowerInvariant(), v => _s.Cap = TypeConverter.ToString(v) switch { "round" => LineCap.Round, "square" => LineCap.Square, "butt" => LineCap.Butt, _ => _s.Cap });
        Prop("lineJoin", () => _s.Join.ToString().ToLowerInvariant(), v => _s.Join = TypeConverter.ToString(v) switch { "round" => LineJoin.Round, "bevel" => LineJoin.Bevel, "miter" => LineJoin.Miter, _ => _s.Join });
        Prop("globalAlpha", () => _s.Alpha, v => { var x = (float)TypeConverter.ToNumber(v); if (x is >= 0 and <= 1) _s.Alpha = x; });
        Prop("globalCompositeOperation", () => Blends.First(b => b.Blend == _s.Blend).Name, v =>
        {
            // (canvas: a name it doesn't know is ignored)
            var name = TypeConverter.ToString(v);
            foreach (var b in Blends)
                if (b.Name == name) _s.Blend = b.Blend;
        });
        Prop("filter", () => _s.Filter, v =>
        {
            // "none" or "blur(4px)"; any other filter is ignored, as a canvas does with one it can't read.
            var text = TypeConverter.ToString(v).Trim();
            if (text == "none") { (_s.Blur, _s.Filter) = (0, text); return; }
            var m = System.Text.RegularExpressions.Regex.Match(text, @"^blur\(\s*(\d+(?:\.\d+)?)(?:px)?\s*\)$");
            if (!m.Success) return;
            _s.Blur = Math.Min(200, float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            _s.Filter = text;
        });
        Prop("font", () => _s.Font, v =>
        {
            // "bold 16px sans-serif": the size and whether it's bold (the family is the app's).
            var text = TypeConverter.ToString(v);
            var px = System.Text.RegularExpressions.Regex.Match(text, @"(\d+(?:\.\d+)?)px");
            if (!px.Success) return;
            _s.FontSize = Math.Clamp(float.Parse(px.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 1, 1000);
            _s.Bold = text.Contains("bold") || System.Text.RegularExpressions.Regex.IsMatch(text, @"\b[6-9]00\b");
            _s.Font = text;
        });
        Prop("textAlign", () => _s.Align switch { TextAlign.Center => "center", TextAlign.Right => "right", _ => "left" }, v => _s.Align = TypeConverter.ToString(v) switch
        {
            "center" => TextAlign.Center,
            "right" or "end" => TextAlign.Right,
            "left" or "start" => TextAlign.Left,
            _ => _s.Align,
        });
        Prop("textBaseline", () => _s.Baseline.ToString().ToLowerInvariant(), v => _s.Baseline = TypeConverter.ToString(v) switch
        {
            "top" or "hanging" => TextBaseline.Top,
            "middle" => TextBaseline.Middle,
            "bottom" or "ideographic" => TextBaseline.Bottom,
            "alphabetic" => TextBaseline.Alphabetic,
            _ => _s.Baseline,
        });
        Prop("imageTint", () => _s.Tint == 0 ? JsValue.Null : CssColor.Of(_s.Tint), v => _s.Tint = v.IsNull() || v.IsUndefined() ? 0 : CssColor.Parse(TypeConverter.ToString(v)) ?? _s.Tint);
    }

    private void Push()
    {
        _stack.Push(_s);
        _s.Clips = 0;
        if (_stack.Count > 512) throw new JavaScriptException("save() or beginLayer() without restore() or endLayer(), too many times.");
    }

    private void Rect(JsValue[] a, bool stroke)
    {
        var c = Base(DrawOp.Rect, stroke);
        (c.X, c.Y, c.W, c.H) = (F(a, 0), F(a, 1), F(a, 2), F(a, 3));
        if (Finite(c.X, c.Y, c.W, c.H)) Add(c);
    }

    private void Text(JsValue[] a, bool stroke)
    {
        if (a.Length < 3) return;
        var c = Base(DrawOp.Text, stroke);
        c.Text = TypeConverter.ToString(a[0]);
        if (c.Text.Length > 2000) c.Text = c.Text[..2000];
        (c.X, c.Y) = (F(a, 1), F(a, 2));
        (c.FontSize, c.Bold, c.Align, c.Baseline) = (_s.FontSize, _s.Bold, _s.Align, _s.Baseline);
        if (c.Text.Length > 0 && Finite(c.X, c.Y)) Add(c);
    }

    private JsValue Gradient(ModGradient g)
    {
        var o = new JsObject(_e);
        o.Set("addColorStop", new ClrFunction(_e, "addColorStop", (_, a) =>
        {
            var at = a.Length > 0 ? (float)TypeConverter.ToNumber(a[0]) : float.NaN;
            if (at is < 0 or > 1 || float.IsNaN(at)) throw new JavaScriptException("addColorStop: the offset goes from 0 to 1.");
            if (CssColor.Parse(a.Length > 1 ? TypeConverter.ToString(a[1]) : null) is not { } color) throw new JavaScriptException("addColorStop: not a colour.");
            if (g.Stops.Count >= 64) return JsValue.Undefined;
            g.Stops.Add((at, color));
            g.Stops.Sort((x, y) => x.Offset.CompareTo(y.Offset));
            g.Version++;
            return JsValue.Undefined;
        }, 2));
        Gradients.Add(o, g);
        return o;
    }
}
