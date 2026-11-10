using System;
using System.Drawing;

namespace MSSQLTool.Completion
{
    /// <summary>Where the completion popup should go, and how large it may become.</summary>
    internal struct CompletionPopupLayout
    {
        public Point Location;
        public Size Size;
        /// <summary>The form's minimum size, reduced when the screen space requires it.</summary>
        public Size MinimumSize;
    }

    /// <summary>
    /// Places the completion popup on the side of the caret line that has more room.  Its left
    /// edge starts at the caret and is only moved left when the preferred width would leave the
    /// monitor's working area.  The height is capped to the chosen side, so the caret line stays
    /// visible even when the caret sits low on the screen.  The layout is pure arithmetic so it can
    /// be verified independently of the editor.
    /// </summary>
    internal static class CompletionPopupPlacement
    {
        /// <summary>Empty pixels kept between the popup and the caret line.</summary>
        internal const int Gap = 4;

        /// <summary>Smallest popup height that still shows something useful.</summary>
        internal const int MinimumUsableHeight = 120;

        /// <summary>Smallest popup width that still shows something useful.</summary>
        internal const int MinimumUsableWidth = 320;

        /// <summary>
        /// Computes the popup geometry for a caret whose visual top-left is
        /// <paramref name="caretPoint"/> and whose line is <paramref name="lineHeight"/> pixels tall.
        /// </summary>
        internal static CompletionPopupLayout Resolve(Rectangle workArea, Size preferred,
            Size preferredMinimum, Point caretPoint, int lineHeight)
        {
            int line = Math.Max(1, lineHeight);

            // Preferred width, clamped to the work area.  A popup wider than the monitor is
            // narrowed only when there is no other way to keep it inside the working area.
            int width = Math.Max(1, Math.Min(preferred.Width, workArea.Width));
            int minimumWidth = Math.Min(preferredMinimum.Width, width);

            // Geometry of the two candidate sides.  caretPoint is the *visual* caret position, so
            // this also works when SSMS wraps a long line onto several screen rows.
            int belowTop = caretPoint.Y + line + Gap;
            int roomBelow = workArea.Bottom - belowTop;
            int roomAbove = caretPoint.Y - Gap - workArea.Top;

            bool belowFits = roomBelow >= MinimumUsableHeight;
            bool aboveFits = roomAbove >= MinimumUsableHeight;
            // Prefer below the caret, but only when that side has usable room.  When the caret is
            // near the bottom of the screen, flipping above keeps a real list visible instead of
            // squeezing a 40-pixel strip below the line.
            bool placeBelow = belowFits != aboveFits ? belowFits : roomBelow >= roomAbove;
            int room = Math.Max(0, placeBelow ? roomBelow : roomAbove);

            int height = Math.Max(1, Math.Min(preferred.Height, room > 0 ? room : preferred.Height));
            if (height > room) height = Math.Max(1, room);

            int minimumHeight = Math.Min(preferredMinimum.Height, height);
            if (minimumHeight < MinimumUsableHeight) minimumHeight = Math.Min(MinimumUsableHeight, height);

            int y = placeBelow ? belowTop : caretPoint.Y - Gap - height;

            // Keep the popup inside the work area. Because the height was capped to the available
            // room, this can no longer drag the popup across the caret line.
            y = Math.Max(workArea.Top, Math.Min(y, workArea.Bottom - height));

            // The left edge starts at the caret.  If the preferred width would cross the monitor's
            // right edge, shift the whole popup left; the width is kept unless it cannot fit on the
            // monitor at all.
            int x = Math.Max(workArea.Left, Math.Min(caretPoint.X, workArea.Right - width));

            return new CompletionPopupLayout
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                MinimumSize = new Size(minimumWidth, minimumHeight)
            };
        }
    }
}
