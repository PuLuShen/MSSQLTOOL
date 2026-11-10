using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using MSSQLTool.Editor.Vs2022;

namespace MSSQLTool.Editor
{
    /// <summary>
    /// Paints an optional picture behind the SQL text.
    ///
    /// Two ways of doing that are implemented, and the first one that can be set up wins:
    ///
    /// Backdrop (preferred).  A single image grid is inserted as the first child of the panel that
    /// holds the editor host, and every surface above it - the text view, the format map entries and
    /// the panels in between - is made transparent.  That is the architecture ClaudiaIDE uses: one
    /// rectangle behind everything, so the left margin, the text area and the strip below the text are
    /// covered by the very same picture.  It has no seam and no uncovered strip by construction, and
    /// because the picture lives outside the text rendering pipeline it is not affected by the editor
    /// zoom (the format layer paints in zoomed coordinates, so at 119% its picture is 19% larger than
    /// the same rectangle in the host).
    ///
    /// Format map (fallback).  SSMS paints the code area from the "TextView Background" entry of the
    /// editor format map, so the picture can be composed with the theme colour there, and the host
    /// control gets the same rectangle painted with absolute coordinates.  This path is kept for
    /// shells where no suitable panel can be found above the view, and it is what the early builds
    /// shipped.
    ///
    /// Everything is reverted together, so switching the feature off restores the theme.  No adornment
    /// is used: both surfaces are viewport relative, so scrolling never repaints the document.
    /// </summary>
    internal static class EditorBackground
    {
        // Keep the decoded bitmap small enough for an editor: a 4K photo decoded at full size costs
        // tens of megabytes for a surface that is never wider than the screen.
        private const int DecodeWidth = 2560;

        private static readonly object Gate = new object();
        private static readonly ConditionalWeakTable<IWpfTextView, ViewState> States =
            new ConditionalWeakTable<IWpfTextView, ViewState>();

        /// <summary>
        /// Brushes this feature painted.  Each one already contains the picture, so none of them can
        /// be the base the next composition is drawn on: doing that paints the picture twice and the
        /// background gets clearer every time a document is opened.
        /// </summary>
        private static readonly ConditionalWeakTable<Brush, object> OwnBrushes =
            new ConditionalWeakTable<Brush, object>();
        private static readonly object OwnBrushMarker = new object();

        /// <summary>
        /// The theme brush seen before any picture was painted.  Views created after the picture was
        /// applied inherit our brush, so the real theme colour has to be remembered once.
        /// </summary>
        private static readonly object BaseGate = new object();
        private static Brush rememberedBaseBrush;

        /// <summary>
        /// Original format-map properties per map.  A second view can reuse a map that is already
        /// painted; snapshotting that state would both double-paint and make "off" restore the
        /// picture instead of the theme colour.
        /// </summary>
        private static readonly ConditionalWeakTable<object, Dictionary<string, ResourceDictionary>> MapOriginals =
            new ConditionalWeakTable<object, Dictionary<string, ResourceDictionary>>();
        private static readonly List<WeakReference<IWpfTextView>> Views = new List<WeakReference<IWpfTextView>>();
        private static readonly Dictionary<string, BitmapImage> Images =
            new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Small decoded copies for the settings picker, keyed by picture and width.</summary>
        private static readonly Dictionary<string, BitmapImage> Thumbnails =
            new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Injected by the MEF listener; also used to make the text background transparent.</summary>
        internal static IEditorFormatMapService FormatMapService;

        /// <summary>
        /// Second way to reach the same format map.  Both services are imported as optional because
        /// two independent exports are cheaper than one that may silently be missing.
        /// </summary>
        internal static IClassificationFormatMapService ClassificationFormatMapService;

        internal sealed class ViewState
        {
            public Brush BaseBrush;
            public Brush AppliedBrush;
            public bool Applying;
            public bool ImageApplied;

            public IEditorFormatMap FormatMap;
            public bool MapTried;
            public bool MapHooked;
            public string MapSource;
            public bool ChromeLogged;

            /// <summary>Last geometry the host layer used, logged with every apply.</summary>
            public string Geometry;

            /// <summary>Ancestor control that paints the strip below the text surface.</summary>
            public FrameworkElement Host;
            public int HostAttempts;
            public Brush HostOriginalBrush;
            public Brush HostAppliedBrush;
            public readonly Dictionary<string, ResourceDictionary> SavedProperties =
                new Dictionary<string, ResourceDictionary>(StringComparer.Ordinal);

            /// <summary>"backdrop" or "format"; logged with every apply so a screenshot can be matched.</summary>
            public string Mode;

            /// <summary>Panel that holds the editor host and therefore the backdrop grid.</summary>
            public Panel BackdropParent;
            public Grid BackdropRoot;
            public Image BackdropPicture;
            public bool BackdropShared;
            public bool BackdropWanted;
            public int BackdropAttempts;

            /// <summary>Type names of the chrome visuals that were made transparent, for the log.</summary>
            public string ChromeCleared;

            /// <summary>Children of the backdrop panel, logged so a leftover strip can be named.</summary>
            public string PanelChildren;

            /// <summary>
            /// Visuals this feature made transparent, with the brush each one had.  The text surface
            /// only reaches the backdrop when every element in between is transparent, so the whole
            /// chain has to be recorded to be able to restore it.
            /// </summary>
            public readonly List<KeyValuePair<FrameworkElement, Brush>> ClearedBackgrounds =
                new List<KeyValuePair<FrameworkElement, Brush>>();
        }

        /// <summary>Called from the MEF creation listener for every SQL editor document.</summary>
        internal static void Attach(
            IWpfTextView view,
            IEditorFormatMapService formatMapService,
            IClassificationFormatMapService classificationFormatMapService)
        {
            if (formatMapService != null) FormatMapService = formatMapService;
            if (classificationFormatMapService != null) ClassificationFormatMapService = classificationFormatMapService;
            if (view == null || view.IsClosed) return;

            ViewState existing;
            if (States.TryGetValue(view, out existing))
            {
                // The editor host path can reach a view before the MEF listener fires (documents
                // restored during startup), and the listener must not subscribe a second time.
                Apply(view);
                return;
            }

            ViewState state = States.GetValue(view, v => new ViewState { BaseBrush = v.Background });
            Track(view);

            view.BackgroundBrushChanged += OnBackgroundBrushChanged;
            view.Closed += OnViewClosed;
            view.LayoutChanged += OnLayoutChanged;
            view.ZoomLevelChanged += OnZoomLevelChanged;

            // The host layer uses an absolute viewport, so it has to be recomputed when the editor is
            // resized; otherwise the strip below the text would keep the old crop.
            var element = view.VisualElement as FrameworkElement;
            if (element != null) element.SizeChanged += OnVisualSizeChanged;

            IEditorFormatMap map = EnsureFormatMap(view, state);

            Apply(view);

            MSSQLToolPackage._logger?.Info(
                "SQL-BACKGROUND attach: formatMap=" + (map != null ? "yes" : "no") +
                " service=" + DescribeServices() +
                " contentType=" + SafeContentType(view) +
                " roles=" + SafeRoles(view) +
                " enabled=" + SettingsManager.GetEditorBackgroundSettings().enabled);
        }

