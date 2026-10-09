using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using System.Windows;

/// <summary>
/// Draws background/foreground highlights over an AvalonEdit editor. Quick Search uses it for
/// two layers at once: a subtle whole line marker plus a stronger marker for the matched text,
/// which is why <see cref="BeginUpdate"/> lets callers add many markers with a single redraw.
/// </summary>
public class TextMarkerService : DocumentColorizingTransformer, IBackgroundRenderer
{
    private readonly TextSegmentCollection<TextMarker> markers;
    private readonly TextEditor editor;
    private int updateDepth;

    public TextMarkerService(TextEditor editor)
    {
        this.editor = editor;
        markers = new TextSegmentCollection<TextMarker>(editor.Document);

        editor.TextArea.TextView.BackgroundRenderers.Add(this);
        editor.TextArea.TextView.LineTransformers.Add(this);
    }

    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>
    /// Batches <see cref="Create"/> calls so a document with hundreds of matches only redraws once.
    /// </summary>
    public void BeginUpdate()
    {
        updateDepth++;
    }

    public void EndUpdate()
    {
        if (updateDepth > 0) updateDepth--;
        if (updateDepth == 0)
        {
            editor.TextArea.TextView.Redraw();
        }
    }

    public TextMarker Create(int startOffset, int length)
    {
        var marker = new TextMarker(startOffset, length);
        markers.Add(marker);
        if (updateDepth == 0)
        {
            editor.TextArea.TextView.Redraw();
        }

        return marker;
    }

    public TextMarker Create(int startOffset, int length, Color background, Color? foreground = null)
    {
        var marker = Create(startOffset, length);
        marker.BackgroundColor = background;
        marker.ForegroundColor = foreground;
        return marker;
    }

    public void RemoveAll()
    {
        if (markers.Count == 0) return;

        markers.Clear();
        if (updateDepth == 0)
        {
            editor.TextArea.TextView.Redraw();
        }
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (markers == null || markers.Count == 0)
            return;

        foreach (var marker in markers)
        {
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, marker))
            {
                var brush = new SolidColorBrush(marker.BackgroundColor);
                brush.Freeze();
                drawingContext.DrawRectangle(brush, null, rect);
            }
        }
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        foreach (var marker in markers.FindOverlappingSegments(line))
        {
            if (marker.ForegroundColor == null) continue;

            ChangeLinePart(
                marker.StartOffset,
                marker.EndOffset,
                element =>
                {
                    element.TextRunProperties.SetForegroundBrush(
                        new SolidColorBrush(marker.ForegroundColor.Value));
                });
        }
    }

    public class TextMarker : TextSegment
    {
        public TextMarker(int startOffset, int length)
        {
            StartOffset = startOffset;
            Length = length;
        }

        public Color BackgroundColor { get; set; } = Colors.Yellow;
        public Color? ForegroundColor { get; set; }
    }
}
