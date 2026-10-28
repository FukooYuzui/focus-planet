using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using FocusGarden.Core;

namespace FocusGarden.App;

public partial class MainWindow
{
    private readonly SerialWorkQueue planetWork = new();
    private readonly SemaphoreSlim planetOperation = new(1, 1);
    private Task<bool>? planetRefreshTask;
    private Task planetActionTask = Task.CompletedTask;
    private Task? planetCloseTask;
    private Action? pendingClosePlanetMutation;
    private Exception? pendingClosePlanetError;
    private bool planetCloseFailureReported;
    private bool planetClosed, planetDisposed, planetMutationPending, suppressPlanetEdit, planetPresentationDirty;
    private PlanetPresentation? cachedPlanetPresentation;
    private Dictionary<string, int> planetFocusTotals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> appliedPlanetFocus = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> appliedPlanetTokens = new(StringComparer.Ordinal);
    private string? appliedPlanetTokenSource;
    private long planetFocusRevision = -1;
    private DateTime planetFocusDate = DateTime.MinValue;

    private sealed record PlanetPresentation(PlanetSnapshot Snapshot,
        IReadOnlyList<PlanetBuildingPlacement> Buildings, IReadOnlyList<PlanetSummary> Planets);

    // This method is called only during initial construction or on planetWork.
    private PlanetPresentation ReadPlanetPresentation(PlanetSnapshot snapshot) => new(snapshot,
        planetLayout!.GetBuildings(snapshot.PlanetId.ToString(CultureInfo.InvariantCulture)), planetStore!.ListPlanets());

    private void RefreshPlanet() => _ = RefreshPlanetAsync();

    private Task<bool> RefreshPlanetAsync()
    {
        if (planetClosed || shuttingDown || planetStore == null || planetView == null) return Task.FromResult(false);
        if (planetRefreshTask is { IsCompleted: false }) return planetRefreshTask;
        return planetRefreshTask = RefreshPlanetCoreAsync(false);
    }

    // Used after intentional out-of-band fixture changes. Ordinary application writes use focusDataRevision.
    private async Task<bool> ForceRefreshPlanetAsync()
    {
        if (planetRefreshTask is { IsCompleted: false } running) await running;
        if (planetClosed || shuttingDown || planetStore == null) return false;
        return await (planetRefreshTask = RefreshPlanetCoreAsync(true));
    }

    private async Task<bool> RefreshPlanetCoreAsync(bool fullFocusRead)
    {
        planetTick = Stopwatch.GetTimestamp();
        await planetOperation.WaitAsync();
        try
        {
            if (planetClosed || shuttingDown) return false;
            var now = DateTimeOffset.Now;
            await ReadPlanetFocusAsync(now.LocalDateTime.Date, fullFocusRead);
            if (planetClosed || shuttingDown) return false;
            var today = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var focusChanges = planetFocusTotals.Where(p => string.CompareOrdinal(p.Key, today) <= 0 &&
                (!appliedPlanetFocus.TryGetValue(p.Key, out var old) || old != p.Value)).ToDictionary(p => p.Key, p => p.Value);
            var tokenChanges = eligiblePlanetTokens.Where(p => string.CompareOrdinal(p.Key, today) <= 0 &&
                (!appliedPlanetTokens.TryGetValue(p.Key, out var old) || old != p.Value)).ToDictionary(p => p.Key, p => p.Value);
            var presentation = await planetWork.RunAsync(() => ReadPlanetPresentation(
                focusChanges.Count == 0 && tokenChanges.Count == 0
                    ? planetStore!.GetSnapshot(now)
                    : planetStore!.Update(now, focusChanges, tokenChanges)));
            // Only successful transactions advance our observation cache. A failed refresh retries the same deltas.
            RememberGrowthObservations(focusChanges, tokenChanges, now);
            if (planetClosed || shuttingDown) return false;
            ApplyPlanetPresentation(presentation);
            return true;
        }
        catch (Exception ex)
        {
            if (!planetClosed && !shuttingDown) Status("星球暂时无法更新，已有专注记录仍然保留：" + ex.Message);
            return false;
        }
        finally { planetOperation.Release(); }
    }