        /// <summary>
        /// Attaches the feature to a view that the MEF listener may have missed.  SSMS restores query
        /// windows during startup, when the extension's text-view listener is not necessarily
        /// composed yet; the editor host path (which the package only builds when it touches the
        /// view) calls this so the picture still appears.
        /// </summary>
        internal static void EnsureAttached(IWpfTextView view)
        {
            if (view == null || view.IsClosed) return;

            ViewState state;
            if (States.TryGetValue(view, out state))
            {
                if (!state.ImageApplied && SettingsManager.GetEditorBackgroundSettings().enabled) Apply(view);
                return;
            }

            Attach(view, FormatMapService, ClassificationFormatMapService);
        }

        private static string DescribeServices()
        {
            string format = FormatMapService != null ? "format" : "-";
            string classification = ClassificationFormatMapService != null ? "classification" : "-";
            return format + "/" + classification;
        }

        private static string SafeContentType(IWpfTextView view)
        {
            try { return view.TextBuffer == null ? "?" : view.TextBuffer.ContentType.TypeName; }
            catch (Exception) { return "?"; }
        }

        private static string SafeRoles(IWpfTextView view)
        {
            try { return view.Roles == null ? "?" : view.Roles.ToString(); }
            catch (Exception) { return "?"; }
        }

        /// <summary>Re-applies the current settings to every open SQL editor.</summary>
        internal static void RefreshAll()
        {
            var open = new List<IWpfTextView>(OpenViews());
            foreach (IWpfTextView view in SqlEditorServices.OpenViews())
            {
                if (!open.Contains(view)) open.Add(view);
            }

            foreach (IWpfTextView view in open)
            {
                ViewState state;
                if (!States.TryGetValue(view, out state))
                {
                    Attach(view, FormatMapService, ClassificationFormatMapService);
                }
                else
                {
                    // Saving the settings is an explicit user request: look for the format map once more
                    // in case it was unavailable when this view was first seen.
                    state.MapTried = false;
                    Apply(view);
                }
            }

            MSSQLToolPackage._logger?.Info("SQL-BACKGROUND refresh: views=" + open.Count +
                " enabled=" + SettingsManager.GetEditorBackgroundSettings().enabled);
        }

        private static bool IsOwnBrush(Brush brush)
        {
            object ignored;
            return brush != null && OwnBrushes.TryGetValue(brush, out ignored);
        }

        private static void RememberBaseBrush(Brush brush)
        {
            if (brush == null || IsOwnBrush(brush)) return;
            lock (BaseGate) { rememberedBaseBrush = brush; }
        }

        private static bool HasRememberedBase
        {
            get { lock (BaseGate) { return rememberedBaseBrush != null; } }
        }

        private static Brush RememberedBase
        {
            get { lock (BaseGate) { return rememberedBaseBrush; } }
        }

        internal static void Apply(IWpfTextView view)
        {
            if (view == null || view.IsClosed) return;

            ViewState state = States.GetValue(view, v => new ViewState { BaseBrush = v.Background });
            SettingsManager.EditorBackgroundSettings settings = SettingsManager.GetEditorBackgroundSettings();

            if (!settings.enabled)
            {
                Restore(view, state);
                return;
            }

            // The picture only becomes visible when the theme background is transparent, so the
            // format map is resolved here as well: saving the settings is the documented retry point.
            EnsureFormatMap(view, state);

            BitmapImage image = LoadImage(settings.image);
            if (image == null)
            {
                // A missing or unreadable file must never break the editor: keep the theme colour.
                Restore(view, state);
                return;
            }

            // A view created after the picture was painted already carries our brush, so the theme
            // colour has to be recovered instead of being used as the base: composing on top of a
            // painted brush draws the picture twice.
            EditorBackgroundRules.BaseBrushSource baseSource = EditorBackgroundRules.ChooseBase(
                IsOwnBrush(view.Background), IsOwnBrush(state.BaseBrush), HasRememberedBase);
            Brush baseBrush = null;
            switch (baseSource)
            {
                case EditorBackgroundRules.BaseBrushSource.Captured: baseBrush = view.Background; break;
                case EditorBackgroundRules.BaseBrushSource.Creation: baseBrush = state.BaseBrush; break;
                case EditorBackgroundRules.BaseBrushSource.Remembered: baseBrush = RememberedBase; break;
            }
            RememberBaseBrush(baseBrush);

            double opacity = EditorBackgroundRules.EffectiveOpacity(settings.opacityPercent, settings.dimOnDarkTheme, baseBrush);
            Brush brush = BuildEditorBrush(image, settings.fit, baseBrush, opacity);
            OwnBrushes.Add(brush, OwnBrushMarker);

            state.Applying = true;
            try
            {
                // Preferred: one picture behind the whole editor.  The text area, the left margin and
                // the strip below the text belong to different visuals; painting each of them
                // separately is exactly what produced the seam and the uncovered strip.
                bool backdrop = false;
                if (settings.useBackdrop)
                {
                    backdrop = TryApplyBackdrop(
                        view, state, image, baseBrush, settings.opacityPercent, settings.dimOnDarkTheme, settings.fit,
                        out opacity);
                }
                else
                {
                    // Escape hatch kept in the settings file: an older shell that paints the text
                    // surface opaque would hide the backdrop and leave a black editor.
                    state.BackdropWanted = false;
                }

                if (backdrop)
                {
                    // Everything above the backdrop becomes transparent, including the view itself.
                    SetViewBackground(view, state, Brushes.Transparent);
                    ApplyFormatMap(state, null);
                    RestoreHost(state);
                }
                else
                {
                    // The format map is the only surface that is actually painted, so the view keeps
                    // its plain theme colour: a second copy there could never be aligned with it.
                    SetViewBackground(view, state, baseBrush ?? state.BaseBrush);
                    ApplyFormatMap(state, brush);
                    ApplyHostSurface(view, state, image, baseBrush, opacity);
                }

                state.Mode = backdrop ? "backdrop" : "format";
                state.ImageApplied = true;
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Could not paint the editor background image.");
            }
            finally
            {
                state.Applying = false;
            }
        }

