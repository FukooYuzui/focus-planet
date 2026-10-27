using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FocusGarden.Core;

namespace FocusGarden.App;

public partial class MainWindow
{
    private PlanetGrowthStore? planetStore;
    private PlanetLayoutStore? planetLayout;
    private PlanetView3D? planetView;
    private PlanetSnapshot? planetSnapshot;
    private Dictionary<string, long> eligiblePlanetTokens = [];
    private IReadOnlyList<PlanetBuildingPlacement> planetBuildings = [];
    private long planetTick;
    private long displayedPlanetId;
    private int displayedPlanetLevel = -1;
    private int? selectedPlanetPlot;
    private bool travellingPlanet;
    private string? placingPlanetKind, movingPlanetBuilding;
    private string PlanetKey => planetSnapshot!.PlanetId.ToString(CultureInfo.InvariantCulture);

    private void InitializePlanet()
    {
        planetStore = new PlanetGrowthStore(Path.Combine(App.DataDirectory, "planet.db"));
        planetStore.Initialize(DateTimeOffset.Now, store.GetAllDailyTotals());
        planetLayout = new PlanetLayoutStore(Path.Combine(App.DataDirectory, "planet-layout.db"));
        planetView = new PlanetView3D();
        planetView.PlotClicked += PlanetPlotClicked;
        PlanetSceneHost.Content = planetView;
        PlanetRoadmapText.Text = "荒凉初生 → 森林萌芽（150 点）→ 生命栖息（1 个月 / 1,200 点）\n→ 定居家园（2 个月 / 3,000 点）→ 风能文明（3 个月 / 5,500 点）\n→ 星际绿洲（5 个月 / 9,000 点）→ 6 个月后可开拓新星球。\n阶段同时需要成长值和星龄。成长较慢时可以继续建设，没有到期清空。";
        ReloadPlanetTokens(); RefreshPlanet();
        Page("planet");
    }

    private void ReloadPlanetTokens()
    {
        if (planetStore == null || !dashboardReady || dashboardClosed) return;
        var source = tokenImporter.ActiveSource;
        if (source == null) return;
        // Binding a different source clears its observation high-water. Never replay an old cache
        // if the following query fails after that binding was persisted.
        eligiblePlanetTokens = [];
        var now = DateTimeOffset.Now;
        var cutoff = planetStore.GetTokenPeriodStart(source, now);
        eligiblePlanetTokens = tokenImporter.GetDailyTotalsSince(cutoff, now);
    }

    private void PlanetTick()
    {
        if (planetStore == null || shuttingDown) return;
        PlanetClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        PlanetFocusStatus.Text = clock == null ? "下一段专注，将成为这颗星球的养分。" : breaking ? "正在休息 · 小星球静静陪着你" : $"专注中  {FormatTime(clock.RemainingSeconds)} · {currentTask}";
        if (Stopwatch.GetElapsedTime(planetTick).TotalSeconds < 5) return;
        RefreshPlanet();
    }

