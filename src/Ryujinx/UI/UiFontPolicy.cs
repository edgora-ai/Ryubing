using Avalonia.Media;
using System;

namespace Ryujinx.Ava.UI
{
    /// <summary>
    /// Host UI CJK fallback only. Keeps the default Latin face, embedded mono/icons, emoji
    /// selection and guest firmware/shared fonts unchanged. No system fonts are redistributed.
    /// </summary>
    public static class UiFontPolicy
    {
        // Han, Bopomofo, CJK punctuation and full-width forms, but not Latin, emoji or PUA icons.
        private static readonly UnicodeRange _cjkRange = UnicodeRange.Parse(
            "2E80-2FFF,3000-303F,3100-312F,31A0-31EF,3200-33FF,3400-4DBF,4E00-9FFF," +
            "F900-FAFF,FE10-FE1F,FE30-FE4F,FF00-FFEF,20000-2FA1F,30000-3347F");

        /// <summary>Apply before Avalonia initializes FontManager; other platforms keep their defaults.</summary>
        public static FontManagerOptions CreateFontManagerOptions() =>
            OperatingSystem.IsMacOS() ? CreateMacOSFontManagerOptions() : new FontManagerOptions();

        /// <summary>
        /// Fresh options on each call. Separate entries let FontManager check each face's glyph
        /// coverage and continue to the next entry or platform fallback when a glyph is absent.
        /// </summary>
        public static FontManagerOptions CreateMacOSFontManagerOptions() => new()
        {
            FontFallbacks =
            [
                new FontFallback { FontFamily = new FontFamily("PingFang SC"), UnicodeRange = _cjkRange },
                new FontFallback { FontFamily = new FontFamily("PingFang TC"), UnicodeRange = _cjkRange },
                new FontFallback { FontFamily = new FontFamily("Heiti SC"), UnicodeRange = _cjkRange },
                new FontFallback { FontFamily = new FontFamily("Heiti TC"), UnicodeRange = _cjkRange },
                new FontFallback { FontFamily = new FontFamily("Arial Unicode MS"), UnicodeRange = _cjkRange },
            ],
        };

        public static bool IsCjkCodepoint(int codepoint) => _cjkRange.IsInRange(codepoint);
    }
}