        internal static void Restore(IWpfTextView view, ViewState state)
        {
            if (view == null || state == null) return;
            if (!state.ImageApplied && state.SavedProperties.Count == 0 && state.BackdropRoot == null) return;

            state.Applying = true;
            try
            {
                RestoreTextBackground(state);
                // state.BaseBrush can itself be a brush we painted (a view created while another
                // document already showed the picture).  Restoring to it would leave the picture on
                // screen after the feature was switched off.
                Brush restoreTo = IsOwnBrush(state.BaseBrush)
                    ? (HasRememberedBase ? RememberedBase : null)
                    : state.BaseBrush;
                RestoreBackdrop(state);
                SetViewBackground(view, state, restoreTo);
                RestoreHost(state);
                state.ImageApplied = false;
                state.Mode = null;
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Could not restore the editor background.");
            }
            finally
            {
                state.Applying = false;
            }
        }

        /// <summary>Puts the host control's own background back when the feature is switched off.</summary>
        private static void RestoreHost(ViewState state)
        {
            if (state == null || state.Host == null) return;

            try
            {
                if (ReferenceEquals(GetBackground(state.Host), state.HostAppliedBrush))
                {
                    SetBackground(state.Host, state.HostOriginalBrush);
                }
            }
            catch (Exception) { }
            finally
            {
                state.HostAppliedBrush = null;
            }
        }

        private static Brush GetBackground(FrameworkElement element)
        {
            var control = element as Control;
            if (control != null) return control.Background;
            var panel = element as Panel;
            if (panel != null) return panel.Background;
            var border = element as Border;
            if (border != null) return border.Background;
            return null;
        }

        private static void SetBackground(FrameworkElement element, Brush brush)
        {
            var control = element as Control;
            if (control != null) { control.Background = brush; return; }
            var panel = element as Panel;
            if (panel != null) { panel.Background = brush; return; }
            var border = element as Border;
            if (border != null) border.Background = brush;
        }

        private static void SetViewBackground(IWpfTextView view, ViewState state, Brush brush)
        {
            if (brush == null)
            {
                // Never leave the control unpainted when the theme brush was unavailable.
                brush = state.BaseBrush;
                if (brush == null) return;
            }

            view.Background = brush;

            // The visible surface is the WPF control; set the same brush there in case the interface
            // property is only a facade in this shell version.
            var control = view.VisualElement as Control;
            if (control != null) control.Background = brush;

            state.AppliedBrush = brush;
        }

        /// <summary>
        /// Puts the picture behind the whole editor: a grid inserted as the first child of the panel
        /// that holds the editor host, with the theme colour underneath and the picture at the
        /// requested opacity on top.  Everything between the picture and the text is made transparent
        /// afterwards, which is what makes the left margin, the code area and the strip below the text
        /// show one and the same picture instead of three differently placed copies.
        ///
        /// Returns false when this shell offers nothing to insert the grid into (or when the fit mode
        /// needs tiling, which an Image control cannot do): the caller then falls back to the format
        /// map path.
        /// </summary>
        private static bool TryApplyBackdrop(
            IWpfTextView view, ViewState state, BitmapImage image, Brush baseBrush,
            int opacityPercent, bool dimOnDarkTheme, string fit, out double appliedOpacity)
        {
            appliedOpacity = 0;
            state.BackdropWanted = false;
            if (image == null) return false;

            // An Image control stretches one copy; tiling is only possible through a brush, and the
            // format map path still supports it.
            string normalized = SettingsManager.EditorBackgroundSettings.NormalizeFit(fit);
            if (string.Equals(normalized, "Tile", StringComparison.Ordinal)) return false;

            var element = view.VisualElement as FrameworkElement;
            if (element == null) return false;

            state.BackdropWanted = true;

            Panel parent = state.BackdropParent;
            if (parent == null || state.BackdropRoot == null)
            {
                parent = FindBackdropParent(element);
                if (parent == null) return false;
                state.BackdropParent = parent;
            }

            Grid root = state.BackdropRoot;
            if (root == null)
            {
                root = FindExistingBackdrop(parent);
                if (root != null)
                {
                    // Another view of the same host panel already owns the picture; two copies would
                    // darken each other.  Only the owner removes it again.
                    state.BackdropShared = true;
                }
                else
                {
                    root = CreateBackdrop(parent);
                    if (root == null) return false;
                }

                state.BackdropRoot = root;
                state.BackdropPicture = FindBackdropPicture(root);
            }

            if (state.BackdropPicture == null) return false;

            // The chain has to be cleared first: the brushes recorded there are the colours that used
            // to show behind the code, and one of them is the theme background the picture needs.
            // Exactly that one is taken - never the colours of the chrome elements cleared below:
            // after a query runs, SSMS paints an indicator whose background is dark, and using it as
            // the base tinted the whole editor.
            Brush chainBase;
            ClearSurfaceChain(state, element, parent, out chainBase);

            // The scrollbars and the empty strip below the text are not part of the text view either;
            // they keep the theme colour and showed up as a white band under the picture.  Everything
            // inside the panel that holds the backdrop is made transparent for the same reason,
            // including the siblings of the view (a scrollbar container, for example), while scrollbar
            // thumbs, popups and adorners are kept.
            int budget = 600;
            ClearChromeBackgrounds(state, parent, 0, ref budget);

            // A view brush in this shell is transparent white (#00FFFFFF): the editor background is
            // painted by the visuals above the view.  Once those are transparent for the backdrop, the
            // picture would be composed on the bare window - which is black - and a 30% picture became
            // a dark, muddy rectangle.  The base therefore has to be a genuinely opaque brush.
            Brush resolvedBase = ResolveBackdropBase(state, parent, baseBrush, chainBase);
            if (resolvedBase == null) resolvedBase = Brushes.White;

            appliedOpacity = EditorBackgroundRules.EffectiveOpacity(opacityPercent, dimOnDarkTheme, resolvedBase);

            root.Background = resolvedBase;
            state.BackdropPicture.Source = image;
            state.BackdropPicture.Opacity = appliedOpacity;
            state.BackdropPicture.Stretch = EditorBackgroundRules.StretchFor(normalized);

            state.BackdropAttempts = 0;
            return true;
        }

