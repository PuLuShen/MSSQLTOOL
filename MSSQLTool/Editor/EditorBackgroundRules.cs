using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace MSSQLTool.Editor
{
    /// <summary>
    /// The parts of the editor background feature that do not touch Visual Studio types: which
    /// picture is requested, how strong it is drawn and how it fills the window.  Keeping this
    /// separate from <see cref="EditorBackground"/> lets the rules be tested without loading the
    /// editor assemblies (and keeps the policy out of the view plumbing).
    /// </summary>
    internal static class EditorBackgroundRules
    {
        /// <summary>Names of the bundled pictures (see Resources/backgrounds/NOTICE.txt).</summary>
        internal static readonly string[] BuiltInImages =
        {
            "sage-breeze", "crimson-horizon", "noir-gold", "violet-starlight", "spark-notebook", "rose-dawn"
        };

        /// <summary>
        /// Pictures shipped up to 5.447.  A settings file written before the switch still names them,
        /// so they are mapped to the closest replacement instead of resolving to "no picture".
        /// </summary>
        private static readonly Dictionary<string, string> LegacyBuiltInImages =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "mist-forest", "sage-breeze" },
                { "night-sky", "violet-starlight" },
                { "mountain-ridge", "crimson-horizon" },
                { "desert-dunes", "noir-gold" }
            };

        internal const string BuiltInPackPrefix = "pack://application:,,,/MSSQLTool;component/Resources/backgrounds/";

        /// <summary>Dark themes get a lighter touch so code stays readable.</summary>
        internal const double DarkThemeFactor = 0.75;

        /// <summary>Where the theme colour under the picture is taken from.</summary>
        internal enum BaseBrushSource
        {
            /// <summary>The brush currently on the view, which is a theme brush.</summary>
            Captured,

            /// <summary>The brush the view had when it was first seen, which is a theme brush.</summary>
            Creation,

            /// <summary>The first theme brush this feature ever saw, kept for views created later.</summary>
            Remembered,

            /// <summary>No theme brush is known; the picture is painted without a base rectangle.</summary>
            None
        }

        /// <summary>
        /// Chooses the brush the picture is composed on top of.  A brush this feature painted already
        /// contains the picture, so using it as the base paints the picture twice: a 25% picture
        /// becomes about 44% after a second document is opened — the "the background got brighter by
        /// itself" report.  A painted brush must therefore never win, even when it is the only one
        /// currently on the view.
        /// </summary>
        internal static BaseBrushSource ChooseBase(bool capturedIsOurs, bool creationIsOurs, bool hasRemembered)
        {
            if (!capturedIsOurs) return BaseBrushSource.Captured;
            if (!creationIsOurs) return BaseBrushSource.Creation;
            return hasRemembered ? BaseBrushSource.Remembered : BaseBrushSource.None;
        }

        /// <summary>Editor format map entry that paints the opaque editor background.</summary>
        internal const string TextViewBackgroundKey = "TextView Background";

        /// <summary>
        /// Surfaces the picture is painted on.  There must be exactly one, and it is the format map's
        /// "TextView Background" entry: that is what the shell actually paints.  The view's own
        /// background is covered by that layer, and a transparent entry there reveals the black window
        /// behind the editor (5.490), so the picture can never move elsewhere.  A second surface would
        /// stretch the same brush into another rectangle and show a seam plus an uncovered strip.
        /// </summary>
        internal static readonly string[] PictureSurfaces = { "format-map" };

        /// <summary>
        /// Format items that paint the code area: the plain-text classification (per character runs)
        /// and the view background (empty space).  Both have to be transparent for a picture on the
        /// view to be visible, and both are restored together.
        /// </summary>
        internal static readonly string[] TransparentBackgroundKeys =
        {
            TextViewBackgroundKey, "Plain Text", "SQL", "TSQL"
        };

        /// <summary>Below this luminance the theme counts as dark.</summary>
        internal const double DarkThemeThreshold = 0.45;

        /// <summary>
        /// Resolves a setting value into an image URI: <c>builtin:name</c> for the bundled pictures,
        /// otherwise an absolute file path.  Returns null when the value cannot be resolved, so the
        /// caller simply keeps the theme background.
        /// </summary>
        internal static Uri ResolveImageUri(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string trimmed = value.Trim();

            if (trimmed.StartsWith(SettingsManager.EditorBackgroundSettings.BuiltInPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string name = trimmed.Substring(SettingsManager.EditorBackgroundSettings.BuiltInPrefix.Length).Trim();
                string retired;
                if (LegacyBuiltInImages.TryGetValue(name, out retired)) name = retired;
                if (Array.IndexOf(BuiltInImages, name) < 0) return null;
                return new Uri(BuiltInPackPrefix + name + ".jpg", UriKind.Absolute);
            }

            try
            {
                if (!File.Exists(trimmed)) return null;
                return new Uri(Path.GetFullPath(trimmed), UriKind.Absolute);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The settings UI speaks in transparency (how much the picture is washed out), while the stored
        /// setting stays in opacity so an existing settings file keeps its meaning.  0% transparency
        /// shows the picture fully, 95% is the faintest the slider allows.
        /// </summary>
        internal static int OpacityToTransparency(int opacityPercent)
        {
            return SettingsManager.EditorBackgroundSettings.MaxOpacityPercent - ClampOpacity(opacityPercent);
        }

        internal static int TransparencyToOpacity(int transparencyPercent)
        {
            int transparency = Math.Max(0, Math.Min(100, transparencyPercent));
            return ClampOpacity(SettingsManager.EditorBackgroundSettings.MaxOpacityPercent - transparency);
        }

        private static int ClampOpacity(int opacityPercent)
        {
            return Math.Max(
                SettingsManager.EditorBackgroundSettings.MinOpacityPercent,
                Math.Min(SettingsManager.EditorBackgroundSettings.MaxOpacityPercent, opacityPercent));
        }

        internal static double EffectiveOpacity(int opacityPercent, bool dimOnDarkTheme, Brush baseBrush)
        {
            double opacity = ClampOpacity(opacityPercent) / 100.0;

            // 100% is an explicit request for a fully covering picture; the dark-theme softening must
            // not silently turn it into 75%.
            if (dimOnDarkTheme && opacity < 1.0 && IsDark(baseBrush)) opacity *= DarkThemeFactor;

            return Math.Round(Math.Max(0.02, Math.Min(1.0, opacity)), 3);
        }

        internal static bool IsDark(Brush brush)
        {
            if (brush == null) return false;

            var solid = brush as SolidColorBrush;
            if (solid != null) return Luminance(solid.Color) < DarkThemeThreshold;

            var gradient = brush as GradientBrush;
            if (gradient != null && gradient.GradientStops.Count > 0)
            {
                double total = 0;
                foreach (GradientStop stop in gradient.GradientStops) total += Luminance(stop.Color);
                return total / gradient.GradientStops.Count < DarkThemeThreshold;
            }

            // An image brush means a picture is already painted; do not dim on top of it.
            return false;
        }

        internal static double Luminance(Color color)
        {
            return (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
        }

        internal static Stretch StretchFor(string fit)
        {
            switch (SettingsManager.EditorBackgroundSettings.NormalizeFit(fit))
            {
                case "Fill": return Stretch.Fill;
                case "Uniform": return Stretch.Uniform;
                case "Tile": return Stretch.Fill;
                default: return Stretch.UniformToFill;
            }
        }

        /// <summary>
        /// Where the picture is drawn inside an area for the default "cover the area" fit, in that
        /// area's coordinates.  Both the format-map layer and the host layer use this same geometry, so
        /// the strip the host paints continues the picture instead of breaking it at a different scale.
        /// </summary>
        internal static Rect PicturePlacement(double imageWidth, double imageHeight, double areaWidth, double areaHeight)
        {
            return PlacementForFit("UniformToFill", imageWidth, imageHeight, areaWidth, areaHeight);
        }

        /// <summary>
        /// The picture rectangle for the selected fit mode, in the coordinates of the painted area.
        /// The format layer lets WPF stretch the drawing into its own rectangle, so the host layer has
        /// to reproduce that same rectangle instead of always assuming "cover": with Fill, Uniform or
        /// Tile the two copies would otherwise land at different sizes and the left margin - which is
        /// transparent and shows the host layer - would break away from the text area.
        /// </summary>
        internal static Rect PlacementForFit(
            string fit, double imageWidth, double imageHeight, double areaWidth, double areaHeight)
        {
            if (imageWidth <= 0 || imageHeight <= 0 || areaWidth <= 0 || areaHeight <= 0) return Rect.Empty;

            switch (SettingsManager.EditorBackgroundSettings.NormalizeFit(fit))
            {
                case "Fill":
                    return new Rect(0, 0, areaWidth, areaHeight);

                case "Tile":
                    // A tiled brush repeats the image at its natural size; one ImageDrawing cannot
                    // express that, and a wrong copy would show as a seam.  The host stays on the
                    // theme colour in this mode (the text area still tiles normally).
                    return Rect.Empty;

                case "Uniform":
                    return Scale(imageWidth, imageHeight, areaWidth, areaHeight,
                        Math.Min(areaWidth / imageWidth, areaHeight / imageHeight));

                default:
                    return Scale(imageWidth, imageHeight, areaWidth, areaHeight,
                        Math.Max(areaWidth / imageWidth, areaHeight / imageHeight));
            }
        }

        private static Rect Scale(double imageWidth, double imageHeight, double areaWidth, double areaHeight, double scale)
        {
            double width = imageWidth * scale;
            double height = imageHeight * scale;
            return new Rect((areaWidth - width) / 2, (areaHeight - height) / 2, width, height);
        }

        /// <summary>
        /// The format layer paints in the editor's zoomed coordinate space, so the area it fills is the
        /// text canvas multiplied by the zoom factor (a 585 DIP viewport at 119% is painted 696 DIP
        /// tall).  A bogus reading falls back to 100%, never to an empty rectangle.
        /// </summary>
        internal static Size ZoomedArea(double width, double height, double zoom)
        {
            double factor = zoom > 0 && zoom < 8 ? zoom : 1.0;
            return new Size(Math.Max(0, width * factor), Math.Max(0, height * factor));
        }
    }
}
