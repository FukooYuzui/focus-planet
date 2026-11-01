namespace FocusGarden.Core;

public sealed record GoalPart(string Id, string Title, bool Completed = false);
public sealed record LongTermGoal(string Id, string Title, string Mode, GoalPart[] Parts, decimal Percent = 0, bool Archived = false)
{
    public decimal Progress => Mode == "parts" ? (Parts.Length == 0 ? 0 : decimal.Round(100m * Parts.Count(x => x.Completed) / Parts.Length, 2)) : Percent;
    public string ProgressLabel => Mode == "parts" ? $"{Parts.Count(x => x.Completed)}/{Parts.Length} 部分 · {Progress:0.##}%" : $"{Percent:0.##}%";
    public override string ToString() => Title + " · " + ProgressLabel + (Archived ? "（已归档）" : "");
}
public sealed record GoalDraft(string? Id, string Title, string Mode, string[] PartTitles);
public sealed record GoalContribution(string GoalId, string[] PartIds, decimal Percent = 0);