        /// <summary>
        /// Finds an opaque colour to put under the picture.  The theme background is not on the view in
        /// this shell (it is transparent white), so the first opaque brush in the chain that was just
        /// cleared, or in the panel that holds it, is the colour the editor really shows.
        /// </summary>
        private static Brush ResolveBackdropBase(ViewState state, Panel parent, Brush preferred, Brush chainBase)
        {
            if (IsOpaqueSolid(preferred)) return preferred;
            if (state != null && IsOpaqueSolid(state.BaseBrush)) return state.BaseBrush;
            if (IsOpaqueSolid(RememberedBase)) return RememberedBase;

            // What used to be behind the code: the outermost opaque brush of the view's own chain.
            if (IsOpaqueSolid(chainBase)) return chainBase;

            DependencyObject node = parent;
            for (int depth = 0; depth < 8 && node != null; depth++)
            {
                var surface = node as FrameworkElement;
                if (surface != null)
                {
                    Brush brush = GetBackground(surface);
                    if (IsOpaqueSolid(brush)) return brush;
                }

                node = VisualTreeHelper.GetParent(node);
            }

            return null;
        }

        private static bool IsOpaqueSolid(Brush brush)
        {
            var solid = brush as SolidColorBrush;
            return solid != null && solid.Color.A == 255;
        }

        private const string BackdropName = "MSSQLToolEditorBackdrop";

        /// <summary>
        /// The panel that holds the editor host.  The first panel above the text view is used: it is
        /// the container the host itself paints into, so a picture there covers the text area, the
        /// margins and the strip below the text at once.
        /// </summary>
        private static Panel FindBackdropParent(FrameworkElement element)
        {
            DependencyObject node = element;
            for (int depth = 0; depth < 6; depth++)
            {
                if (node == null) return null;
                node = VisualTreeHelper.GetParent(node);
                if (node == null) return null;

                var panel = node as Panel;
                if (panel == null) continue;

                if (!CanHostBackdrop(panel)) return null;
                if (panel.ActualWidth <= 0 || panel.ActualHeight <= 0) return null;
                if (panel.ActualWidth + 1 < element.ActualWidth) return null;
                if (panel.ActualHeight + 1 < element.ActualHeight) return null;

                return panel;
            }

            return null;
        }

        /// <summary>A Canvas or a stacking panel measures its children without stretching them.</summary>
        private static bool CanHostBackdrop(Panel panel)
        {
            if (panel is Canvas) return false;
            if (panel is StackPanel) return false;
            if (panel is WrapPanel) return false;
            return true;
        }

        private static Grid FindExistingBackdrop(Panel parent)
        {
            try
            {
                foreach (object child in parent.Children)
                {
                    var grid = child as Grid;
                    if (grid != null && string.Equals(grid.Name, BackdropName, StringComparison.Ordinal)) return grid;
                }
            }
            catch (Exception) { }

            return null;
        }

        private static Grid CreateBackdrop(Panel parent)
        {
            var grid = new Grid
            {
                Name = BackdropName,
                IsHitTestVisible = false,
                ClipToBounds = true
            };

            var picture = new Image
            {
                IsHitTestVisible = false,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
            grid.Children.Add(picture);

            try
            {
                var asGrid = parent as Grid;
                if (asGrid != null)
                {
                    int rows = asGrid.RowDefinitions.Count;
                    int columns = asGrid.ColumnDefinitions.Count;
                    if (rows > 0) Grid.SetRowSpan(grid, rows);
                    if (columns > 0) Grid.SetColumnSpan(grid, columns);
                }

                // First child: below the editor host and below its scrollbars.
                parent.Children.Insert(0, grid);
                return grid;
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Could not insert the editor backdrop layer.");
                return null;
            }
        }

        private static Image FindBackdropPicture(Grid root)
        {
            foreach (object child in root.Children)
            {
                var image = child as Image;
                if (image != null) return image;
            }

            return null;
        }

        /// <summary>
        /// Makes every visual between the text view and the backdrop panel transparent.  A single
        /// opaque background anywhere in that chain hides the picture completely, which is why this
        /// walks the chain instead of only clearing the view.  Original brushes are kept for restore.
        /// </summary>
        private static FrameworkElement ClearSurfaceChain(
            ViewState state, FrameworkElement element, Panel parent, out Brush chainBase)
        {
            DependencyObject node = element;
            FrameworkElement outermost = null;
            chainBase = null;
            for (int depth = 0; depth < 8 && node != null && !ReferenceEquals(node, parent); depth++)
            {
                var surface = node as FrameworkElement;
                if (surface != null)
                {
                    outermost = surface;
                    Brush current = GetBackground(surface);
                    var solid = current as SolidColorBrush;
                    if (current != null && (solid == null || solid.Color.A != 0) && !HasClearedBackground(state, surface))
                    {
                        state.ClearedBackgrounds.Add(new KeyValuePair<FrameworkElement, Brush>(surface, current));
                        // Outermost wins: the surface closest to the panel is the theme background.
                        if (IsOpaqueSolid(current)) chainBase = current;
                    }

                    SetBackground(surface, Brushes.Transparent);
                }

                node = VisualTreeHelper.GetParent(node);
            }

            return outermost;
        }

        /// <summary>
        /// Clears the opaque backgrounds inside the editor chrome (scrollbar tracks, the empty strip
        /// below the text, margins).  They are painted above the backdrop grid, so any opaque brush
        /// there shows as a band of the theme colour at the edge of the picture.  Thumbs, popups and
        /// adorners are left alone: a scrollbar without a thumb or a hit-testable popup with no
        /// background would be worse than a band.
        /// </summary>
        private static void ClearChromeBackgrounds(
            ViewState state, DependencyObject node, int depth, ref int budget)
        {
            if (node == null || budget <= 0 || depth > 10) return;
            if (state.BackdropRoot != null && ReferenceEquals(node, state.BackdropRoot)) return;

            var element = node as FrameworkElement;
            if (element != null)
            {
                string type = node.GetType().Name;
                if (!IsChromeExemption(type))
                {
                    Brush current = GetBackground(element);
                    var solid = current as SolidColorBrush;
                    if (current != null && (solid == null || solid.Color.A != 0))
                    {
                        if (!HasClearedBackground(state, element))
                        {
                            state.ClearedBackgrounds.Add(new KeyValuePair<FrameworkElement, Brush>(element, current));
                        }

                        SetBackground(element, Brushes.Transparent);
                    }
                }
            }

            budget--;
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int index = 0; index < count; index++)
            {
                ClearChromeBackgrounds(state, VisualTreeHelper.GetChild(node, index), depth + 1, ref budget);
            }
        }

        private static bool HasClearedBackground(ViewState state, FrameworkElement surface)
        {
            for (int index = 0; index < state.ClearedBackgrounds.Count; index++)
            {
                if (ReferenceEquals(state.ClearedBackgrounds[index].Key, surface)) return true;
            }

            return false;
        }