    private async Task ReadPlanetFocusAsync(DateTime today, bool fullRead)
    {
        var revision = Volatile.Read(ref focusDataRevision);
        if (!fullRead && revision == planetFocusRevision && today == planetFocusDate) return;
        var since = today < planetFocusDate ? today : planetFocusDate;
        var all = fullRead || planetFocusRevision < 0 || since == DateTime.MinValue;
        var result = await focusWork.RunAsync(() => (Revision: Volatile.Read(ref focusDataRevision),
            Values: all ? store.GetAllDailyTotals() : store.GetDailyTotalsSince(since)));
        if (all) planetFocusTotals = new(result.Values, StringComparer.Ordinal);
        else
        {
            var cutoff = since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var day in planetFocusTotals.Keys.Where(d => string.CompareOrdinal(d, cutoff) >= 0).ToArray()) planetFocusTotals.Remove(day);
            foreach (var row in result.Values) planetFocusTotals[row.Key] = row.Value;
        }
        planetFocusRevision = result.Revision; planetFocusDate = today;
    }

    private void RememberGrowthObservations(IReadOnlyDictionary<string, int> focus,
        IReadOnlyDictionary<string, long> tokens, DateTimeOffset now)
    {
        var today = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var row in focus) if (string.CompareOrdinal(row.Key, today) <= 0) appliedPlanetFocus[row.Key] = row.Value;
        foreach (var row in tokens) if (string.CompareOrdinal(row.Key, today) <= 0) appliedPlanetTokens[row.Key] = row.Value;
    }

    private async Task ReloadPlanetTokensAsync()
    {
        await planetOperation.WaitAsync();
        try
        {
            if (planetClosed || shuttingDown || planetStore == null || !dashboardReady || dashboardClosed) return;
            var source = cachedTokenSource;
            // A changed source begins a fresh period; never reuse the previous source after a read failure.
            eligiblePlanetTokens = [];
            if (!string.Equals(source, appliedPlanetTokenSource, StringComparison.OrdinalIgnoreCase))
            {
                appliedPlanetTokens.Clear(); appliedPlanetTokenSource = source;
            }
            if (source == null) return;
            var now = DateTimeOffset.Now;
            var cutoff = await planetWork.RunAsync(() => planetStore.GetTokenPeriodStart(source, now));
            var totals = await GetPlanetTokenTotalsAsync(cutoff, now);
            if (planetClosed || shuttingDown || dashboardClosed || !string.Equals(source, cachedTokenSource, StringComparison.OrdinalIgnoreCase)) return;
            eligiblePlanetTokens = totals;
        }
        finally { planetOperation.Release(); }
    }

    private async Task RunPlanetMutationAsync(Action mutation, Action? afterSaved = null)
    {
        if (planetClosed || shuttingDown || planetMutationPending || travellingPlanet || planetSnapshot == null) return;
        var expectedPlanet = planetSnapshot.PlanetId;
        bool mutationSaved = false;
        planetMutationPending = true; UpdatePlanetLayoutButtons();
        await planetOperation.WaitAsync();
        try
        {
            // This action was accepted before shutdown. Preserve it even if closing began while it waited.
            if (planetSnapshot?.PlanetId != expectedPlanet) return;
            var presentation = await planetWork.RunAsync(() =>
            {
                mutation();
                // A later presentation read can fail. Never replay an already committed rotation or placement.
                mutationSaved = true;
                return ReadPlanetPresentation(planetStore!.GetSnapshot(DateTimeOffset.Now));
            });
            if (planetClosed || shuttingDown) return;
            ApplyPlanetPresentation(presentation);
            afterSaved?.Invoke();
        }
        catch (Exception ex)
        {
            if (!mutationSaved && (planetClosed || shuttingDown))
            {
                // Closing freezes further UI actions. Keep precisely this failed action for an explicit close retry.
                pendingClosePlanetMutation = mutation; pendingClosePlanetError = ex;
                planetCloseFailureReported = false;
            }
            if (!planetClosed && !shuttingDown)
            {
                planetView?.SelectPlot(selectedPlanetPlot);
                PlanetLayoutHint.Text = ex.Message; Status(ex.Message);
            }
        }
        finally
        {
            planetOperation.Release(); planetMutationPending = false;
            if (!planetClosed && !shuttingDown) UpdatePlanetLayoutButtons();
        }
    }

    private async Task<bool> TravelPlanetAsync(Action travel)
    {
        if (travellingPlanet || planetMutationPending || planetClosed || shuttingDown || startingFocus || savingSettings || endingFocus) return false;
        travellingPlanet = true; UpdatePlanetLayoutButtons();
        try
        {
            if (cachedTokenSource != null || Directory.Exists(dashboardPreferences.ResolveCodexHome()))
            {
                Status("启程前正在结算当前星球的专注和 Token 助力…");
                if (!await ImportTokensAsync())
                {
                    if (!dashboardClosed) Status("用量未完整更新，本次仍留在原星球。请完成刷新后再次启程。");
                    return false;
                }
            }
            if (planetClosed || shuttingDown || dashboardClosed) return false;
            if (clock != null || startingFocus || savingSettings || endingFocus) { Status("专注正在开始或保存，本次仍留在原星球。请在结束后启程。"); return false; }
            if (!await RefreshPlanetAsync()) { Status("星球助力未能保存，本次仍留在原星球。请检查数据目录后再试。"); return false; }
            await planetOperation.WaitAsync();
            try
            {
                if (planetClosed || shuttingDown || dashboardClosed || clock != null || startingFocus || savingSettings || endingFocus) return false;
                var presentation = await planetWork.RunAsync(() =>
                {
                    travel();
                    return ReadPlanetPresentation(planetStore!.GetSnapshot(DateTimeOffset.Now));
                });
                if (planetClosed || shuttingDown) return false;
                ApplyPlanetPresentation(presentation);
                PlanetSelector.SelectedValue = presentation.Snapshot.PlanetId;
                Status("已前往 " + presentation.Snapshot.Name + "。后续成长将帮助当前星球。");
                return true;
            }
            finally { planetOperation.Release(); }
        }
        catch (Exception ex)
        {
            if (!planetClosed && !shuttingDown) { PlanetLayoutHint.Text = ex.Message; Status(ex.Message); }
            return false;
        }
        finally { travellingPlanet = false; if (!planetClosed && !shuttingDown) UpdatePlanetLayoutButtons(); }
    }

    internal async Task DrainPlanetAsync()
    {
        await Task.WhenAll(planetRefreshTask ?? Task.CompletedTask, planetActionTask);
        await planetWork.DrainAsync();
    }

    private Task ClosePlanetAsync()
    {
        if (planetDisposed) return Task.CompletedTask;
        if (planetCloseTask is null || planetCloseTask.IsFaulted || planetCloseTask.IsCanceled)
            planetCloseTask = ClosePlanetCoreAsync();
        return planetCloseTask;
    }

    private async Task ClosePlanetCoreAsync()
    {
        planetClosed = true; observationWindow?.Close();
        await Task.WhenAll(planetRefreshTask ?? Task.CompletedTask, planetActionTask);
        await planetOperation.WaitAsync();
        try
        {
            if (pendingClosePlanetMutation is { } retry)
            {
                if (!planetCloseFailureReported)
                {
                    planetCloseFailureReported = true;
                    throw new IOException("家园布局尚未保存，窗口已保留。请检查磁盘后再次关闭，以重试这次保存。", pendingClosePlanetError);
                }
                try
                {
                    await planetWork.RunAsync(retry);
                    pendingClosePlanetMutation = null; pendingClosePlanetError = null;
                }
                catch (Exception ex)
                {
                    pendingClosePlanetError = ex;
                    throw new IOException("家园布局仍未保存，窗口已保留。请检查磁盘后再次关闭。", ex);
                }
            }
            await planetWork.RunAsync(() => { planetLayout?.Dispose(); planetStore?.Dispose(); });
            await planetWork.DisposeAsync();
            planetDisposed = true;
        }
        finally { planetOperation.Release(); }
    }
}
