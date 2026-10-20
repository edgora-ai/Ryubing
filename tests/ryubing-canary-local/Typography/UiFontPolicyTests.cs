using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using NUnit.Framework;
using Ryujinx.Ava.UI;
using Ryujinx.Ava.UI.Helpers;
using System;
using System.Linq;
using System.Text;

namespace Ryubing.Canary.Local.Tests.Typography;

[TestFixture]
[NonParallelizable]
public class UiFontPolicyTests
{
    // Preserve and exercise the production resource URIs, not replacement test font assets.
    private const string MonoFamily = "avares://Ryujinx/Assets/Fonts/Mono/#JetBrains Mono";
    private const string IconFamily = "avares://Ryujinx/Assets/Fonts#Segoe Fluent Icons";

    [Test]
    public void MacOSFallbacksDoNotReplaceLatinDefaultsOrMapEmbeddedFamilies()
    {
        FontManagerOptions options = UiFontPolicy.CreateMacOSFontManagerOptions();
        Assert.Multiple(() =>
        {
            Assert.That(options.DefaultFamilyName, Is.Null);
            Assert.That(options.FontFamilyMappings, Is.Null);
            Assert.That(options.FontFallbacks.Select(fallback => fallback.FontFamily.Name), Is.EqualTo(new[]
            {
                "PingFang SC", "PingFang TC", "Heiti SC", "Heiti TC", "Arial Unicode MS",
            }));
        });
    }

    [Test]
    public void CjkRangesIncludeHanAndPunctuationButExcludeLatinEmojiAndPrivateUseIcons()
    {
        int[] included = { '中', '文', '设', '置', '設', '定', '檔', '體', '：', '、', '（', '）', 0x3105, 0x20000 };
        int[] excluded = { 'A', 'i', 'W', '0', '_', ',', '(', ')', 0x0410, 0x0627, 0x1f600, 0xe7f7, 0xebe8, 59748 };
        foreach (int codepoint in included)
        {
            Assert.That(UiFontPolicy.IsCjkCodepoint(codepoint), Is.True, $"U+{codepoint:X}");
            Assert.That(UiFontPolicy.CreateMacOSFontManagerOptions().FontFallbacks
                .All(fallback => fallback.UnicodeRange.IsInRange(codepoint)), Is.True);
        }
        foreach (int codepoint in excluded)
        {
            Assert.That(UiFontPolicy.IsCjkCodepoint(codepoint), Is.False, $"U+{codepoint:X}");
            Assert.That(UiFontPolicy.CreateMacOSFontManagerOptions().FontFallbacks
                .Any(fallback => fallback.UnicodeRange.IsInRange(codepoint)), Is.False);
        }
    }

    [Test]
    public void EveryBuilderGetsIndependentOptionsAndFallbackEntries()
    {
        FontManagerOptions first = UiFontPolicy.CreateMacOSFontManagerOptions();
        first.FontFallbacks[0].FontFamily = new FontFamily("Test mutation");
        FontManagerOptions second = UiFontPolicy.CreateMacOSFontManagerOptions();
        Assert.That(second.FontFallbacks[0].FontFamily.Name, Is.EqualTo("PingFang SC"));
        Assert.That(first, Is.Not.SameAs(second));
        Assert.That(first.FontFallbacks[0], Is.Not.SameAs(second.FontFallbacks[0]));
    }

    [Test]
    public void PlatformOptionsAddOverridesOnlyOnMacOS()
    {
        FontManagerOptions options = UiFontPolicy.CreateFontManagerOptions();
        Assert.That(options.FontFallbacks?.Count ?? 0, Is.EqualTo(OperatingSystem.IsMacOS() ? 5 : 0));
        Assert.That(options.DefaultFamilyName, Is.Null);
        Assert.That(options.FontFamilyMappings, Is.Null);
    }

    [AvaloniaTest]
    public void HarnessUsesActualSkiaFontTablesInsteadOfHeadlessDrawingStubs()
    {
        GlyphTypeface face = Typeface.Default.GlyphTypeface;
        Assert.That(face.PlatformTypeface.GetType().Assembly.GetName().Name, Is.EqualTo("Avalonia.Skia"));
        Assert.That(face.CharacterToGlyphMap.GetGlyph('A'), Is.Not.Zero);
        Assert.That(face.GlyphCount, Is.GreaterThan(0));
    }

    [AvaloniaTest]
    public void SimplifiedTraditionalAndMixedPunctuationHaveRealRegularAndBoldGlyphs()
    {
        RequireMacOS();
        foreach (string text in new[] { "设置：Switch 123（简体中文）", "設定：Switch 123（繁體中文檔案）" })
        {
            foreach (FontWeight weight in new[] { FontWeight.Normal, FontWeight.Bold })
            {
                using TextLayout layout = new(text, new Typeface(FontFamily.Default, weight: weight), 16, Brushes.Black);
                AssertComplete(layout, text);
            }
        }
    }