        /// <summary>Removes the picture layer and gives every cleared visual its background back.</summary>
        private static void RestoreBackdrop(ViewState state)
        {
            if (state == null) return;

            for (int index = state.ClearedBackgrounds.Count - 1; index >= 0; index--)
            {
                KeyValuePair<FrameworkElement, Brush> saved = state.ClearedBackgrounds[index];
                try { SetBackground(saved.Key, saved.Value); }
                catch (Exception) { }
            }

            state.ClearedBackgrounds.Clear();

            try
            {
                if (!state.BackdropShared && state.BackdropParent != null && state.BackdropRoot != null)
                    state.BackdropParent.Children.Remove(state.BackdropRoot);
            }
            catch (Exception) { }

            state.BackdropRoot = null;
            state.BackdropPicture = null;
            state.BackdropParent = null;
            state.BackdropShared = false;
            state.BackdropAttempts = 0;
        }

        /// <summary>
        /// The host control that contains the text view paints the thin strip below the text surface;
        /// that is the white line that survived every format-map change.  The picture is painted there
        /// too, with the very same geometry as the format layer: not the viewport, but the text canvas
        /// the format layer actually fills.  The left margin (line numbers, selection margin) is a
        /// separate transparent visual, so it shows this host layer; using the viewport instead of the
        /// canvas centered a second, differently scaled copy on a wider rectangle and left the margin
        /// broken away from the text area, with the theme colour showing below the shorter copy.
        /// </summary>
        private static void ApplyHostSurface(
            IWpfTextView view, ViewState state, BitmapImage image, Brush baseBrush, double opacity)
        {
            if (image == null) return;
            var element = view.VisualElement as FrameworkElement;
            if (element == null) return;

            double elementWidth = element.ActualWidth;
            double elementHeight = element.ActualHeight;
            if (elementWidth <= 0 || elementHeight <= 0)
            {
                elementWidth = view.ViewportWidth;
                elementHeight = view.ViewportHeight;
            }

            if (elementWidth <= 0 || elementHeight <= 0) return;

            Rect canvas = FindTextCanvas(view, element, elementWidth, elementHeight);

            // The format layer paints in the editor's zoomed coordinate space: at 119% a 585 DIP
            // viewport is painted 696 DIP tall.  Using the plain canvas here left the host copy both
            // smaller and shorter than the picture the user sees - the left margin broke away and
            // the theme colour showed below 585 DIP.  The host rectangle has to carry the same zoom.
            double zoom = ZoomFactor(view);
            Size zoomed = EditorBackgroundRules.ZoomedArea(canvas.Width, canvas.Height, zoom);
            Rect placement = EditorBackgroundRules.PlacementForFit(
                SettingsManager.GetEditorBackgroundSettings().fit,
                image.PixelWidth, image.PixelHeight, zoomed.Width, zoomed.Height);

            FrameworkElement host = state.Host ?? FindHost(element, elementHeight);
            if (host == null) return;

            Point offset;
            try { offset = element.TranslatePoint(new Point(0, 0), host); }
            catch (Exception) { return; }

            double hostWidth = Math.Max(1, host.ActualWidth);
            double hostHeight = Math.Max(1, host.ActualHeight);
            var hostRect = new Rect(0, 0, hostWidth, hostHeight);

            // The theme colour covers the whole host, not only the picture rectangle: outside that
            // rectangle a brush with TileMode=None draws nothing, which showed up as black blocks at the
            // corners and beside the scrollbar.  The brush maps host coordinates 1:1 (Viewbox == Viewport),
            // so the picture lands exactly where the format layer puts it.
            var drawing = new DrawingGroup();
            if (baseBrush != null)
            {
                drawing.Children.Add(new GeometryDrawing(baseBrush, null, new RectangleGeometry(hostRect)));
            }

            // Same rectangle the format layer gets: the text canvas, not the viewport.  The canvas
            // starts after the left margin, so the picture is shifted by that margin; otherwise the
            // margin shows a copy centred on the view and the two copies do not line up.  Tile mode
            // returns no rectangle: the host then keeps the theme colour instead of a wrong copy.
            Rect pictureRect = Rect.Empty;
            if (!placement.IsEmpty)
            {
                pictureRect = new Rect(
                    offset.X + canvas.X + placement.X,
                    offset.Y + canvas.Y + placement.Y,
                    placement.Width,
                    placement.Height);

                var picture = new DrawingGroup { Opacity = opacity };
                picture.Children.Add(new ImageDrawing(image, pictureRect));
                drawing.Children.Add(picture);
            }

            var brush = new DrawingBrush(drawing)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = hostRect,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = hostRect,
                Stretch = Stretch.Fill,
                TileMode = TileMode.None
            };

            try { brush.Freeze(); }
            catch (Exception) { }

            if (state.Host == null)
            {
                state.Host = host;
                state.HostOriginalBrush = GetBackground(host);
                // The host layer maps host coordinates 1:1, so a host-only resize needs a new brush.
                host.SizeChanged += OnVisualSizeChanged;
            }

            SetBackground(host, brush);
            state.HostAppliedBrush = brush;
        }

        /// <summary>The editor zoom as a factor; a zoom change repaints the text picture, so the host follows it.</summary>
        private static double ZoomFactor(IWpfTextView view)
        {
            try
            {
                double level = view.ZoomLevel;
                if (level > 0 && level < 800) return level / 100.0;
            }
            catch (Exception) { }

            return 1.0;
        }

        /// <summary>
        /// The rectangle the format layer really paints: the text canvas, which starts after the left
        /// margin and is as tall as the view.  The margin itself is transparent, so it shows the host
        /// layer; aligning the host picture to the canvas instead of the viewport is what makes the
        /// margin continue the picture instead of showing a second, differently scaled copy.
        /// </summary>
        private static Rect FindTextCanvas(
            IWpfTextView view, FrameworkElement element, double width, double height)
        {
            FrameworkElement layer = FindDescendant(element, 0, 8);
            if (layer != null)
            {
                try
                {
                    Point origin = layer.TranslatePoint(new Point(0, 0), element);
                    double left = Math.Max(0, Math.Min(origin.X, width));
                    double top = Math.Max(0, Math.Min(origin.Y, height));
                    double layerWidth = layer.ActualWidth > 0 ? layer.ActualWidth : width - left;
                    double canvasWidth = Math.Min(layerWidth, width - left);
                    double canvasHeight = Math.Min(height - top, height);
                    if (canvasWidth > 0 && canvasHeight > 0)
                        return new Rect(left, top, canvasWidth, canvasHeight);
                }
                catch (Exception) { }
            }

            // Fallback: the first character of the first line starts exactly where the text canvas
            // starts, so its left edge is the margin width when the layer is not reachable.
            try
            {
                IWpfTextViewLineCollection lines = view.TextViewLines;
                if (lines != null && lines.Count > 0)
                {
                    double left = lines[0].GetCharacterBounds(lines[0].Start).Left;
                    if (left > 0 && left < width) return new Rect(left, 0, width - left, height);
                }
            }
            catch (Exception) { }

            return new Rect(0, 0, width, height);
        }

