using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.Styling;
using FluentAvalonia.UI.Controls;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Ryubing.Canary.Local.Tests.Typography;

[TestFixture]
[NonParallelizable]
public class SettingsNavigationViewTests
{
    [AvaloniaTest]
    public void EmptyUnattachedNavigationCanBeInspectedWithoutAWorkaround()
    {
        FANavigationView view = new();
        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(view);
        Assert.That(peer, Is.Not.Null);
        Assert.That(peer.GetChildren() ?? Array.Empty<AutomationPeer>(), Is.Empty);
    }

    [AvaloniaTest]
    public void FluentAvalonia3RealizesBothNavigationLayoutsAndTheirCurrentPage()
    {
        // Exercise the pinned framework itself. Do not import the old ItemsRepeater AX workaround
        // or assert that this new library reproduces an old-library exception.
        foreach (FANavigationViewPaneDisplayMode mode in new[] { FANavigationViewPaneDisplayMode.Left, FANavigationViewPaneDisplayMode.Top })
        {
            FluentAvaloniaTheme theme = new() { PreferSystemTheme = false, PreferUserAccentColor = false };
            Application.Current!.Styles.Add(theme);
            FANavigationViewItem first = new() { Content = "界面", Tag = "UiPage" };
            FANavigationViewItem second = new() { Content = "音频 SDL3", Tag = "AudioPage" };
            TextBlock content = new() { Text = "设置（Switch 123）" };
            FANavigationView view = new()
            {
                IsSettingsVisible = false,
                PaneDisplayMode = mode,
                IsPaneOpen = true,
                Content = content,
            };
            view.MenuItems.Add(first);
            view.MenuItems.Add(second);
            view.SelectedItem = first;
            Window window = new() { Width = 1000, Height = 700, Content = view };
            try
            {
                window.Show();
                view.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Assert.That(view.GetVisualDescendants().OfType<FANavigationViewItem>(), Does.Contain(first).And.Contain(second));
                Assert.That(view.GetVisualDescendants(), Does.Contain(content));
                Assert.DoesNotThrow(() => ControlAutomationPeer.CreatePeerForElement(view).GetChildren());
                TextBlock replacement = new() { Text = "音频 SDL3" };
                view.SelectedItem = second;
                view.Content = replacement;
                view.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Assert.That(view.SelectedItem, Is.SameAs(second));
                Assert.That(view.GetVisualDescendants(), Does.Contain(replacement).And.Not.Contain(content));
                Assert.DoesNotThrow(() => ControlAutomationPeer.CreatePeerForElement(view).GetChildren());
            }
            finally
            {
                window.Close();
                Application.Current.Styles.Remove(theme);
            }
        }
    }

    [Test]
    public void ProductionSettingsUsesCanarysFANavigationViewAndOriginalPageSet()
    {
        string source = Environment.GetEnvironmentVariable("RYUBING_CANARY_LOCAL_SOURCE_ROOT");
        Assert.That(source, Is.Not.Null.And.Not.Empty);
        Assert.That(Path.IsPathFullyQualified(source), Is.True);
        XDocument document = XDocument.Load(Path.Combine(source, "src/Ryujinx/UI/Windows/SettingsWindow.axaml"));
        XElement navigation = document.Descendants().Single(element => (string)element.Attribute("Name") == "NavPanel");
        Assert.That(navigation.Name.LocalName, Is.EqualTo(nameof(FANavigationView)));
        Assert.That(navigation.Descendants().Count(element => element.Name.LocalName == nameof(FANavigationViewItem)), Is.EqualTo(11));
    }
}