    [AvaloniaTest]
    public void CjkFallsBackFromTheEmbeddedMonoWithoutReplacingItsLatinFace()
    {
        RequireMacOS();
        foreach (FontWeight weight in new[] { FontWeight.Normal, FontWeight.Bold })
        {
            Typeface requested = new(new FontFamily(MonoFamily), weight: weight);
            GlyphTypeface mono = requested.GlyphTypeface;
            Assert.That(mono.FamilyName, Does.StartWith("JetBrains Mono"));
            Assert.That(mono.CharacterToGlyphMap.GetGlyph('中'), Is.Zero, "The real primary must lack Chinese glyphs.");
            foreach (string text in new[] { "设置：Switch 123（中文）", "設定：Switch 123（檔案）" })
            {
                using TextLayout layout = new(text, requested, 16, Brushes.Black);
                ShapedTextRun[] runs = AssertComplete(layout, text);
                Assert.That(runs.Where(run => run.Text.ToString().Any(character => character is > ' ' and <= '\x7f'))
                    .All(run => run.GlyphRun.GlyphTypeface.FamilyName == mono.FamilyName), Is.True);
                Assert.That(runs.Where(run => run.Text.ToString().EnumerateRunes().Any(rune => UiFontPolicy.IsCjkCodepoint(rune.Value)))
                    .All(run => run.GlyphRun.GlyphTypeface.FamilyName != mono.FamilyName), Is.True);
            }
        }
    }

    [AvaloniaTest]
    public void ActualMenuAccessTextAndSettingsTextBlockKeepTheirRequestedFamilyAndWeight()
    {
        RequireMacOS();
        AccessText menu = new() { Text = "设置(_S)", FontFamily = new FontFamily(MonoFamily), FontWeight = FontWeight.Bold };
        TextBlock settings = new() { Text = "設定", FontFamily = new FontFamily(MonoFamily) };
        foreach (TextBlock control in new TextBlock[] { menu, settings })
        {
            FontFamily requestedFamily = control.FontFamily;
            FontWeight requestedWeight = control.FontWeight;
            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            string layoutText = string.Concat(control.TextLayout.TextLines.SelectMany(line => line.TextRuns).Select(run => run.Text.ToString()));
            Assert.That(layoutText, Does.Contain(control == menu ? "设置" : "設定"));
            AssertComplete(control.TextLayout, layoutText);
            Assert.That(control.FontFamily, Is.SameAs(requestedFamily));
            Assert.That(control.FontWeight, Is.EqualTo(requestedWeight));
        }
    }

    [AvaloniaTest]
    public void ExistingIconsAndMonoAdvancesArePreserved()
    {
        GlyphTypeface icon = new Typeface(new FontFamily(IconFamily)).GlyphTypeface;
        Assert.That(icon.FamilyName, Is.EqualTo("Segoe Fluent Icons"));
        foreach (string key in new[] { "Device", "Chip", "Bug" })
        {
            string text = (string)new GlyphValueConverter(key).ProvideValue(null);
            Assert.That(UiFontPolicy.IsCjkCodepoint(char.ConvertToUtf32(text, 0)), Is.False);
            using TextLayout layout = new(text, new Typeface(new FontFamily(IconFamily)), 16, Brushes.Black);
            ShapedTextRun[] runs = AssertComplete(layout, text);
            Assert.That(runs.All(run => run.GlyphRun.GlyphTypeface.FamilyName == icon.FamilyName), Is.True, key);
        }

        GlyphTypeface mono = new Typeface(new FontFamily(MonoFamily)).GlyphTypeface;
        Assert.That(mono.FamilyName, Does.StartWith("JetBrains Mono"));
        Assert.That(mono.TryGetHorizontalGlyphAdvance(mono.CharacterToGlyphMap.GetGlyph('i'), out ushort advance), Is.True);
        Assert.That(advance, Is.GreaterThan(0));
        foreach (char character in new[] { 'W', '0' })
        {
            Assert.That(mono.TryGetHorizontalGlyphAdvance(mono.CharacterToGlyphMap.GetGlyph(character), out ushort actual), Is.True);
            Assert.That(actual, Is.EqualTo(advance));
        }
    }

    [AvaloniaTest]
    public void EmojiStillUsesPlatformCoverageOutsideTheCjkOverride()
    {
        RequireMacOS();
        Assert.That(UiFontPolicy.IsCjkCodepoint(0x1f600), Is.False);
        using TextLayout layout = new("A 😀 中", Typeface.Default, 16, Brushes.Black);
        AssertComplete(layout, "A 😀 中");
    }

    private static ShapedTextRun[] AssertComplete(TextLayout layout, string expected)
    {
        ShapedTextRun[] runs = layout.TextLines.SelectMany(line => line.TextRuns).OfType<ShapedTextRun>().ToArray();
        Assert.That(runs, Is.Not.Empty);
        Assert.That(runs.Sum(run => run.Length), Is.EqualTo(expected.Length), "Do not accept a truncated layout as complete coverage.");
        foreach (ShapedTextRun run in runs)
        {
            GlyphTypeface face = run.GlyphRun.GlyphTypeface;
            Assert.That(face.PlatformTypeface.GetType().Assembly.GetName().Name, Is.EqualTo("Avalonia.Skia"));
            Assert.That(run.GlyphRun.GlyphInfos.All(info => info.GlyphIndex != 0), Is.True, $"{face.FamilyName}: {run.Text}");
            foreach (Rune rune in run.Text.ToString().EnumerateRunes())
            {
                Assert.That(face.CharacterToGlyphMap.GetGlyph(rune.Value), Is.Not.Zero, $"{face.FamilyName}: U+{rune.Value:X}");
            }
        }
        return runs;
    }

    private static void RequireMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Ignore("The explicit system CJK policy is macOS-only; this case needs the real macOS Skia font manager.");
        }
    }
}
