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
    /// Places the completion popup below the caret line so the text being typed stays visible.
    ///
    /// The popup is put on the side of the caret line that has more room, and its height is capped
    /// to that room, which is what keeps the caret line uncovered. Merely clamping a tall popup
    /// into the work area (the previous behaviour) pushed it back over the caret whenever the caret
    /// sat low on the screen.  The layout is pure arithmetic so it can be verified independently of
    /// the editor.
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
        /// Computes the popup geometry for a caret whose line starts at
        /// <paramref name="caretLineTop"/> and is <paramref name="lineHeight"/> pixels tall.
        /// </summary>
        internal static CompletionPopupLayout Resolve(Rectangle workArea, Size preferred,
            Size preferredMinimum, Point caretLineTop, int lineHeight)
        {
            int line = Math.Max(1, lineHeight);

            // Preferred width, clamped to the work area.
            int width = Math.Max(1, Math.Min(preferred.Width, workArea.Width));
            int minimumWidth = Math.Min(preferredMinimum.Width, width);

            // Geometry of the two candidate sides.
            int belowTop = caretLineTop.Y + line + Gap;
            int roomBelow = workArea.Bottom - belowTop;
            int roomAbove = caretLineTop.Y - Gap - workArea.Top;

            bool placeBelow = roomBelow >= roomAbove;
            int room = Math.Max(0, placeBelow ? roomBelow : roomAbove);

            // Cap the height to the room on the chosen side; this is what guarantees the caret line
            // stays visible. The minimum is lowered as far as necessary so the cap always applies.
            int height = Math.Max(1, Math.Min(preferred.Height, Math.Max(room, MinimumUsableHeight)));
            if (height > room) height = Math.Max(1, room);

            int minimumHeight = Math.Min(preferredMinimum.Height, height);
            if (minimumHeight < MinimumUsableHeight) minimumHeight = Math.Min(MinimumUsableHeight, height);

            int y = placeBelow ? belowTop : caretLineTop.Y - Gap - height;

            // Keep the popup inside the work area. Because the height was capped to the available
            // room, this can no longer drag the popup across the caret line.
            y = Math.Max(workArea.Top, Math.Min(y, workArea.Bottom - height));

            int x = Math.Max(workArea.Left, Math.Min(caretLineTop.X, workArea.Right - width));

            return new CompletionPopupLayout
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                MinimumSize = new Size(minimumWidth, minimumHeight)
            };
        }
    }
}
