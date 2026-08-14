namespace TamizChat.Controls;

/// <summary>
/// The subdivision the whole app lays things out with: one item fills the space,
/// two take half each, three and four take a quarter each, and from five on the
/// cells stay quarter-sized and the container scrolls.
///
/// Used for the rooms on a server and for the people inside a room, so both read
/// the same way and neither shrinks into stamps on a busy server.
/// </summary>
public static class TileLayout
{
    public readonly record struct Slot(double X, double Y, double Width, double Height);

    /// <summary>Columns, and how many rows fit before scrolling starts.</summary>
    public static (int Columns, int RowsInView) Shape(int count) => count switch
    {
        <= 1 => (1, 1),
        2 => (2, 1),
        _ => (2, 2),
    };

    public static Slot[] Arrange(int count, double width, double height, double gap)
    {
        if (count <= 0 || width <= 0 || height <= 0)
        {
            return [];
        }

        var (columns, rowsInView) = Shape(count);
        var cellWidth = (width - (gap * (columns - 1))) / columns;
        var cellHeight = (height - (gap * (rowsInView - 1))) / rowsInView;

        var slots = new Slot[count];
        for (var i = 0; i < count; i++)
        {
            var column = i % columns;
            var row = i / columns;
            slots[i] = new Slot(
                column * (cellWidth + gap),
                row * (cellHeight + gap),
                Math.Max(0, cellWidth),
                Math.Max(0, cellHeight));
        }

        return slots;
    }

    /// <summary>
    /// How tall the content is once the extra rows are included — which is what
    /// makes the container scroll rather than squeeze.
    /// </summary>
    public static double ContentHeight(int count, double height, double gap)
    {
        if (count <= 0 || height <= 0)
        {
            return 0;
        }

        var (columns, rowsInView) = Shape(count);
        var cellHeight = (height - (gap * (rowsInView - 1))) / rowsInView;
        var rows = (int)Math.Ceiling(count / (double)columns);
        return (rows * cellHeight) + ((rows - 1) * gap);
    }
}
