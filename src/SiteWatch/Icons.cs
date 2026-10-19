using System.Drawing.Drawing2D;

namespace SiteWatch;

/// <summary>Значки для трея рисуются кодом: кружок цвета состояния.</summary>
public static class Icons
{
    private static readonly Dictionary<OverallState, Icon> Cache = new();

    public static Icon For(OverallState state)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(state, out var icon))
            {
                icon = Draw(ColorFor(state), state);
                Cache[state] = icon;
            }
            return icon;
        }
    }

    public static Color ColorFor(OverallState state) => state switch
    {
        OverallState.AllOk => Color.FromArgb(34, 160, 80),
        OverallState.Warning => Color.FromArgb(230, 160, 20),
        OverallState.Down => Color.FromArgb(215, 40, 40),
        OverallState.NoInternet => Color.FromArgb(120, 90, 200),
        OverallState.Paused => Color.FromArgb(130, 130, 130),
        _ => Color.FromArgb(90, 140, 210),
    };

    private static Icon Draw(Color color, OverallState state)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            using var border = new Pen(Color.FromArgb(220, 255, 255, 255), 2f);
            g.FillEllipse(fill, 2, 2, 28, 28);
            g.DrawEllipse(border, 2, 2, 28, 28);

            using var mark = new Pen(Color.White, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            switch (state)
            {
                case OverallState.AllOk:
                    g.DrawLines(mark, new[] { new PointF(9, 16.5f), new PointF(14, 21.5f), new PointF(23, 11) });
                    break;
                case OverallState.Down:
                    g.DrawLine(mark, 11, 11, 21, 21);
                    g.DrawLine(mark, 21, 11, 11, 21);
                    break;
                case OverallState.Warning:
                case OverallState.NoInternet:
                    g.DrawLine(mark, 16, 9, 16, 18);
                    g.DrawLine(mark, 16, 23, 16, 23.2f);
                    break;
                case OverallState.Paused:
                    g.DrawLine(mark, 12.5f, 10, 12.5f, 22);
                    g.DrawLine(mark, 19.5f, 10, 19.5f, 22);
                    break;
            }
        }

        // Значков всего шесть, они кэшируются — хэндлы живут до выхода программы.
        return Icon.FromHandle(bmp.GetHicon());
    }
}
