using FocusGarden.Core;
using System.Text.Json;

internal static class GoalProgressCases
{
    internal static void Run(string root, Action<string, Action> run)
    {
        string Folder() { var p=Path.Combine(root,"goals-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
        void Check(bool value) { if(!value)throw new Exception("Goal assertion failed"); }
        void Reject(Action action) { bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}Check(rejected); }
        var now=DateTimeOffset.Parse("2026-11-01T10:00:00+08:00");
        run("goal migration backs up exact v1 board bytes and retains completed entries and stable ids",()=>
        {
            var folder=Folder(); var file=Path.Combine(folder,"focus-board.json");
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new { Version=1,Plans="读完教材\n完成论文",Completed=new[]{new CompletedFocusTask("old","已完成的阅读",now)} });
            File.WriteAllBytes(file,bytes); var board=new FocusBoardStore(folder,[]);
            Check(board.Snapshot().Goals.Count==2 && board.Snapshot().Goals.All(g=>g.Progress==0));
            board.Flush(); Check(File.ReadAllBytes(Directory.GetFiles(folder,"focus-board.v1-*.json").Single()).SequenceEqual(bytes));
            var reopened=new FocusBoardStore(folder,[]).Snapshot(); Check(reopened.Goals.Select(g=>g.Id).SequenceEqual(board.Snapshot().Goals.Select(g=>g.Id)));
            Check(reopened.Completed.Single().Title=="已完成的阅读" && !new FocusBoardStore(folder,[]).Dirty);
        });
        run("long legacy plan lines remain verbatim notes instead of being truncated during migration",()=>
        {
            var folder=Folder(); var longLine=new string('读',150);
            File.WriteAllText(Path.Combine(folder,"focus-board.json"),JsonSerializer.Serialize(new{Version=1,Plans=longLine+"\n正常任务",Completed=Array.Empty<CompletedFocusTask>()}));
            var board=new FocusBoardStore(folder,[]); board.Flush(); Check(board.Snapshot().Plans==longLine && board.Snapshot().Goals.Single().Title=="正常任务");
        });
        run("named parts complete together and retained dedup prevents replay after list eviction and reopen",()=>
        {
            var folder=Folder(); var board=new FocusBoardStore(folder,[]); var goal=board.SaveGoal(new(null,"教材","parts",["力学","热学","光学"]));
            var selection=new GoalContribution(goal.Id,[goal.Parts[0].Id,goal.Parts[1].Id]);
            board.AddCompleted(new("event","阅读",now),selection); Check(board.Snapshot().Goals.Single().Parts.Count(p=>p.Completed)==2);
            Check(board.Snapshot().Completed.Single().Title.Contains("力学、热学"));
            for(int i=0;i<110;i++)board.AddCompleted(new("other"+i,"其他",now.AddMinutes(i+1)));
            board.Flush(); board=new FocusBoardStore(folder,[]); board.AddCompleted(new("event","重试",now),selection);
            Check(board.Snapshot().Goals.Single().Parts.Count(p=>p.Completed)==2 && board.Snapshot().Completed.Count==100);
        });
        run("percentage increments are exact and reject overflow zero excessive precision and wrong mode",()=>
        {
            var board=new FocusBoardStore(Folder(),[]); var goal=board.SaveGoal(new(null,"论文","percent",[]));
            board.AddCompleted(new("a","本轮",now),new(goal.Id,[],20.25m)); board.AddCompleted(new("b","本轮",now),new(goal.Id,[],29.75m));
            Check(board.Snapshot().Goals.Single().Percent==50m); Reject(()=>board.ValidateContribution(new(goal.Id,[],50.01m)));
            Reject(()=>board.ValidateContribution(new(goal.Id,[],0))); Reject(()=>board.ValidateContribution(new(goal.Id,[],.001m)));
            Reject(()=>board.ValidateContribution(new(goal.Id,["bad"],10))); board.AddCompleted(new("c","收尾",now),new(goal.Id,[],50));
            Check(board.Snapshot().Goals.Single().Progress==100); Reject(()=>board.ValidateContribution(new(goal.Id,[],1)));
        });
        run("editing and archiving goals preserve earned progress and completed part identities",()=>
        {
            var board=new FocusBoardStore(Folder(),[]);var goal=board.SaveGoal(new(null,"旧名称","parts",["第一章","第二章"]));
            board.AddCompleted(new("a","学习",now),new(goal.Id,[goal.Parts[0].Id]));
            var revised=board.SaveGoal(new(goal.Id,"新名称","parts",["第一章","第二章","第三章"]));
            Check(revised.Id==goal.Id && revised.Parts[0].Id==goal.Parts[0].Id && revised.Parts[0].Completed);
            Reject(()=>board.SaveGoal(new(goal.Id,"名称","parts",["第二章"]))); Reject(()=>board.SaveGoal(new(goal.Id,"名称","percent",[])));
            board.ArchiveGoal(goal.Id); Reject(()=>board.ValidateContribution(new(goal.Id,[goal.Parts[1].Id])));
            Check(board.Snapshot().Goals.Single().Parts[0].Completed && board.Snapshot().Completed.Count==1);
        });
        run("failed task definition save rolls back and retry never creates a duplicate task",()=>
        {
            var folder=Folder();var board=new FocusBoardStore(folder,[]);board.Flush();
            using(var held=new FileStream(Path.Combine(folder,"focus-board.json"),FileMode.Open,FileAccess.Read,FileShare.Read))
            { bool failed=false;try{board.SaveGoalPersisted(new(null,"任务","percent",[]));}catch(IOException){failed=true;}Check(failed && board.Snapshot().Goals.Count==0); }
            board.SaveGoalPersisted(new(null,"任务","percent",[]));Check(new FocusBoardStore(folder,[]).Snapshot().Goals.Count==1);
        });
        run("failed progress flush keeps completion and progress together and a retry credits once",()=>
        {
            var folder=Folder();var board=new FocusBoardStore(folder,[]);var goal=board.SaveGoalPersisted(new(null,"任务","percent",[]));
            using(var held=new FileStream(Path.Combine(folder,"focus-board.json"),FileMode.Open,FileAccess.Read,FileShare.Read))
            { board.AddCompleted(new("a","本次",now),new(goal.Id,[],25));bool failed=false;try{board.Flush();}catch(IOException){failed=true;}Check(failed && board.Dirty); }
            Check(new FocusBoardStore(folder,[]).Snapshot().Goals.Single().Percent==0);
            board.AddCompleted(new("a","重试",now),new(goal.Id,[],25));board.Flush();var loaded=new FocusBoardStore(folder,[]).Snapshot();
            Check(loaded.Goals.Single().Percent==25 && loaded.Completed.Count==1);
        });
    }
}