        /// <summary>Finds the text rendering canvas inside the view, by type name across shells.</summary>
        private static FrameworkElement FindDescendant(DependencyObject node, int depth, int limit)
        {
            if (node == null || depth > limit) return null;

            var element = node as FrameworkElement;
            if (element != null && element.ActualWidth > 0 && element.ActualHeight > 0)
            {
                string name = node.GetType().Name;
                if (name == "TextContentLayer" || name == "TextRenderingLayer" || name == "WpfTextContentLayer")
                    return element;
            }

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int index = 0; index < count; index++)
            {
                FrameworkElement found = FindDescendant(VisualTreeHelper.GetChild(node, index), depth + 1, limit);
                if (found != null) return found;
            }

            return null;
        }

        /// <summary>
        /// Walks up from the text view to the control that is taller than the text viewport: that is the
        /// editor host, and it is the element whose background shows in the strip below the text.
        /// </summary>
        private static FrameworkElement FindHost(FrameworkElement element, double viewportHeight)
        {
            DependencyObject node = element;
            for (int i = 0; i < 4; i++)
            {
                if (node == null) return null;
                node = VisualTreeHelper.GetParent(node);
                var candidate = node as FrameworkElement;
                if (candidate == null) return null;
                if (candidate.ActualHeight > viewportHeight + 0.5 && candidate.ActualWidth > 0 &&
                    (candidate is Control || candidate is Panel || candidate is Border)) return candidate;
            }

            return null;
        }

        /// <summary>
        /// Builds what the shell actually paints: the theme colour with the picture drawn on top at
        /// the requested transparency.  A plain transparent brush is not usable here - the shell reads
        /// such a brush as opaque black - and a bare ImageBrush would blend with whatever sits behind
        /// the editor instead of the theme colour.
        /// </summary>
        private static Brush BuildEditorBrush(BitmapImage image, string fit, Brush baseBrush, double opacity)
        {
            double width = Math.Max(1, image.PixelWidth);
            double height = Math.Max(1, image.PixelHeight);
            var bounds = new Rect(0, 0, width, height);

            var drawing = new DrawingGroup();
            if (baseBrush != null)
            {
                drawing.Children.Add(new GeometryDrawing(baseBrush, null, new RectangleGeometry(bounds)));
            }

            var picture = new DrawingGroup { Opacity = opacity };
            picture.Children.Add(new ImageDrawing(image, bounds));
            drawing.Children.Add(picture);

            var brush = new DrawingBrush(drawing)
            {
                Stretch = EditorBackgroundRules.StretchFor(fit),
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };

            if (string.Equals(SettingsManager.EditorBackgroundSettings.NormalizeFit(fit), "Tile", StringComparison.Ordinal))
            {
                brush.TileMode = TileMode.Tile;
                brush.ViewportUnits = BrushMappingMode.Absolute;
                brush.Viewport = bounds;
            }

            try { brush.Freeze(); }
            catch (Exception) { }

            return brush;
        }

        /// <summary>
        /// The text renderer fills the viewport with the "TextView Background" entry, whose
        /// <c>Background</c> brush is what the shell actually paints.  That entry is the one and only
        /// place the picture may live: a transparent brush there reveals the black window behind the
        /// editor, and a second copy anywhere else is stretched into a different rectangle, which
        /// produces a seam and leaves a strip uncovered.  The per-run entries are made transparent so
        /// glyphs do not repaint over the picture.  Every original property bag is kept so disabling
        /// restores the theme.
        /// </summary>
        private static void ApplyFormatMap(ViewState state, Brush picture)
        {
            if (state.FormatMap == null) return;

            Dictionary<string, ResourceDictionary> originals = MapOriginals.GetValue(
                state.FormatMap, _ => new Dictionary<string, ResourceDictionary>(StringComparer.Ordinal));

            foreach (string key in EditorBackgroundRules.TransparentBackgroundKeys)
            {
                ResourceDictionary properties = state.FormatMap.GetProperties(key);
                if (properties == null) continue;

                ResourceDictionary saved;
                if (!originals.TryGetValue(key, out saved))
                {
                    // Snapshot once per map, before anything of ours is written into it.  A second
                    // view reusing this map must not record the painted values as the original.
                    saved = new ResourceDictionary();
                    foreach (DictionaryEntry entry in properties) saved[entry.Key] = entry.Value;
                    originals[key] = saved;
                }

                state.SavedProperties[key] = saved;

                properties["BackgroundColor"] = Colors.Transparent;

                if (properties.Contains("Background"))
                {
                    properties["Background"] = IsPictureKey(key) && picture != null
                        ? picture
                        : (Brush)Brushes.Transparent;
                }

                state.FormatMap.SetProperties(key, properties);
            }
        }

        private static bool IsPictureKey(string key)
        {
            return string.Equals(key, EditorBackgroundRules.TextViewBackgroundKey, StringComparison.Ordinal);
        }

        private static bool IsChromeExemption(string type)
        {
            if (string.IsNullOrEmpty(type)) return false;
            return type.IndexOf("Thumb", StringComparison.OrdinalIgnoreCase) >= 0
                || type.IndexOf("Popup", StringComparison.OrdinalIgnoreCase) >= 0
                || type.IndexOf("ToolTip", StringComparison.OrdinalIgnoreCase) >= 0
                || type.IndexOf("Adorner", StringComparison.OrdinalIgnoreCase) >= 0
                || type.IndexOf("Image", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void RestoreTextBackground(ViewState state)
        {
            if (state.FormatMap == null || state.SavedProperties.Count == 0) return;

            foreach (KeyValuePair<string, ResourceDictionary> pair in state.SavedProperties)
            {
                try { state.FormatMap.SetProperties(pair.Key, pair.Value); }
                catch (Exception ex) { MSSQLToolPackage._logger?.Error(ex, "Could not restore the editor background colour."); }
            }

            state.SavedProperties.Clear();

            // The map is back to the theme, so the next apply has to snapshot it again instead of
            // reusing the originals it was painted with.
            try { MapOriginals.Remove(state.FormatMap); }
            catch (Exception) { }
        }

        /// <summary>
        /// Finds the format map that paints the code area.  Several independent routes are tried and
        /// the winner is logged, because a single missing MEF export would otherwise look exactly
        /// like "the picture does not show".
        /// </summary>
        private static IEditorFormatMap GetFormatMap(IWpfTextView view, ViewState state)
        {
            if (state.FormatMap != null) return state.FormatMap;
            if (state.MapTried) return null;
            state.MapTried = true;

            IEditorFormatMap map = null;
            string error = null;
            string source = "none";

            if (FormatMapService != null)
            {
                map = TryGet(() => FormatMapService.GetEditorFormatMap(view), out error);
                if (map != null) source = "editor-format-map";
            }

            string classification = "-";
            if (ClassificationFormatMapService != null)
            {
                // IClassificationFormatMap is a different API in this shell (it exposes text properties,
                // not the format-item property bags), so it cannot stand in for the map.  Probing it is
                // still useful: a working classification map proves the editor services reach this part.
                try
                {
                    IClassificationFormatMap formatMap = ClassificationFormatMapService.GetClassificationFormatMap(view);
                    classification = formatMap != null ? "ok" : "null";
                }
                catch (Exception ex)
                {
                    classification = "error:" + ex.GetType().Name;
                }
            }

            if (map == null)
            {
                map = FindPublishedFormatMap(view);
                if (map != null) source = "view-properties";
            }

            state.FormatMap = map;
            state.MapSource = source;

            MSSQLToolPackage._logger?.Info(
                "SQL-BACKGROUND map: source=" + source +
                " type=" + (map == null ? "null" : map.GetType().FullName) +
                " services=" + DescribeServices() +
                " classification=" + classification +
                (error == null ? string.Empty : " error=" + error));

            return map;
        }

        private static IEditorFormatMap EnsureFormatMap(IWpfTextView view, ViewState state)
        {
            IEditorFormatMap map = GetFormatMap(view, state);
            if (map != null && !state.MapHooked)
            {
                try
                {
                    map.FormatMappingChanged += OnFormatMappingChanged;
                    state.MapHooked = true;
                }
                catch (Exception ex)
                {
                    MSSQLToolPackage._logger?.Error(ex, "Could not watch the editor format map.");
                }
            }

            return map;
        }

        private static IEditorFormatMap TryGet(Func<IEditorFormatMap> getter, out string error)
        {
            error = null;
            try { return getter(); }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>Last resort: some shells publish the map in the view or buffer property bag.</summary>
        private static IEditorFormatMap FindPublishedFormatMap(IWpfTextView view)
        {
            try
            {
                foreach (KeyValuePair<object, object> property in view.Properties.PropertyList)
                {
                    var map = property.Value as IEditorFormatMap;
                    if (map != null) return map;
                }
            }
            catch (Exception) { }

            try
            {
                ITextBuffer buffer = view.TextBuffer;
                if (buffer != null)
                {
                    foreach (KeyValuePair<object, object> property in buffer.Properties.PropertyList)
                    {
                        var map = property.Value as IEditorFormatMap;
                        if (map != null) return map;
                    }
                }
            }
            catch (Exception) { }

            return null;
        }

        private static void OnZoomLevelChanged(object sender, ZoomLevelChangedEventArgs e)
        {
            IWpfTextView view = sender as IWpfTextView;
            if (view == null || view.IsClosed) return;

            ViewState state;
            if (!States.TryGetValue(view, out state) || state.Applying) return;

            // The format layer is repainted at the new zoom immediately, so the host copy would keep
            // the old picture size until the next layout event without this.
            Apply(view);
        }

        private static void OnBackgroundBrushChanged(object sender, EventArgs e)
        {
            IWpfTextView view = sender as IWpfTextView;
            if (view == null || view.IsClosed) return;

            ViewState state = States.GetValue(view, v => new ViewState());
            if (state.Applying) return;
            if (ReferenceEquals(view.Background, state.AppliedBrush)) return;

            // The transparency we asked for makes the shell push a transparent brush onto the view;
            // that is our own doing and must never replace the theme colour we restore to.
            var brush = view.Background as SolidColorBrush;
            if (brush != null && brush.Color.A == 0) return;

            // Our own picture brush is not a theme colour either.  Treating it as one painted the
            // picture on top of itself, which is how the background got brighter on its own.
            if (IsOwnBrush(view.Background)) return;

            // A theme switch or another extension replaced the brush.  Remember the real background
            // so the feature can still be turned off cleanly, then paint the picture again.
            state.BaseBrush = view.Background;
            state.SavedProperties.Clear();
            if (state.FormatMap != null)
            {
                try { MapOriginals.Remove(state.FormatMap); }
                catch (Exception) { }
            }
            Apply(view);
        }

        private static void OnFormatMappingChanged(object sender, FormatItemsEventArgs e)
        {
            var map = sender as IEditorFormatMap;
            if (map == null) return;

            foreach (IWpfTextView view in OpenViews())
            {
                ViewState state;
                if (!States.TryGetValue(view, out state) || state.FormatMap == null || state.Applying) continue;
                if (!ReferenceEquals(state.FormatMap, map)) continue;
                if (!HasOpaqueBackground(state)) continue;

                // The theme has just reset the picture transparency: re-apply it.
                state.SavedProperties.Clear();
                try { MapOriginals.Remove(state.FormatMap); }
                catch (Exception) { }
                Apply(view);
            }
        }

        /// <summary>
        /// True when one of the transparent keys is opaque again.  Our own writes raise the same event,
        /// so without this check re-applying would feed itself.
        /// </summary>
        private static bool HasOpaqueBackground(ViewState state)
        {
            foreach (string key in EditorBackgroundRules.TransparentBackgroundKeys)
            {
                try
                {
                    ResourceDictionary properties = state.FormatMap.GetProperties(key);
                    if (properties == null) continue;
                    object value = properties["BackgroundColor"];
                    if (value is Color color && color.A != 0) return true;

                    // A theme reset can also restore an opaque Background brush under a transparent
                    // colour; our own picture brush is an ImageBrush, so a solid brush here is foreign.
                    var brush = properties["Background"] as SolidColorBrush;
                    if (brush != null && brush.Color.A != 0) return true;
                }
                catch (Exception) { }
            }

            return false;
        }

        /// <summary>Recomputes the host layer after a resize; it maps host coordinates 1:1.</summary>
        private static void OnVisualSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var element = sender as FrameworkElement;
            if (element == null) return;

            if (!(element is Control) && !(element is Panel) && !(element is Border)) return;

            foreach (IWpfTextView view in OpenViews())
            {
                ViewState state;
                if (!States.TryGetValue(view, out state) || state.Applying || !state.ImageApplied) continue;
                if (!ReferenceEquals(view.VisualElement, element) && !ReferenceEquals(state.Host, element)) continue;

                Apply(view);
                return;
            }
        }

        private static void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            IWpfTextView view = sender as IWpfTextView;
            if (view == null || view.IsClosed) return;

            ViewState state;
            if (!States.TryGetValue(view, out state) || state.Applying || !state.ImageApplied) return;

            // The first apply runs before the editor has a size, so neither the panel above the host
            // nor the host itself could be found yet.  The first real layout pass is when they become
            // reachable; a few attempts are enough and keep a shell without them from re-applying
            // forever.
            if (state.BackdropWanted && state.BackdropRoot == null && state.BackdropAttempts < 5)
            {
                state.BackdropAttempts++;
                Apply(view);
                return;
            }

            if (state.Host == null && state.HostAttempts < 4 && !string.Equals(state.Mode, "backdrop", StringComparison.Ordinal))
            {
                state.HostAttempts++;
                Apply(view);
                return;
            }

            // Some shells repaint the control background from the theme during a layout pass.  In
            // backdrop mode a transparent brush is what this feature wants, so comparing only by
            // reference would re-apply on every layout event while typing.
            var control = view.VisualElement as Control;
            Brush current = control != null ? control.Background : view.Background;
            if (ReferenceEquals(current, state.AppliedBrush)) return;
            var translucent = current as SolidColorBrush;
            if (translucent != null && translucent.Color.A == 0) return;

            Apply(view);
        }

        private static void OnViewClosed(object sender, EventArgs e)
        {
            IWpfTextView view = sender as IWpfTextView;
            if (view == null) return;

            ViewState state;
            if (States.TryGetValue(view, out state))
            {
                state.Applying = true;
                try
                {
                    RestoreTextBackground(state);
                    RestoreBackdrop(state);
                }
                catch (Exception)
                {
                }

                if (state.FormatMap != null)
                {
                    try { state.FormatMap.FormatMappingChanged -= OnFormatMappingChanged; }
                    catch (Exception) { }
                    state.MapHooked = false;
                }
            }

            try
            {
                view.BackgroundBrushChanged -= OnBackgroundBrushChanged;
                view.LayoutChanged -= OnLayoutChanged;
                view.ZoomLevelChanged -= OnZoomLevelChanged;
                view.Closed -= OnViewClosed;
                var element = view.VisualElement as FrameworkElement;
                if (element != null) element.SizeChanged -= OnVisualSizeChanged;
                if (state != null && state.Host != null) state.Host.SizeChanged -= OnVisualSizeChanged;
            }
            catch (Exception)
            {
            }

            lock (Gate)
            {
                Views.RemoveAll(reference =>
                {
                    IWpfTextView target;
                    return !reference.TryGetTarget(out target) || target == null || ReferenceEquals(target, view);
                });
            }
        }

        private static void Track(IWpfTextView view)
        {
            lock (Gate)
            {
                Views.RemoveAll(reference =>
                {
                    IWpfTextView target;
                    return !reference.TryGetTarget(out target) || target == null || ReferenceEquals(target, view) || target.IsClosed;
                });

                Views.Add(new WeakReference<IWpfTextView>(view));
            }
        }

        internal static List<IWpfTextView> OpenViews()
        {
            var open = new List<IWpfTextView>();
            lock (Gate)
            {
                for (int index = Views.Count - 1; index >= 0; index--)
                {
                    IWpfTextView view;
                    if (!Views[index].TryGetTarget(out view) || view == null || view.IsClosed) Views.RemoveAt(index);
                    else open.Add(view);
                }
            }

            return open;
        }

        /// <summary>Decodes once and keeps the frozen bitmap for every editor view.</summary>
        internal static BitmapImage LoadImage(string value)
        {
            Uri uri = EditorBackgroundRules.ResolveImageUri(value);
            if (uri == null) return null;

            string key = value.Trim();
            lock (Gate)
            {
                BitmapImage cached;
                if (Images.TryGetValue(key, out cached)) return cached;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.DecodePixelWidth = DecodeWidth;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = uri;
                bitmap.EndInit();
                bitmap.Freeze();

                lock (Gate) { Images[key] = bitmap; }
                return bitmap;
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Could not load the editor background image: " + key);
                return null;
            }
        }

        /// <summary>Decodes a small copy for the settings picker; a full 2560 px decode per tile would cost tens of megabytes.</summary>
        internal static BitmapImage LoadThumbnail(string value, int decodeWidth)
        {
            Uri uri = EditorBackgroundRules.ResolveImageUri(value);
            if (uri == null) return null;

            string key = value.Trim() + "|" + decodeWidth;
            lock (Gate)
            {
                BitmapImage cached;
                if (Thumbnails.TryGetValue(key, out cached)) return cached;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.DecodePixelWidth = decodeWidth;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = uri;
                bitmap.EndInit();
                bitmap.Freeze();

                lock (Gate) { Thumbnails[key] = bitmap; }
                return bitmap;
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Could not load the editor background thumbnail: " + key);
                return null;
            }
        }

        /// <summary>Drops the decoded cache, used when the user picks a different file at the same path.</summary>
        internal static void ClearImageCache()
        {
            lock (Gate) { Images.Clear(); Thumbnails.Clear(); }
        }
    }

    /// <summary>
    /// Attaches the background to every SQL editor document as it is created.  The content types and
    /// roles mirror the parts that are known to run in SSMS (context menu, diagnostics, ghost text):
    /// an SSMS query window does not necessarily answer to "SQL Server Tools" alone, and a listener
    /// that declares a content type nothing publishes is silently never called.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("SQL Server Tools")]
    [ContentType("SQL")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class EditorBackgroundViewListener : IWpfTextViewCreationListener
    {
        [Import(AllowDefault = true)]
        public IEditorFormatMapService FormatMapService { get; set; }

        [Import(AllowDefault = true)]
        public IClassificationFormatMapService ClassificationFormatMapService { get; set; }

        public void TextViewCreated(IWpfTextView textView)
        {
            try
            {
                MSSQLToolPackage._logger?.Info(
                    "SQL-BACKGROUND listener: view created contentType=" + SafeContentType(textView) +
                    " roles=" + SafeRoles(textView) +
                    " services=" + (FormatMapService != null ? "format" : "-") +
                    "/" + (ClassificationFormatMapService != null ? "classification" : "-"));

                EditorBackground.Attach(textView, FormatMapService, ClassificationFormatMapService);
            }
            catch (Exception ex)
            {
                MSSQLToolPackage._logger?.Error(ex, "Could not attach the editor background image.");
            }
        }

        private static string SafeContentType(IWpfTextView view)
        {
            try { return view == null || view.TextBuffer == null ? "?" : view.TextBuffer.ContentType.TypeName; }
            catch (Exception) { return "?"; }
        }

        private static string SafeRoles(IWpfTextView view)
        {
            try { return view == null || view.Roles == null ? "?" : view.Roles.ToString(); }
            catch (Exception) { return "?"; }
        }
    }
}