    private bool RefreshPlanet()
    {
        if (planetStore == null || planetLayout == null || planetView == null || shuttingDown) return false;
        planetTick = Stopwatch.GetTimestamp();
        try
        {
            planetSnapshot = planetStore.Update(DateTimeOffset.Now, store.GetAllDailyTotals(), eligiblePlanetTokens);
            var s = planetSnapshot;
            bool changedPlanet = displayedPlanetId != s.PlanetId;
            bool changedLevel = displayedPlanetLevel != s.Level;
            PlanetNameText.Text = s.Name;
            PlanetLevelText.Text = $"Lv.{s.Level}  ·  {s.LevelName}  ·  星龄 {s.AgeDays + 1} 天";
            PlanetWeatherText.Text = (s.Weather switch { "clear" => "晴朗", "cloudy" => "多云", "rain" => "轻雨", _ => "极光" }) + (s.IsNight ? " · 夜晚" : " · 白昼");
            PlanetGrowthText.Text = $"累计 {s.TotalGrowth:N0} 点";
            PlanetCycleText.Text = $"{s.ActivatedAt.ToLocalTime():yyyy.MM.dd} 启程  ·  {s.CycleEndsAt.ToLocalTime():yyyy.MM.dd} 开放探索";
            long start = PlanetGrowthRules.LevelThresholds[s.Level];
            PlanetGrowthProgress.Maximum = s.NextLevelAt.HasValue ? Math.Max(1, s.NextLevelAt.Value - start) : 1;
            PlanetGrowthProgress.Value = s.NextLevelAt.HasValue ? Math.Clamp(s.TotalGrowth - start, 0, s.NextLevelAt.Value - start) : 1;
            PlanetNextText.Text = s.NextLevelAt.HasValue
                ? $"下一阶段：{PlanetGrowthRules.GetLevelName(s.Level + 1)} · 还需 {Math.Max(0, s.NextLevelAt.Value - s.TotalGrowth):N0} 点" + (s.NextLevelAvailableAt > DateTimeOffset.Now ? $" · {s.NextLevelAvailableAt.Value.ToLocalTime():yyyy.MM.dd} 起解锁" : "")
                : "你的星球已成为星际绿洲。继续布置家园，或在周期结束后探索新世界。";
            PlanetFocusPointsText.Text = $"+{s.TodayFocusPoints}";
            PlanetTokenPointsText.Text = $"+{s.TodayTokenPoints}";
            PlanetSynergyPointsText.Text = $"+{s.TodaySynergyPoints}";
            PlanetFocusDetailText.Text = "新增专注 " + Duration(s.TodayFocusSeconds);
            PlanetTokenDetailText.Text = "新增用量 " + TokenNumber(s.TodayTokens);
            PlanetTodayGrowthText.Text = $"今日共 {s.TodayGrowth} 点 · 本星球 +{s.TodayPlanetGrowth}";
            ExplorePlanetButton.IsEnabled = s.CanExplore && clock == null && !importingTokens;
            PlanetExploreHint.Text = s.CanExplore ? "周期已满，可以启程。" : DateTimeOffset.Now < s.CycleEndsAt ? $"还需 {Math.Max(1, Math.Ceiling((s.CycleEndsAt - DateTimeOffset.Now).TotalDays))} 天" : "已从这颗星球启程过，可在档案中切换。";
            planetView.SetState(s.Level, s.Weather, clock != null && !breaking, s.IsNight, s.Seed);
            if (changedPlanet)
            {
                displayedPlanetId = s.PlanetId;
                PlanetNameInput.Text = s.Name;
                selectedPlanetPlot = null; placingPlanetKind = null; movingPlanetBuilding = null;
                PlanetSceneHost.Height = 470;
                PlanetEditCheck.IsChecked = false; planetView.SetEditMode(false); planetView.ResetView();
                RefreshPlanetBuildings(); RefreshPlanetSelector();
            }
            if (changedPlanet || changedLevel)
            {
                displayedPlanetLevel = s.Level;
                var choice = PlanetBuildingChoice.SelectedValue as string;
                PlanetBuildingChoice.ItemsSource = PlanetLayoutCatalog.Entries.Where(d => d.RequiredLevel <= s.Level).Select(d => new BuildingChoice(d.Kind, $"{d.Name} · 最多 {d.MaxCount} 个")).ToArray();
                PlanetBuildingChoice.DisplayMemberPath = nameof(BuildingChoice.Label); PlanetBuildingChoice.SelectedValuePath = nameof(BuildingChoice.Kind);
                PlanetBuildingChoice.SelectedValue = choice;
                if (PlanetBuildingChoice.SelectedIndex < 0) PlanetBuildingChoice.SelectedIndex = 0;
                if (s.Level == 0) PlanetLayoutHint.Text = "星球还很荒凉。累计 150 点后解锁树木和花朵，再逐步建设家园。";
            }
            UpdatePlanetLayoutButtons();
            return true;
        }
        catch (Exception ex) { Status("星球暂时无法更新，已有专注记录仍然保留：" + ex.Message); return false; }
    }

