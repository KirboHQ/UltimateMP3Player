using System.Text.Json;

namespace UltimateMP3Player.Core;

// The bars of the music and the song's bar, per profile (5.4). AppSettings keeps the ones in use (every view reads them there); the
// profile keeps its own and puts them back there when it opens (Open). A profile from before keeps the device's ones.
public sealed class BarsLook
{
    public bool Visualizer { get; set; } = true;
    public VisualizerFlow Flow { get; set; } = VisualizerFlow.MirrorSides;
    public VisualizerPlace Place { get; set; } = VisualizerPlace.Bottom;
    public VisualizerStyle Style { get; set; } = VisualizerStyle.Bars;
    public VisualizerStyle SeekStyle { get; set; } = VisualizerStyle.Bars;
    public bool SeekMirrored { get; set; } = true;
    public VisualizerTint Tint { get; set; } = VisualizerTint.Cover;
    public int Thickness { get; set; } = 1;
    public double Height { get; set; } = 0.38;
    public double Strength { get; set; } = 1;

    public static BarsLook From(AppSettings s) => new()
    {
        Visualizer = s.Visualizer, Flow = s.VisualizerFlow, Place = s.VisualizerPlace, Style = s.VisualizerStyle, SeekStyle = s.SeekStyle,
        SeekMirrored = s.SeekMirrored, Tint = s.VisualizerTint, Thickness = s.VisualizerThickness, Height = s.VisualizerHeight, Strength = s.VisualizerStrength,
    };

    public void ApplyTo(AppSettings s)
    {
        (s.Visualizer, s.VisualizerFlow, s.VisualizerPlace, s.VisualizerStyle, s.SeekStyle) = (Visualizer, Flow, Place, Style, SeekStyle);
        (s.SeekMirrored, s.VisualizerTint, s.VisualizerThickness, s.VisualizerHeight, s.VisualizerStrength) = (SeekMirrored, Tint, Thickness, Height, Strength);
    }

    // A profile opening: its bars in use; the first time, it takes the device's. True when the profile changed (to be saved).
    public static bool Open(ProfileData d, AppSettings s)
    {
        if (d.Bars is { } mine)
        {
            mine.ApplyTo(s);
            return false;
        }
        d.Bars = From(s);
        return true;
    }
}

// A look with a name (Settings › Appearance › Looks): the theme and its colour, light or dark, the glass and its picture, the bars,
// the layout, all together. Each profile has its own (ProfileData.Looks). The one chosen (ProfileData.LookId) is the profile's look
// as it is now: it's taken back into its preset only when another one is chosen, copied or sent away (Take), so a change in the
// settings never has to know about presets. Its picture is a file of the profile's ("looks/<id>.jpg").
public sealed class LookPreset
{
    public const string Extension = ".umplook";

    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "";
    public string Theme { get; set; } = ThemeColors.DynamicId;
    public double RgbSpeed { get; set; } = 1;
    public string CustomColor { get; set; } = "#7C5CFF";
    public string Appearance { get; set; } = ThemeColors.System;
    public GlassLook Glass { get; set; } = new();
    public LayoutLook Layout { get; set; } = new();
    public BarsLook Bars { get; set; } = new();

    public static string PictureOf(string profileId, string id) => Path.Combine(AppPaths.ProfileDir(profileId), "looks", id + ".jpg");

    // The profile's look as it is now, as a preset (without its picture).
    public static LookPreset Snap(ProfileData d, AppSettings s, string name) => new()
    {
        Name = name, Theme = d.Theme, RgbSpeed = d.RgbSpeed, CustomColor = d.CustomColor, Appearance = d.Appearance,
        Glass = Copy(d.Glass), Layout = Copy(d.Layout), Bars = BarsLook.From(s),
    };

    // The profile's look as it is now, into this preset (its picture copied).
    public void Take(ProfileData d, AppSettings s, string profileId, string backdrop)
    {
        var now = Snap(d, s, Name);
        (Theme, RgbSpeed, CustomColor, Appearance, Glass, Layout, Bars) = (now.Theme, now.RgbSpeed, now.CustomColor, now.Appearance, now.Glass, now.Layout, now.Bars);
        var picture = PictureOf(profileId, Id);
        try
        {
            if (Glass.Picture && File.Exists(backdrop))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(picture)!);
                File.Copy(backdrop, picture, true);
            }
            else if (File.Exists(picture)) File.Delete(picture);
        }
        catch (Exception ex) { AppPaths.AppendLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] look picture: {ex}\n\n"); }
    }

    // This preset becomes the profile's look (the caller applies it: theme, glass, bars).
    public void Give(ProfileData d, AppSettings s, string profileId, string backdrop)
    {
        (d.Theme, d.RgbSpeed, d.CustomColor, d.Appearance) = (Theme, RgbSpeed, CustomColor, Appearance);
        d.Glass = Copy(Glass);
        d.Layout = Copy(Layout);
        d.Bars = Copy(Bars);
        d.Bars.ApplyTo(s);
        var picture = PictureOf(profileId, Id);
        try
        {
            if (File.Exists(picture)) File.Copy(picture, backdrop, true);
            // (its picture lost: the cover)
            else d.Glass.Picture = false;
        }
        catch (Exception ex) { AppPaths.AppendLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] look picture: {ex}\n\n"); }
    }

    // Another preset with the same look, and its own picture.
    public LookPreset Clone(string profileId, string name)
    {
        var copy = Copy(this);
        copy.Id = Ids.New();
        copy.Name = name;
        try
        {
            if (File.Exists(PictureOf(profileId, Id))) File.Copy(PictureOf(profileId, Id), PictureOf(profileId, copy.Id), true);
        }
        catch { }
        return copy;
    }

    public void DeletePicture(string profileId)
    {
        try { File.Delete(PictureOf(profileId, Id)); } catch { }
    }

    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonStore.Options), JsonStore.Options)!;

    // ---- the file: the look and its picture, for another profile or device

    private sealed class LookFile
    {
        public int Version { get; set; } = 1;
        public LookPreset? Look { get; set; }
        public byte[]? Picture { get; set; }
    }

    public string ToFile(string profileId)
    {
        var picture = PictureOf(profileId, Id);
        return JsonSerializer.Serialize(new LookFile { Look = this, Picture = File.Exists(picture) ? File.ReadAllBytes(picture) : null }, JsonStore.Options);
    }

    // A look's file, or the look inside a file of settings (SettingsFile): a new preset of this profile (a new id, its picture in place).
    public static LookPreset? Read(string path, string profileId)
    {
        var json = File.ReadAllText(path);
        LookPreset? look;
        byte[]? bytes;
        if (SettingsFile.TryRead(json) is { } settings)
            (look, bytes) = (settings.Look, settings.Picture);
        else
        {
            var file = JsonSerializer.Deserialize<LookFile>(json, JsonStore.Options);
            (look, bytes) = (file?.Look, file?.Picture);
        }
        if (look == null) return null;
        look.Id = Ids.New();
        if (string.IsNullOrWhiteSpace(look.Name)) look.Name = Path.GetFileNameWithoutExtension(path);
        if (bytes is { Length: > 0 })
        {
            var picture = PictureOf(profileId, look.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(picture)!);
            File.WriteAllBytes(picture, bytes);
        }
        else look.Glass.Picture = false;
        return look;
    }
}
