using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FocusGarden.App;

internal sealed record TokenCalendarDay(DateTime Date, long? Tokens, bool IsFuture, int MonthColumn, int MonthRow, int Weekday, int Week, double X, double Z, double Height);

internal static class TokenCalendar3DGeometry
{
    internal const double MonthPitchX = 9.35;
    internal const double MonthPitchZ = 8.50;
    internal const double MonthWidth = 8.80;
    internal const double MonthDepth = 7.95;
    internal const double SceneWidth = MonthPitchX * 3 + MonthWidth;
    internal const double SceneDepth = MonthPitchZ * 2 + MonthDepth;

    internal static IReadOnlyList<TokenCalendarDay> BuildCalendar(int year, IReadOnlyDictionary<string, long> dailyTotals, DateTime today, bool squareRoot)
    {
        if (year is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(year));
        ArgumentNullException.ThrowIfNull(dailyTotals);
        var values = new Dictionary<DateTime, long>();
        foreach (var (key, value) in dailyTotals)
        {
            if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new ArgumentException("日期应为 yyyy-MM-dd。", nameof(dailyTotals));
            if (value < 0) throw new ArgumentException("Token 数不能为负数。", nameof(dailyTotals));
            if (date.Year == year) values[date] = value;
        }
        var maximum = values.Count == 0 ? 0 : values.Values.Max();
        var result = new List<TokenCalendarDay>(DateTime.IsLeapYear(year) ? 366 : 365);
        for (int month = 1; month <= 12; month++)
        {
            int column = (month - 1) % 4, row = (month - 1) / 4;
            int firstWeekday = (int)new DateTime(year, month, 1).DayOfWeek;
            for (int day = 1; day <= DateTime.DaysInMonth(year, month); day++)
            {
                var date = new DateTime(year, month, day);
                int weekday = (firstWeekday + day - 1) % 7, week = (firstWeekday + day - 1) / 7;
                long? tokens = values.TryGetValue(date, out var count) ? count : null;
                double fraction = maximum == 0 || !tokens.HasValue ? 0 : (double)tokens.Value / maximum;
                double height = !tokens.HasValue || tokens == 0 ? .08 : .20 + 4.20 * (squareRoot ? Math.Sqrt(fraction) : fraction);
                result.Add(new TokenCalendarDay(date, tokens, date.Date > today.Date, column, row, weekday, week,
                    column * MonthPitchX + .46 + weekday * 1.12,
                    row * MonthPitchZ + 1.75 + week * .95, height));
            }
        }
        return result;
    }

    internal static MeshGeometry3D Box(double x, double y, double z, double width, double height, double depth)
    {
        if (new[] { x, y, z, width, height, depth }.Any(n => double.IsNaN(n) || double.IsInfinity(n)) || width <= 0 || height <= 0 || depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "三维尺寸必须为正且有限。");
        double x1 = x + width, y1 = y + height, z1 = z + depth;
        var mesh = new MeshGeometry3D();
        AddFace(mesh, new(x, y1, z), new(x, y1, z1), new(x1, y1, z1), new(x1, y1, z), new(0, 1, 0));
        AddFace(mesh, new(x, y, z1), new(x, y, z), new(x1, y, z), new(x1, y, z1), new(0, -1, 0));
        AddFace(mesh, new(x, y, z1), new(x1, y, z1), new(x1, y1, z1), new(x, y1, z1), new(0, 0, 1));
        AddFace(mesh, new(x1, y, z), new(x, y, z), new(x, y1, z), new(x1, y1, z), new(0, 0, -1));
        AddFace(mesh, new(x1, y, z1), new(x1, y, z), new(x1, y1, z), new(x1, y1, z1), new(1, 0, 0));
        AddFace(mesh, new(x, y, z), new(x, y, z1), new(x, y1, z1), new(x, y1, z), new(-1, 0, 0));
        mesh.Freeze();
        return mesh;
    }

    internal static MeshGeometry3D LabelPlane(double x, double y, double z, double width, double depth)
    {
        var mesh = new MeshGeometry3D();
        AddFace(mesh, new(x, y, z), new(x, y, z + depth), new(x + width, y, z + depth), new(x + width, y, z), new(0, 1, 0));
        mesh.Freeze();
        return mesh;
    }

    private static void AddFace(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c, Point3D d, Vector3D normal)
    {
        int start = mesh.Positions.Count;
        foreach (var p in new[] { a, b, c, d }) { mesh.Positions.Add(p); mesh.Normals.Add(normal); }
        foreach (var p in new[] { new Point(0, 0), new Point(0, 1), new Point(1, 1), new Point(1, 0) }) mesh.TextureCoordinates.Add(p);
        foreach (var index in new[] { 0, 1, 2, 0, 2, 3 }) mesh.TriangleIndices.Add(start + index);
    }
}