    private void RefreshPlanetBuildings()
    {
        if (planetSnapshot == null || planetLayout == null) return;
        planetBuildings = planetLayout.GetBuildings(PlanetKey);
        planetView?.SetBuildings(planetBuildings);
    }

    private void RefreshPlanetSelector()
    {
        if (planetStore == null || planetSnapshot == null) return;
        PlanetSelector.ItemsSource = planetStore.ListPlanets().Select(p => new PlanetChoice(p.PlanetId, $"{p.Name} · {p.TotalGrowth:N0} 点")).ToArray();
        PlanetSelector.DisplayMemberPath = nameof(PlanetChoice.Label); PlanetSelector.SelectedValuePath = nameof(PlanetChoice.Id);
        PlanetSelector.SelectedValue = planetSnapshot.PlanetId;
    }

    private void ShowPlanet(object sender, RoutedEventArgs e) { RefreshPlanet(); Page("planet"); }
    private void RenamePlanet(object sender, RoutedEventArgs e) => PlanetAction(() => { planetStore!.SetName(PlanetNameInput.Text); RefreshPlanet(); RefreshPlanetSelector(); Status("星球名字已保存。"); });
    private async void SelectPlanet(object sender, RoutedEventArgs e)
    {
        if (clock != null || importingTokens) { Status("请在本轮结束、用量更新完成后切换星球，让助力准确入账。"); return; }
        if (PlanetSelector.SelectedValue is not long id) return;
        await TravelPlanetAsync(() => { planetStore!.SelectPlanet(id); RefreshPlanet(); Status("已前往 " + planetSnapshot!.Name + "。后续成长将帮助当前星球。"); });
    }
    private async void ExplorePlanet(object sender, RoutedEventArgs e)
    {
        if (clock != null || importingTokens) { Status("请在本轮结束、用量更新完成后启程。"); return; }
        await TravelPlanetAsync(() => { planetStore!.ExploreNewPlanet(DateTimeOffset.Now); RefreshPlanet(); Status("新星球已发现，旧家园保留在星球档案中。"); });
    }
    private async Task TravelPlanetAsync(Action travel)
    {
        if (travellingPlanet) return;
        travellingPlanet = true;
        try
        {
            // Settle outstanding local logs before changing which planet receives new growth.
            if (tokenImporter.ActiveSource != null || Directory.Exists(dashboardPreferences.ResolveCodexHome()))
            {
                Status("启程前正在结算当前星球的专注和 Token 助力…");
                if (!await ImportTokensAsync()) { if (!dashboardClosed) Status("用量未完整更新，本次仍留在原星球。请完成刷新后再次启程。"); return; }
            }
            if (shuttingDown || dashboardClosed) return;
            if (clock != null) { Status("专注已开始，本次仍留在原星球。请在结束后启程。"); return; }
            if (!RefreshPlanet()) { Status("星球助力未能保存，本次仍留在原星球。请检查数据目录后再试。"); return; }
            PlanetAction(travel);
        }
        finally { travellingPlanet = false; }
    }

