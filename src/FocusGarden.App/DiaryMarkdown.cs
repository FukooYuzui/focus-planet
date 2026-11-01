using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace FocusGarden.App;

/// <summary>Offline preview of common Markdown. HTML and remote images remain inert text.</summary>
internal static class DiaryMarkdown
{
    public static FlowDocument Render(string markdown)
    {
        var document = new FlowDocument { FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 14, Foreground = Brushes.WhiteSmoke, Background = new SolidColorBrush(Color.FromRgb(16, 26, 31)), PagePadding = new Thickness(16) };
        bool code = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n').Take(4000))
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal)) { code = !code; continue; }
            var line = raw; var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            if (code) { p.FontFamily = new FontFamily("Consolas"); p.Background = new SolidColorBrush(Color.FromRgb(16, 26, 31)); p.Inlines.Add(new Run(line)); }
            else
            {
                var heading = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
                if (heading.Success) { p.FontSize = 27 - heading.Groups[1].Length * 2; p.FontWeight = FontWeights.SemiBold; line = heading.Groups[2].Value; }
                else if (line.StartsWith("> ")) { line = line[2..]; p.BorderThickness = new Thickness(3, 0, 0, 0); p.BorderBrush = Brushes.LightSlateGray; p.Padding = new Thickness(10, 0, 0, 0); }
                else if (line.StartsWith("- ") || line.StartsWith("* ")) line = "• " + line[2..];
                foreach (var part in Regex.Split(line, @"(\*\*[^*]+\*\*|`[^`]+`)", RegexOptions.None, TimeSpan.FromSeconds(1)))
                {
                    if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4) p.Inlines.Add(new Bold(new Run(part[2..^2])));
                    else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2) p.Inlines.Add(new Run(part[1..^1]) { FontFamily = new FontFamily("Consolas"), Foreground = Brushes.LightGreen });
                    else p.Inlines.Add(new Run(part));
                }
            }
            document.Blocks.Add(p);
        }
        return document;
    }
}
