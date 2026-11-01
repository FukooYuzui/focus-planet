using System.Windows;
using System.Windows.Controls;
using FocusGarden.App;
using FocusGarden.Core;

internal static partial class Program
{
    private static FocusBoardSnapshot Board(MainWindow window) => Field<FocusBoardSnapshot>(window,"boardSnapshot");
    private static void RunBoardCases()
    {
        Run("board debounce cannot lose the last edit when closing immediately", () =>
        {
            using var f = new Fixture(false);
            Field<TextBox>(f.Window,"boardPlansInput").Text = "离开前刚写下的计划";
            Call(f.Window,"PrepareCloseAsync");
            Equal("离开前刚写下的计划",new FocusBoardStore(f.DirectoryPath,[]).Snapshot().Plans);
            f.Window.Close();
        });
        Run("board save failure keeps the draft retryable and prevents entering focus before it is saved", () =>
        {
            using var f = new Fixture(false); var input=Field<TextBox>(f.Window,"boardPlansInput"); input.Text="不能丢失的新计划";
            using(var held=new System.IO.FileStream(System.IO.Path.Combine(f.DirectoryPath,"focus-board.json"),System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.Read))
            {
                Call(f.Window,"Begin",false); Assert(Field<object?>(f.Window,"clock")==null); Assert(!input.IsReadOnly);
                Assert(Field<bool>(f.Window,"boardPlansDirty")); Equal("不能丢失的新计划",input.Text);
            }
            f.Start(); Equal("不能丢失的新计划",new FocusBoardStore(f.DirectoryPath,[]).Snapshot().Plans); f.AssertHistory();
        });
        Run("board editing is outside focus and saves before start while corner surfaces stay read only", () =>
        {
            using var f=new Fixture(false); var input=Field<TextBox>(f.Window,"boardPlansInput"); Assert(!input.IsReadOnly);
            input.Text="读完量子力学\n完成研究报告\n坚持运动"; f.Start(); Assert(input.IsReadOnly);
            Equal(input.Text,new FocusBoardStore(f.DirectoryPath,[]).Snapshot().Plans);
            Call(f.Window,"Finish","emergency","stop"); f.Drain(); Assert(!input.IsReadOnly); f.AssertHistory();
        });
        Run("completed pomodoro appears once but emergency sessions and breaks never enter the board", () =>
        {
            using var f=new Fixture(false); f.Start(); for(int i=0;i<15;i++) f.Tick(4);
            Equal(1,Board(f.Window).Completed.Count(x=>x.Title=="strict test"));
            Call(f.Window,"StartBreak",f.Window,new RoutedEventArgs()); Call(f.Window,"Finish","completed","break"); f.Drain();
            Equal(2,Board(f.Window).Completed.Count); f.AssertHistory();
            var saved=new FocusBoardStore(f.DirectoryPath,[]).Snapshot(); Equal(2,saved.Completed.Count);
        });
        Run("daily task completion updates the board without ending protection in either accounting mode", () =>
        {
            foreach(var all in new[]{false,true})
            {
                using var d=new DailyFixture(all); d.Start(); d.StartTask("完成文献笔记");
                for(int i=0;i<20;i++) d.Advance(3);
                d.Advance(1); Assert(d.Active); Equal(1,Board(d.F.Window).Completed.Count(x=>x.Title=="完成文献笔记"));
                d.StartTask("未完成的任务"); SendHotkey(d.F.Window,9082); d.F.Drain();
                Assert(!Board(d.F.Window).Completed.Any(x=>x.Title=="未完成的任务")); d.F.AssertHistory();
            }
        });
        Run("daily deadline after a timing gap records the finished task without inventing focus seconds", () =>
        {
            using var d=new DailyFixture(); d.Start(); d.StartTask("延迟回调任务"); d.Advance(10);
            for(int i=0;i<20;i++) d.Advance(2.5);
            Assert(d.Active); Equal(50,d.TaskRow("延迟回调任务").ElapsedSeconds);
            Equal("interrupted",d.TaskRow("延迟回调任务").Status);
            Equal(1,Board(d.F.Window).Completed.Count(x=>x.Title=="延迟回调任务")); d.F.AssertHistory();
        });
        Run("focus corner cards render normal daily and portrait layouts without editable controls or overlap", () =>
        {
            using var d=new DailyFixture(); var input=Field<TextBox>(d.F.Window,"boardPlansInput"); input.Text="六个月内完成课题\n读完三本专业书\n每周坚持运动";
            Call(d.F.Window,"SaveBoardPlansAsync"); d.Start(); d.StartTask("阅读第一篇文献"); for(int i=0;i<20;i++)d.Advance(3);
            var backdrop=Field<IReadOnlyList<WorkspaceBackdropWindow>>(d.F.Window,"workspaceBackdrops").Single();
            Render(backdrop,"focus-board-daily-wide.png",1440,900); Render(backdrop,"focus-board-daily-portrait.png",720,1280);
            var layout=Field<FocusSurfaceLayout>(backdrop,"boardLayout");
            Assert(Field<TextBlock>(layout,"plans").Text.Contains("六个月"));
            var normal=new ImmersionWindow("准备下一项任务",true,null,()=>{});
            try { normal.UpdateTime("24:37"); normal.UpdateBoard(Board(d.F.Window)); Render(normal,"focus-board-normal.png",1440,900); }
            finally { normal.Release(); }
            Call(d.F.Window,"Page","board"); Render(d.F.Window,"focus-board-editor-locked.png",1200,1000);
            d.F.AssertHistory();
        });
    }
}
