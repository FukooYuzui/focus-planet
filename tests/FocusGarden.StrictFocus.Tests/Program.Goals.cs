using System.Windows;
using System.Windows.Controls;
using FocusGarden.App;
using FocusGarden.Core;

internal static partial class Program
{
    private static LongTermGoal AddGoal(MainWindow window, string title, string mode, params string[] parts)
        => (LongTermGoal)Call(window,"SaveGoalAsync",new GoalDraft(null,title,mode,parts))!;
    private static void SelectGoal(GoalWorkPicker picker, string id)
        => picker.GoalChoice.SelectedItem = picker.GoalChoice.Items.OfType<LongTermGoal>().Single(x=>x.Id==id);
    private static void RunGoalCases()
    {
        Run("pomodoro captures its selected goal increment and credits it exactly once on completion",()=>
        {
            using var f=new Fixture(false);var goal=AddGoal(f.Window,"完成实验报告","percent");var other=AddGoal(f.Window,"其他目标","percent");
            var picker=Field<GoalWorkPicker>(f.Window,"normalGoalPicker");SelectGoal(picker,goal.Id);picker.PercentInput.Text="20.5";
            f.Start();Assert(!picker.IsEnabled);SelectGoal(picker,other.Id);picker.PercentInput.Text="90";
            bool refused=false;try { Call(f.Window,"SaveGoalAsync",new GoalDraft(goal.Id,"不能修改","percent",[])); } catch(InvalidOperationException) { refused=true; } Assert(refused);
            for(int i=0;i<15;i++)f.Tick(4);f.Drain();
            Equal(20.5m,Board(f.Window).Goals.Single(x=>x.Id==goal.Id).Percent);Equal(0m,Board(f.Window).Goals.Single(x=>x.Id==other.Id).Percent);
            Assert(Board(f.Window).Completed.Any(x=>x.Title.Contains("+20.5%")));f.AssertHistory();
        });
        Run("ordinary early exit saves focus seconds but never applies the chosen goal increment",()=>
        {
            using var f=new Fixture(false);var goal=AddGoal(f.Window,"阅读计划","percent");var picker=Field<GoalWorkPicker>(f.Window,"normalGoalPicker");SelectGoal(picker,goal.Id);picker.PercentInput.Text="30";
            f.Start();f.Tick(3);Call(f.Window,"Finish","emergency","stop");f.Drain();
            Equal(0m,Board(f.Window).Goals.Single().Percent);Equal(3,f.Current.ElapsedSeconds);Equal(1,Board(f.Window).Completed.Count);f.AssertHistory();
        });
        Run("daily named parts are credited at the deadline in either accounting mode while aborted next tasks are not",()=>
        {
            foreach(bool all in new[]{false,true})
            {
                using var d=new DailyFixture(all);var goal=AddGoal(d.F.Window,"读完教材","parts","第一章","第二章","第三章");d.Start();
                Equal<string?>(null,(string?)Call(d.F.Window,"StartDailySelectedTaskAsync","",1,new GoalContribution(goal.Id,[goal.Parts[0].Id,goal.Parts[1].Id])));
                for(int i=0;i<20;i++)d.Advance(3);Assert(d.Active);
                var done=Board(d.F.Window).Goals.Single();Equal(2,done.Parts.Count(x=>x.Completed));Assert(Board(d.F.Window).Completed.Any(x=>x.Title.Contains("第一章、第二章")));
                Call(d.F.Window,"StartDailySelectedTaskAsync","读第三章",1,new GoalContribution(goal.Id,[goal.Parts[2].Id]));SendHotkey(d.F.Window,9082);d.F.Drain();
                Equal(2,Board(d.F.Window).Goals.Single().Parts.Count(x=>x.Completed));d.F.AssertHistory();
            }
        });
        Run("invalid daily goal selection cannot create a session or release protection and natural end credits the final valid task",()=>
        {
            using var d=new DailyFixture();var goal=AddGoal(d.F.Window,"写作","percent");d.Now=new(2026,11,2,11,59,0,TimeSpan.FromHours(8));d.Start();
            Assert(Call(d.F.Window,"StartDailySelectedTaskAsync","本轮",1,new GoalContribution(goal.Id,[],101)) is string);Equal(1,d.F.Store.GetSessions().Count);Assert(d.Active);
            Equal<string?>(null,(string?)Call(d.F.Window,"StartDailySelectedTaskAsync","本轮",1,new GoalContribution(goal.Id,[],25)));
            for(int i=0;i<20;i++)d.Advance(3);Assert(!d.Active);Equal(25m,Board(d.F.Window).Goals.Single().Percent);
            d.Advance(1);Equal(25m,new FocusBoardStore(d.F.DirectoryPath,[]).Snapshot().Goals.Single().Percent);d.F.AssertHistory();
        });
        Run("goal editor and selection controls render named parts and cumulative completion on wide and portrait focus surfaces",()=>
        {
            using var d=new DailyFixture();var goal=AddGoal(d.F.Window,"读完量子力学教材","parts","第一章：基本概念","第二章：波函数","第三章：算符与测量","第四章：角动量");
            var percentage = AddGoal(d.F.Window,"完成研究报告","percent");
            var editor = Field<LongGoalEditor>(d.F.Window,"goalEditor"); var list = Field<ListBox>(editor,"goals");
            list.SelectedItem = list.Items.OfType<LongTermGoal>().Single(x => x.Id == goal.Id);
            Assert(Field<StackPanel>(editor,"partsPanel").Visibility == Visibility.Visible);
            Call(d.F.Window,"Page","board");Render(d.F.Window,"goals-editor.png",1200,1100);
            var ordinary=Field<GoalWorkPicker>(d.F.Window,"normalGoalPicker");SelectGoal(ordinary,goal.Id);
            Call(d.F.Window,"Page","focus");Render(d.F.Window,"goals-pomodoro-picker.png",1200,1100);
            d.Start();var backdrop=Field<IReadOnlyList<WorkspaceBackdropWindow>>(d.F.Window,"workspaceBackdrops").Single();
            var panel=Field<DailyTaskPanel>(backdrop,"dailyTaskPanel");SelectGoal(panel.GoalPicker,goal.Id);
            foreach(var check in Field<List<CheckBox>>(panel.GoalPicker,"checks").Take(2))check.IsChecked=true;
            var captured=panel.GoalPicker.Capture()!;Equal(2,captured.PartIds.Length);
            Render(backdrop,"goals-daily-picker.png",1500,1000);Render(backdrop,"goals-daily-portrait.png",720,1280);
            Call(d.F.Window,"StartDailySelectedTaskAsync","阅读教材",1,captured);for(int i=0;i<20;i++)d.Advance(3);
            Render(backdrop,"goals-completed.png",1500,1000);
            SelectGoal(panel.GoalPicker,percentage.Id); panel.GoalPicker.PercentInput.Text="20"; Render(backdrop,"goals-percentage.png",1500,1000); d.F.AssertHistory();
        });
    }
}