    private void TogglePlanetEdit(object sender, RoutedEventArgs e)
    {
        bool enabled = PlanetEditCheck.IsChecked == true;
        planetView?.SetEditMode(enabled);
        PlanetSceneHost.Height = enabled ? Math.Clamp(Math.Max(ActualHeight, Height) - 550, 350, 410) : 470;
        placingPlanetKind = null; movingPlanetBuilding = null; selectedPlanetPlot = null;
        PlanetLayoutHint.Text = enabled ? "选择一种景观或建筑并点击放置，也可以点击已有建筑所在的地块进行调整。" : "布局已自动保存。拖动星球可以查看各处活动。";
        UpdatePlanetLayoutButtons();
    }
    private void StartPlanetPlacement(object sender, RoutedEventArgs e)
    {
        if (PlanetBuildingChoice.SelectedItem is not BuildingChoice choice) return;
        PlanetEditCheck.IsChecked = true; planetView?.SetEditMode(true);
        PlanetSceneHost.Height = Math.Clamp(Math.Max(ActualHeight, Height) - 550, 350, 410);
        placingPlanetKind = choice.Kind; movingPlanetBuilding = null;
        PlanetLayoutHint.Text = "请在星球上点击一个空地块放置。可拖动星球寻找位置。";
        UpdatePlanetLayoutButtons();
    }
    private PlanetBuildingPlacement? SelectedPlanetBuilding => planetBuildings.FirstOrDefault(b => b.PlotId == selectedPlanetPlot);
    private void PlanetPlotClicked(int plot) => PlanetAction(() =>
    {
        if (planetSnapshot == null || PlanetEditCheck.IsChecked != true) return;
        if (movingPlanetBuilding != null)
        {
            try { planetLayout!.Move(PlanetKey, movingPlanetBuilding, plot); }
            catch { planetView?.SelectPlot(selectedPlanetPlot); throw; }
            movingPlanetBuilding = null; selectedPlanetPlot = plot;
            RefreshPlanetBuildings(); PlanetLayoutHint.Text = $"已移动到 {plot + 1} 号地块，布局已保存。";
        }
        else if (placingPlanetKind != null)
        {
            try { planetLayout!.Place(PlanetKey, placingPlanetKind, plot, planetSnapshot.Level); }
            catch { planetView?.SelectPlot(selectedPlanetPlot); throw; }
            placingPlanetKind = null; selectedPlanetPlot = plot;
            RefreshPlanetBuildings(); PlanetLayoutHint.Text = $"已放置在 {plot + 1} 号地块，布局已保存。";
        }
        else
        {
            selectedPlanetPlot = plot;
            var building = SelectedPlanetBuilding;
            PlanetLayoutHint.Text = building == null ? $"已选择 {plot + 1} 号空地块。选择建筑并点击“放置”。" : $"已选中 {PlanetLayoutCatalog.Entries.First(d => d.Kind == building.Kind).Name} · {plot + 1} 号地块 · 朝向 {building.QuarterTurns * 90}°";
        }
        UpdatePlanetLayoutButtons();
    });
    private void MovePlanetBuilding(object sender, RoutedEventArgs e)
    {
        if (SelectedPlanetBuilding is not { } building) return;
        movingPlanetBuilding = building.Id; placingPlanetKind = null;
        PlanetLayoutHint.Text = "移动中：点击一个空地块。原位置会保留到移动成功。";
    }
    private void RotatePlanetBuilding(object sender, RoutedEventArgs e) => PlanetAction(() =>
    {
        if (SelectedPlanetBuilding is not { } building) return;
        var rotated = planetLayout!.Rotate(PlanetKey, building.Id); RefreshPlanetBuildings();
        PlanetLayoutHint.Text = $"朝向已旋转为 {rotated.QuarterTurns * 90}°，布局已保存。";
    });
    private void RemovePlanetBuilding(object sender, RoutedEventArgs e) => PlanetAction(() =>
    {
        if (SelectedPlanetBuilding is not { } building) return;
        planetLayout!.Remove(PlanetKey, building.Id); movingPlanetBuilding = null; placingPlanetKind = null;
        RefreshPlanetBuildings(); UpdatePlanetLayoutButtons(); PlanetLayoutHint.Text = "已收起，之后可重新放置，不消耗成长值。";
    });
    private void UpdatePlanetLayoutButtons()
    {
        var editing = PlanetEditCheck.IsChecked == true;
        PlanetPlaceButton.IsEnabled = PlanetBuildingChoice.Items.Count > 0;
        PlanetMoveButton.IsEnabled = PlanetRotateButton.IsEnabled = PlanetRemoveButton.IsEnabled = editing && SelectedPlanetBuilding != null;
    }
    private void PlanetAction(Action action)
    {
        try { action(); }
        catch (Exception ex) { PlanetLayoutHint.Text = ex.Message; Status(ex.Message); }
    }
    private void ClosePlanet() { planetLayout?.Dispose(); planetStore?.Dispose(); }
    private sealed record BuildingChoice(string Kind, string Label) { public override string ToString() => Label; }
    private sealed record PlanetChoice(long Id, string Label) { public override string ToString() => Label; }
}
