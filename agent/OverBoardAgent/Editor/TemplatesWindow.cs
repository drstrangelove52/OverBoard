using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OverBoardAgent.Core;

namespace OverBoardAgent.Editor;

/// <summary>Auswahl einer fertigen Seite. Jede Vorlage zeigt eine Vorschau; ein Klick auf «Seite hinzufügen» gibt sie zurück (bei Abbruch null).</summary>
public sealed class TemplatesWindow : Window
{
    public TemplatesWindow(string language, string theme)
    {
        Title = Strings.Get("tpl.title");
        Width = 760;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var list = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        list.Children.Add(new TextBlock { Text = Strings.Get("tpl.hint"), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });

        foreach (var tpl in PageTemplates.All)
        {
            var page = tpl.Build(language);
            var config = new ConfigModel { Language = language, Theme = theme, Pages = [page] };
            var preview = new Border
            {
                Width = 300, Height = 175, CornerRadius = new CornerRadius(8), ClipToBounds = true,
                Child = new Viewbox { Stretch = Stretch.Uniform, Child = DisplayRenderer.Build(config, new PreviewOptions { Page = 0, ShowGrid = false }) },
            };
            var name = language == "en" ? tpl.En : tpl.De;
            var desc = language == "en" ? tpl.DescEn : tpl.DescDe;
            var pick = new Button { Content = Strings.Get("tpl.add"), Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            var t = tpl;
            pick.Click += (_, _) => Close(t.Build(language));
            var text = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center, Spacing = 4,
                Children =
                {
                    new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, FontSize = 16 },
                    new TextBlock { Text = desc, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
                    pick,
                },
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 18 };
            Grid.SetColumn(text, 1);
            row.Children.Add(preview);
            row.Children.Add(text);
            list.Children.Add(new Border
            {
                Padding = new Thickness(14), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#30808080")), Background = new SolidColorBrush(Color.Parse("#10808080")), Child = row,
            });
        }

        var cancel = new Button { Content = Strings.Get("edit.cancel"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 0, 20, 16) };
        cancel.Click += (_, _) => Close(null);
        var root = new DockPanel();
        DockPanel.SetDock(cancel, Dock.Bottom);
        root.Children.Add(cancel);
        root.Children.Add(new ScrollViewer { Content = list });
        Content = root;
    }
}
