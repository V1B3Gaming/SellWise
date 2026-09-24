using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using SellWise.Core;

namespace SellWise.Windows;

/// <summary>SellWise's look: graphite surfaces, one accent colour, square corners, outlined status tags.</summary>
public static class Theme
{
    private static Vector4 Hex(uint rgb, float a = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    // Surfaces
    public static readonly Vector4 Bg0 = Hex(0x0b0b0c);
    public static readonly Vector4 Bg1 = Hex(0x121213);
    public static readonly Vector4 Bg2 = Hex(0x18181a);
    public static readonly Vector4 Panel = Hex(0x151516);
    public static readonly Vector4 Panel2 = Hex(0x1c1c1e);
    public static readonly Vector4 Field = Hex(0x0f0f10);
    public static readonly Vector4 Raise = Hex(0x212124);
    public static readonly Vector4 Raise2 = Hex(0x2a2a2e);
    public static readonly Vector4 Line = Hex(0x27272a);
    public static readonly Vector4 Line3 = Hex(0x45454b);

    // Text (bright enough to read on the graphite surfaces)
    public static readonly Vector4 Text = Hex(0xf7f7f8);
    public static readonly Vector4 Text2 = Hex(0xcfcfd6);
    public static readonly Vector4 Text3 = Hex(0xa8a8b2);

    // Status colours
    public static readonly Vector4 Good = Hex(0x86d994);
    public static readonly Vector4 Slow = Hex(0xcdd86f);
    public static readonly Vector4 Hold = Hex(0xf2ab62);
    public static readonly Vector4 Vendor = Hex(0x7cb8f2);
    public static readonly Vector4 Bad = Hex(0xf47d73);
    public static readonly Vector4 Gather = Hex(0x72d3c3);
    public static readonly Vector4 OkBg = Hex(0x121a14);
    public static readonly Vector4 OkLine = Hex(0x26402d);

    public sealed record Accent(string Name, Vector4 Color, Vector4 On, Vector4 Soft, Vector4 LineColor, Vector4 Selected);

    public static readonly Accent[] Accents =
    [
        new("violet", Hex(0xa78bfa), Hex(0x140c2b), Hex(0xa78bfa, 0.14f), Hex(0x4c3f7a), Hex(0x1b1729)),
        new("teal", Hex(0x2dd4bf), Hex(0x032420), Hex(0x2dd4bf, 0.13f), Hex(0x1f5c54), Hex(0x122220)),
        new("silver", Hex(0xe4e4e7), Hex(0x121214), Hex(0xe4e4e7, 0.10f), Hex(0x4b4b52), Hex(0x1e1e21)),
    ];

    public static Accent Current { get; private set; } = Accents[0];

    public static void SetAccent(string name)
        => Current = Array.Find(Accents, a => a.Name == name) ?? Accents[0];

    private static IFontHandle? headingFont;
    private static IFontHandle? bigFont;

    /// <summary>Game fonts for headings and big prices; created once and disposed with the plugin.</summary>
    public static void InitFonts(IUiBuilder ui)
    {
        headingFont = ui.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis14));
        bigFont = ui.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis18));
    }

    public static void DisposeFonts()
    {
        headingFont?.Dispose();
        bigFont?.Dispose();
    }

    public static IDisposable? HeadingFont() => headingFont?.Push();
    public static IDisposable? BigFont() => bigFont?.Push();

    /// <summary>
    /// Pushes colours and corner rounding for a window (safe before Begin: none of it moves the title bar buttons).
    /// Pair with <see cref="PushLayout"/> inside Draw. Dispose to pop.
    /// </summary>
    public static IDisposable Push()
    {
        var a = Current;
        var colors = ImRaii.PushColor(ImGuiCol.WindowBg, Bg1)
            .Push(ImGuiCol.ChildBg, Vector4.Zero)
            .Push(ImGuiCol.PopupBg, Bg2)
            .Push(ImGuiCol.Border, Line)
            .Push(ImGuiCol.Text, Text)
            .Push(ImGuiCol.TextDisabled, Text3)
            .Push(ImGuiCol.TitleBg, Bg2)
            .Push(ImGuiCol.TitleBgActive, Bg2)
            .Push(ImGuiCol.TitleBgCollapsed, Bg2)
            .Push(ImGuiCol.FrameBg, Field)
            .Push(ImGuiCol.FrameBgHovered, Raise)
            .Push(ImGuiCol.FrameBgActive, Raise2)
            .Push(ImGuiCol.Button, Raise)
            .Push(ImGuiCol.ButtonHovered, Raise2)
            .Push(ImGuiCol.ButtonActive, a.LineColor)
            .Push(ImGuiCol.Header, a.Selected)
            .Push(ImGuiCol.HeaderHovered, Raise)
            .Push(ImGuiCol.HeaderActive, a.LineColor)
            .Push(ImGuiCol.CheckMark, a.Color)
            .Push(ImGuiCol.SliderGrab, a.Color)
            .Push(ImGuiCol.SliderGrabActive, a.Color)
            .Push(ImGuiCol.Separator, Line)
            .Push(ImGuiCol.ScrollbarBg, Bg1)
            .Push(ImGuiCol.ScrollbarGrab, Raise2)
            .Push(ImGuiCol.ScrollbarGrabHovered, Line3)
            .Push(ImGuiCol.PlotHistogram, a.Color)
            .Push(ImGuiCol.TableHeaderBg, Bg2)
            .Push(ImGuiCol.TableRowBg, Vector4.Zero)
            .Push(ImGuiCol.TableRowBgAlt, Panel)
            .Push(ImGuiCol.TableBorderLight, Line)
            .Push(ImGuiCol.TableBorderStrong, Line)
            .Push(ImGuiCol.Tab, Bg2)
            .Push(ImGuiCol.TabHovered, Raise)
            .Push(ImGuiCol.TabActive, a.Selected);

        var style = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, 4f)
            .Push(ImGuiStyleVar.ChildRounding, 3f)
            .Push(ImGuiStyleVar.FrameRounding, 3f)
            .Push(ImGuiStyleVar.PopupRounding, 3f)
            .Push(ImGuiStyleVar.GrabRounding, 2f)
            .Push(ImGuiStyleVar.ScrollbarRounding, 2f)
            .Push(ImGuiStyleVar.TabRounding, 3f);

        return new Popper(colors, style);
    }

    /// <summary>
    /// Spacing and padding for a window's contents. Push it inside Draw, not before Begin: the title bar's
    /// collapse/close buttons and Dalamud's own title-bar buttons are laid out from FramePadding, and changing it
    /// before Begin leaves their hit areas out of line with where they're drawn.
    /// </summary>
    public static IDisposable PushLayout()
        => ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(10, 6))
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(8, 7))
            .Push(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));

    private sealed class Popper(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            for (var i = items.Length - 1; i >= 0; i--) items[i].Dispose();
        }
    }

    public static uint U32(Vector4 c) => ImGui.GetColorU32(c);

    // ---- Small drawing helpers ------------------------------------------------------------------

    /// <summary>A square-cornered outlined label, e.g. a verdict.</summary>
    public static void Tag(string text, Vector4 color, bool small = false)
    {
        var pad = small ? new Vector2(5, 1) : new Vector2(7, 2);
        var size = ImGui.CalcTextSize(text) + pad * 2;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRect(pos, pos + size, U32(color with { W = 0.85f }), 2f);
        dl.AddText(pos + pad, U32(color), text);
        ImGui.Dummy(size);
    }

    /// <summary>A game icon at a given size (HQ variant when requested).</summary>
    public static void Icon(ushort iconId, bool hq, float size)
    {
        var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId, hq)).GetWrapOrEmpty();
        ImGui.Image(tex.Handle, new Vector2(size));
    }

    /// <summary>Filled background with a hairline border behind a rectangle already laid out.</summary>
    public static void Box(Vector2 min, Vector2 max, Vector4 fill, Vector4 border)
    {
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(min, max, U32(fill), 3f);
        dl.AddRect(min, max, U32(border), 3f);
    }

    /// <summary>A small stat tile: muted label over a value.</summary>
    public static void Tile(string label, string value, float width, Vector4? valueColor = null)
    {
        var pos = ImGui.GetCursorScreenPos();
        var height = ImGui.GetTextLineHeight() * 2 + 16;
        Box(pos, pos + new Vector2(width, height), Panel2, Panel2);
        var dl = ImGui.GetWindowDrawList();
        dl.AddText(pos + new Vector2(10, 7), U32(Text3), Fit(label, width - 20));
        dl.AddText(pos + new Vector2(10, 9 + ImGui.GetTextLineHeight()), U32(valueColor ?? Text), Fit(value, width - 20));
        ImGui.Dummy(new Vector2(width, height));
    }

    /// <summary>A thin progress bar.</summary>
    public static void Bar(float fraction, Vector4 color, Vector2 size)
    {
        var pos = ImGui.GetCursorScreenPos() + new Vector2(0, (ImGui.GetTextLineHeight() - size.Y) / 2);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + size, U32(Raise2), 2f);
        dl.AddRectFilled(pos, pos + new Vector2(size.X * Math.Clamp(fraction, 0, 1), size.Y), U32(color), 2f);
        ImGui.Dummy(new Vector2(size.X, ImGui.GetTextLineHeight()));
    }

    /// <summary>A button filled with the accent colour, for the one main action on a screen.</summary>
    public static bool PrimaryButton(string label, Vector2 size = default)
    {
        var a = Current;
        using var c = ImRaii.PushColor(ImGuiCol.Button, a.Color)
            .Push(ImGuiCol.ButtonHovered, a.Color with { W = 0.85f })
            .Push(ImGuiCol.ButtonActive, a.Color with { W = 0.7f })
            .Push(ImGuiCol.Text, a.On);
        return ImGui.Button(label, size);
    }

    /// <summary>An icon-font glyph as text.</summary>
    public static void Glyph(FontAwesomeIcon icon, Vector4 color)
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            ImGui.TextColored(color, icon.ToIconString());
    }

    public static Vector2 GlyphSize(FontAwesomeIcon icon)
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            return ImGui.CalcTextSize(icon.ToIconString());
    }

    public static void Muted(string text) => ImGui.TextColored(Text3, text);
    public static void Secondary(string text) => ImGui.TextColored(Text2, text);

    public static void Wrapped(string text, Vector4 color)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
    }

    public static (string Label, Vector4 Color) Describe(Verdict v) => v switch
    {
        Verdict.List => ("List", Good),
        Verdict.ListSlow => ("Slow", Slow),
        Verdict.Hold => ("Hold", Hold),
        Verdict.Vendor => ("Vendor", Vendor),
        Verdict.NoData => ("No data", Text3),
        Verdict.Untradable => ("Untradable", Text3),
        Verdict.NotMarketable => ("Not on MB", Text3),
        Verdict.Relist => ("Undercut", Bad),
        Verdict.Raise => ("Raise price", Hold),
        Verdict.ListingOk => ("OK", Good),
        _ => (v.ToString(), Text3),
    };

    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalSeconds < 0 || utc == DateTime.MinValue) return "unknown";
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }

    /// <summary>Shortens big gil amounts for tight spaces: 1,284,300 -> 1.28M.</summary>
    public static string Short(double gil) => Math.Abs(gil) switch
    {
        >= 10_000_000 => $"{gil / 1_000_000:0.#}M",
        >= 1_000_000 => $"{gil / 1_000_000:0.##}M",
        >= 100_000 => $"{gil / 1000:0}k",
        _ => gil.ToString("N0"),
    };

    public static IEnumerable<T> Each<T>(params T[] items) => items;

    /// <summary>Cuts text down (with "...") so it fits in <paramref name="maxWidth"/> pixels. Keeps columns from spilling into each other.</summary>
    public static string Fit(string text, float maxWidth)
    {
        if (maxWidth <= 0) return "";
        if (ImGui.CalcTextSize(text).X <= maxWidth) return text;
        const string dots = "...";
        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid] + dots).X <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return lo == 0 ? dots : text[..lo].TrimEnd() + dots;
    }

    /// <summary>Short counts for tight spaces: 950, 5.8k, 12k, 1.2M.</summary>
    public static string Compact(long n) => Math.Abs(n) switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 10_000 => $"{n / 1000.0:0}k",
        >= 1_000 => $"{n / 1000.0:0.#}k",
        _ => n.ToString(),
    };
}
