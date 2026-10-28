using FocusGarden.Core;
using System.Windows;

namespace FocusGarden.App;

public partial class MainWindow
{
    private bool StartWasInterrupted(long generation)
    {
        if (generation == Volatile.Read(ref sessionEventGeneration) && !desktopLocked && !powerSuspended && !shuttingDown) return false;
        Status("开始期间系统锁定或休眠，本次开始已取消；请返回后重新开始。");
        return true;
    }
    private sealed class SessionWriter(long id)
    {
        public long Id { get; } = id;
        public volatile bool Failed;
    }

    private async Task ObserveProgressAsync(Task write, SessionWriter writer)
    {
        try { await write; }
        catch (Exception error)
        {
            writer.Failed = true;
            if (ReferenceEquals(sessionWriter, writer))
            {
                ReleaseForError();
                Status("记录写入失败，已解除限制。已有时长保留，未保存部分不会补算：" + error.Message);
            }
        }
    }

    private async Task SaveSessionEndAsync(long id, SessionWriter? writer, string requestedStatus, string message, DateTimeOffset ended)
    {
        try
        {
            var actualStatus = await focusWork.RunAsync(() =>
            {
                var result = writer?.Failed == true ? "interrupted" : requestedStatus;
                store.EndSession(id, result, ended);
                Interlocked.Increment(ref focusDataRevision);
                return result;
            });
            if (actualStatus != requestedStatus)
            {
                message = "记录写入曾失败，本轮按中断保存；已保存的专注时长保留。";
                SessionLabel.Text = "下一次，继续从这里开始。";
            }
        }
        catch (Exception error) { message += " 结束状态保存失败，下次启动会保留最后保存的时长并恢复为中断：" + error.Message; }
        finally
        {
            endingFocus = false;
            StartButton.IsEnabled = !shuttingDown; BreakButton.IsEnabled = !shuttingDown; TestButton.IsEnabled = !shuttingDown;
            if (!shuttingDown) { Status(message); RefreshStatistics(); }
        }
    }

    internal async Task DrainFocusAsync()
    {
        await focusWork.DrainAsync();
        await progressTask;
        await finishTask;
    }

    // Also used by isolated UI tests; it waits for actual background work rather than substituting synchronous behavior.
    internal async Task WaitForBackgroundAsync()
    {
        await DrainFocusAsync();
        await statisticsTask;
        await DrainDashboardAsync();
        await DrainPlanetAsync();
        await focusWork.DrainAsync();
    }

    internal async Task PrepareCloseAsync()
    {
        if (allowClose) return;
        if (clock != null || startingFocus || savingSettings) throw new InvalidOperationException("请先结束当前专注或等待开始操作完成。");
        shuttingDown = true; timer.Stop();
        ReleaseWorkspace();
        StartButton.IsEnabled = false; BreakButton.IsEnabled = false; TestButton.IsEnabled = false;
        SetupPanel.IsEnabled = false;
        try
        {
            await DrainFocusAsync();
            await statisticsTask;
            await exportTask;
            await CloseDashboardAsync();
            await ClosePlanetAsync();
            await focusWork.RunAsync(store.Dispose);
            await focusWork.DisposeAsync();
            allowClose = true;
        }
        catch
        {
            // Keep unsaved data in memory. A later close request may retry; never silently abandon a batch.
            // Some components may already be closed. Remain paused and allow only another close attempt.
            Status("仍有记录未保存，程序已暂停。请检查磁盘后再次关闭。");
            throw;
        }
    }

    private async Task CloseFromRequestAsync()
    {
        try { await PrepareCloseAsync(); Close(); }
        catch (Exception error) { Status("仍有记录未保存，窗口已保留。请检查磁盘后再次关闭：" + error.Message); }
    }
}
