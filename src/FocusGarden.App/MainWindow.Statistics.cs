using FocusGarden.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FocusGarden.App;

public partial class MainWindow
{
    private Task statisticsTask = Task.CompletedTask;
    private bool statisticsRequested, historyRendered;
    private long renderedStatisticsRevision = -1;
    private int renderedStatisticsYear = -1, heatmapYear = -1;
    private DateTime renderedStatisticsDate, heatmapToday;
    private FocusSession[] renderedHistory = [];
    private readonly Dictionary<string, Border> heatmapCells = new();
    private readonly Dictionary<string, int> heatmapValues = new();
    private static readonly Brush[] heatmapBrushes = [MakeHeatmapBrush("#26383E"), MakeHeatmapBrush("#204E3B"), MakeHeatmapBrush("#357450"), MakeHeatmapBrush("#62AB74"), MakeHeatmapBrush("#A6E3BA")];
    private static Brush MakeHeatmapBrush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    private static Brush HeatmapBrush(int seconds) => heatmapBrushes[seconds == 0 ? 0 : seconds < 1500 ? 1 : seconds < 3000 ? 2 : seconds < 6000 ? 3 : 4];
    private bool CanRenderStatistics => !shuttingDown && WindowState != WindowState.Minimized && immersion == null && (FocusPage.Visibility == Visibility.Visible || StatsPage.Visibility == Visibility.Visible);

    private void RefreshStatistics()
    {
        if (shuttingDown) return;
        statisticsRequested = true;
        if (!CanRenderStatistics || !statisticsTask.IsCompleted) return;
        statisticsTask = RefreshStatisticsAsync();
    }

    private async Task RefreshStatisticsAsync()
    {
        while (statisticsRequested && CanRenderStatistics)
        {
            statisticsRequested = false;
            var requestedYear = year; var today = DateTime.Today; var revision = Volatile.Read(ref focusDataRevision);
            if (renderedStatisticsRevision == revision && renderedStatisticsYear == requestedYear && renderedStatisticsDate == today) continue;
            try
            {
                var snapshot = await focusWork.RunAsync(() => store.GetStatistics(requestedYear, today));
                if (!CanRenderStatistics) { statisticsRequested = true; return; }
                if (year != requestedYear || DateTime.Today != today) { statisticsRequested = true; continue; }
                ApplyStatistics(snapshot);
                renderedStatisticsRevision = revision; renderedStatisticsYear = requestedYear; renderedStatisticsDate = today;
            }
            catch (Exception error) { if (!shuttingDown) Status("统计暂未刷新，已保存的记录保留：" + error.Message); }
        }
    }
}
