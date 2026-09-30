using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using CodexBackupManager.App.Tests.TestSupport;
using CodexBackupManager.App.ViewModels.Import;
using Xunit;

namespace CodexBackupManager.App.Tests.Views;

/// <summary>
/// Phase 9_2-2 — 실제 <c>ImportWorkspaceView.xaml</c>을 실제 <c>App.xaml</c> 리소스와 함께 그려서 XAML 파싱 오류와
/// 바인딩 오류(<c>System.Windows.Data Error</c>)가 없는지 확인한다. 상태마다(편집·결과·Codex 대기·실패) 한 번씩 그린다.
/// </summary>
/// <remarks>
/// <see cref="Application"/>을 만들지 않는다(같은 프로세스에서 하나만 만들 수 있다는 기존 제약,
/// <see cref="DarkScrollBarOrientationTests"/> 참고). 대신 App.xaml의 리소스 전체를 <see cref="Window.Resources"/>에 넣은
/// 창 XAML 안에 뷰 마크업을 그대로 넣어 파싱한다. 코드 비하인드 이벤트 연결(<c>SelectedItemChanged</c>)만 뺀다.
/// </remarks>
public sealed class ImportWorkspaceViewRenderTests : IDisposable
{
    private readonly ImportWorkspaceHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task 편집_화면을_대화와_프로젝트_상세까지_바인딩_오류_없이_그린다()
    {
        _h.RemoveFromTarget(ImportWorkspaceHarness.Thread2, ImportWorkspaceHarness.Thread3);
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);
        ws.SelectNode(ws.Projects.SelectMany(p => p.Conversations).First(c => c.ThreadId == ImportWorkspaceHarness.Thread2));
        await ImportWorkspaceHarness.ContentLoadedAsync(ws);
        Assert.NotEmpty(ws.ContentMessages); // 대화 내용 미리보기도 함께 그린다
        _h.CodexRunning = true;
        _h.PollCallback!(); // 배너도 함께 그린다

        AssertRendersWithoutBindingErrors(ws);

        // 한 번 그린 창의 CollectionView는 그 STA 스레드에 묶여 있으므로 프로젝트 상세는 새 화면으로 그린다.
        _h.CodexRunning = false;
        ImportWorkspaceViewModel projectView = await _h.OpenEditingAsync(backup);
        projectView.SelectNode(projectView.Projects[0]);
        AssertRendersWithoutBindingErrors(projectView);
    }

    [Fact]
    public async Task 결과_화면을_바인딩_오류_없이_그린다()
    {
        _h.RemoveFromTarget(ImportWorkspaceHarness.Thread2);
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);
        await ws.ImportAsync();
        Assert.Equal(ImportWorkspaceState.Result, ws.State);

        AssertRendersWithoutBindingErrors(ws);
    }

    [Fact]
    public async Task 새_프로젝트_만들기_상태를_이름_칸과_함께_바인딩_오류_없이_그린다()
    {
        // Phase 9_5-05 — CreateNew 문구, 이름 칸(기본값 폴더 이름), "만들지 않기" 선택. 이름 칸 입력은 VM 결정으로 간다.
        _h.RemoveFromTarget(ImportWorkspaceHarness.Thread1);
        string folder = _h.NewFolder("render-create");
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup, folderPicker: () => folder);
        ImportProjectNodeViewModel alpha = ws.Projects.First(p => p.Conversations.Any(c => c.ThreadId == ImportWorkspaceHarness.Thread1));
        alpha.ChooseFolderCommand.Execute(null);
        ws.SelectNode(alpha);
        Assert.True(alpha.IsCreatingProject);

        AssertRendersWithoutBindingErrors(ws, window =>
        {
            System.Windows.Controls.TextBox name = Descendants<System.Windows.Controls.TextBox>(window)
                .Single(t => AutomationNameOf(t) == "새 프로젝트 이름" && ReferenceEquals(t.DataContext, alpha));
            Assert.Equal(System.IO.Path.GetFileName(folder), name.Text);
            System.Windows.Controls.CheckBox decline = Descendants<System.Windows.Controls.CheckBox>(window)
                .Single(c => Equals(c.Content, "새 프로젝트를 만들지 않고 기타 대화로 가져오기") && ReferenceEquals(c.DataContext, alpha));
            Assert.Equal(Visibility.Visible, decline.Visibility);

            name.Text = "화면에서 바꾼 이름";
            Pump();
            Assert.Equal("화면에서 바꾼 이름", alpha.Target!.NewProjectName);
        });
    }

    [Fact]
    public async Task Codex_대기와_실패_화면을_바인딩_오류_없이_그린다()
    {
        string backup = await _h.ExportAsync();
        _h.CodexRunning = true;
        ImportWorkspaceViewModel waiting = _h.CreateWorkspace(() => backup);
        await waiting.OpenAsync();
        Assert.Equal(ImportWorkspaceState.WaitingForCodexExit, waiting.State);
        AssertRendersWithoutBindingErrors(waiting);

        string broken = Path.Combine(_h.TestDir, "broken.codexbackup");
        File.WriteAllText(broken, "x");
        _h.CodexRunning = false;
        ImportWorkspaceViewModel failed = _h.CreateWorkspace(() => broken);
        await failed.OpenAsync();
        Assert.Equal(ImportWorkspaceState.Failed, failed.State);
        AssertRendersWithoutBindingErrors(failed);
    }

    [Fact]
    public async Task 메인_창을_연결된_상태에서_바인딩_오류_없이_그린다()
    {
        // MainWindow.xaml에서 컴파일된 ImportWorkspaceView 요소와 코드 비하인드 이벤트만 빼고 그대로 그린다.
        CodexBackupManager.App.ViewModels.MainViewModel main = _h.CreateMainViewModel(_h.TargetHome);
        await ImportWorkspaceHarness.ConnectAsync(main);
        main.HighlightConversations([ImportWorkspaceHarness.Thread2]);

        AssertRendersWithoutBindingErrors(main, BuildMainWindowXaml());
    }

    private static string BuildMainWindowXaml()
    {
        string appDir = Path.Combine(RepositoryFixtures.RepositoryRoot, "src", "CodexBackupManager.App");
        string text = File.ReadAllText(Path.Combine(appDir, "Views", "MainWindow.xaml"));
        text = Regex.Replace(text, "\\s+x:Class=\"[^\"]*\"", string.Empty);
        text = Regex.Replace(text, "\\s+(Activated|SelectedItemChanged)=\"[^\"]*\"", string.Empty);
        text = Regex.Replace(text, "<views:ImportWorkspaceView[^>]*/>", string.Empty, RegexOptions.Singleline);
        text = text.Replace("clr-namespace:CodexBackupManager.App.Rendering\"", "clr-namespace:CodexBackupManager.App.Rendering;assembly=CodexBackupManager\"");
        text = text.Replace("clr-namespace:CodexBackupManager.App.Views\"", "clr-namespace:CodexBackupManager.App.Views;assembly=CodexBackupManager\"");

        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement window = XElement.Parse(text);
        window.SetAttributeValue(XNamespace.Xmlns + "converters", "clr-namespace:CodexBackupManager.App.Converters;assembly=CodexBackupManager");
        window.SetAttributeValue(XNamespace.Xmlns + "sys", "clr-namespace:System.Windows;assembly=PresentationFramework");
        window.SetAttributeValue("Background", null); // StaticResource Bg는 아래 Resources보다 먼저 파싱되므로 뺀다(색만의 문제)
        window.AddFirst(new XElement(presentation + "Window.Resources", AppResourceDictionary()));
        return QualifyAppNamespaces(window.ToString());
    }

    /// <summary>XamlReader는 컴파일된 XAML과 달리 앱 어셈블리를 모르므로 <c>clr-namespace:CodexBackupManager.App.*</c>에 어셈블리를 붙인다.</summary>
    private static string QualifyAppNamespaces(string xaml)
        => Regex.Replace(xaml, "\"clr-namespace:(CodexBackupManager\\.App\\.[A-Za-z.]+)\"", "\"clr-namespace:$1;assembly=CodexBackupManager\"");

    private static XElement AppResourceDictionary()
    {
        string appDir = Path.Combine(RepositoryFixtures.RepositoryRoot, "src", "CodexBackupManager.App");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XDocument app = XDocument.Load(Path.Combine(appDir, "App.xaml"));
        XElement resources = app.Root!.Element(presentation + "Application.Resources")!;
        return new XElement(presentation + "ResourceDictionary", resources.Elements().Select(e => new XElement(e)));
    }

    // ── 9_2-23~26 화면 다듬기(그려진 화면에서 확인) ──────────────────────────────

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static string AutomationNameOf(UIElement element)
        => System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(element)?.GetName() ?? string.Empty;

    [Fact]
    public async Task 트리_항목은_UI_자동화에서_클래스_이름이_아니라_표시_이름으로_읽힌다()
    {
        _h.RemoveFromTarget(ImportWorkspaceHarness.Thread3);
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);
        var expected = ws.Projects.Select(p => p.AutomationName)
            .Concat(ws.Projects.SelectMany(p => p.Conversations).Select(c => c.AutomationName))
            .ToHashSet();

        var seen = new List<string>();
        AssertRendersWithoutBindingErrors(ws, window =>
        {
            foreach (System.Windows.Controls.TreeViewItem item in Descendants<System.Windows.Controls.TreeViewItem>(window))
            {
                string name = AutomationNameOf(item);
                Assert.DoesNotContain("ViewModel", name);
                Assert.Contains(name, expected);
                seen.Add(name);
            }
        });

        Assert.Contains(seen, n => n.Contains(", 새 대화", StringComparison.Ordinal));
        Assert.Contains(seen, n => n.Contains("이 PC 위치: 기타 대화", StringComparison.Ordinal));
        Assert.Contains("Alpha", seen);
    }

    [Fact]
    public async Task 결과_목록과_메인_트리도_표시_이름으로_읽힌다()
    {
        _h.RemoveFromTarget(ImportWorkspaceHarness.Thread2);
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);
        await ws.ImportAsync();
        string item = ws.Result!.Groups[0].Items[0].AutomationName;

        var resultNames = new List<string>();
        AssertRendersWithoutBindingErrors(ws, window =>
        {
            foreach (System.Windows.Controls.ListBoxItem listItem in Descendants<System.Windows.Controls.ListBoxItem>(window))
            {
                resultNames.Add(AutomationNameOf(listItem));
            }
        });
        Assert.Contains(item, resultNames);
        Assert.Contains("기타 대화 → 기타 대화", resultNames);
        Assert.DoesNotContain(resultNames, n => n.Contains("ViewModel", StringComparison.Ordinal) || n.Contains("ImportResult", StringComparison.Ordinal));

        CodexBackupManager.App.ViewModels.MainViewModel main = _h.CreateMainViewModel(_h.TargetHome);
        await ImportWorkspaceHarness.ConnectAsync(main);
        var mainNames = new List<string>();
        AssertRendersWithoutBindingErrors(main, BuildMainWindowXaml(), window =>
        {
            foreach (System.Windows.Controls.TreeViewItem treeItem in Descendants<System.Windows.Controls.TreeViewItem>(window))
            {
                mainNames.Add(AutomationNameOf(treeItem));
            }
        });
        Assert.Contains("Alpha", mainNames);
        Assert.Contains("기타 대화", mainNames);
        Assert.DoesNotContain(mainNames, n => n.Contains("ViewModel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 검색_칸_자리_표시와_펼침_삼각형과_선택_불가_체크박스()
    {
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup); // 전부 "이미 있음" → 체크 불가

        AssertRendersWithoutBindingErrors(ws, window =>
        {
            // 9_2-24 자리 표시 문구: 비어 있으면 보이고, 입력하면 사라진다.
            System.Windows.Controls.TextBox search = Descendants<System.Windows.Controls.TextBox>(window)
                .Single(t => Equals(t.Tag, "대화 제목 검색"));
            var placeholder = (FrameworkElement)search.Template.FindName("Placeholder", search);
            Assert.Equal(Visibility.Visible, placeholder.Visibility);
            search.Text = "fixture";
            Pump();
            Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
            search.Text = string.Empty;
            Pump();

            // 9_2-25 펼침 삼각형은 머리글 첫 줄(이름 줄) 높이에 있다 — 작업 폴더 상자가 있어도 내려가지 않는다.
            System.Windows.Controls.TreeViewItem project = Descendants<System.Windows.Controls.TreeViewItem>(window).First();
            var expander = (FrameworkElement)project.Template.FindName("Expander", project);
            var header = (FrameworkElement)project.Template.FindName("PART_Header", project);
            double expanderTop = expander.TranslatePoint(new Point(0, 0), project).Y;
            Assert.True(header.ActualHeight > 60, $"머리글 높이 {header.ActualHeight}");
            Assert.True(expanderTop < 12, $"삼각형 위치 {expanderTop}");

            // 9_2-26 선택할 수 없는 대화의 체크박스는 흐리게 보인다.
            System.Windows.Controls.CheckBox disabled = Descendants<System.Windows.Controls.CheckBox>(window).First(c => !c.IsEnabled);
            Assert.Equal(0.35, disabled.Opacity, 3);
        });
    }

    // ── 9_2b-T3 긴 대화 미리보기 스크롤 왕복(FlowDocument 재활용) ─────────────────────

    [Fact]
    public async Task 긴_대화_미리보기를_왕복_스크롤해도_예외가_없다()
    {
        _h.AppendToSourceRollout(ImportWorkspaceHarness.Thread2, 80, "긴 대화");
        _h.RemoveFromTarget(ImportWorkspaceHarness.Thread2);
        string backup = await _h.ExportAsync();
        ImportWorkspaceViewModel ws = await _h.OpenEditingAsync(backup);
        ws.SelectNode(ws.Projects.SelectMany(p => p.Conversations).First(c => c.ThreadId == ImportWorkspaceHarness.Thread2));
        await ImportWorkspaceHarness.ContentLoadedAsync(ws);
        Assert.True(ws.ContentMessages.Count >= 80);

        Exception? unhandled = null;
        int rendered = 0;
        AssertRendersWithoutBindingErrors(ws, window =>
        {
            window.Dispatcher.UnhandledException += (_, e) =>
            {
                unhandled = e.Exception;
                e.Handled = true;
            };

            var list = Descendants<System.Windows.Controls.ListBox>(window).Single(l => l.Name == "ContentList");
            System.Windows.Controls.ScrollViewer viewer = Descendants<System.Windows.Controls.ScrollViewer>(list).First();
            double max = viewer.ScrollableHeight;
            Assert.True(max > 0, "스크롤할 높이가 없습니다.");
            for (int round = 0; round < 2; round++)
            {
                for (double offset = 0; offset <= max; offset += 40)
                {
                    viewer.ScrollToVerticalOffset(offset);
                    Pump();
                }

                for (double offset = max; offset >= 0; offset -= 40)
                {
                    viewer.ScrollToVerticalOffset(offset);
                    Pump();
                }
            }

            rendered = ws.ContentMessages.Count(m => m.IsBodyRendered);
        });

        Assert.Null(unhandled);
        Assert.InRange(rendered, 1, ws.ContentMessages.Count); // 가상화: 보인 것만 FlowDocument를 만든다
    }

    private static void AssertRendersWithoutBindingErrors(ImportWorkspaceViewModel viewModel, Action<Window>? inspect = null)
        => AssertRendersWithoutBindingErrors(viewModel, BuildWindowXaml(), inspect);

    /// <summary>창을 그리고(바인딩 오류 0 확인), <paramref name="inspect"/>로 그려진 화면을 STA 스레드에서 검사한다.</summary>
    private static void AssertRendersWithoutBindingErrors(object viewModel, string windowXaml, Action<Window>? inspect = null)
    {
        var errors = new List<string>();

        RunOnStaThread(() =>
        {
            var listener = new CollectingListener(errors);
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            try
            {
                var window = (Window)XamlReader.Parse(windowXaml);
                window.Width = 1000;
                window.Height = 700;
                window.Left = -20000;
                window.ShowInTaskbar = false;
                window.WindowStyle = WindowStyle.None;
                window.DataContext = viewModel;
                window.Show();
                Pump();
                window.UpdateLayout();
                Pump();
                inspect?.Invoke(window);
                window.Close();
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            }
        });

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static string BuildWindowXaml()
    {
        string appDir = Path.Combine(RepositoryFixtures.RepositoryRoot, "src", "CodexBackupManager.App");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XDocument app = XDocument.Load(Path.Combine(appDir, "App.xaml"));
        XElement resources = app.Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary", resources.Elements().Select(e => new XElement(e)));

        string viewText = File.ReadAllText(Path.Combine(appDir, "Views", "ImportWorkspaceView.xaml"));
        viewText = Regex.Replace(viewText, "\\s+x:Class=\"[^\"]*\"", string.Empty);
        viewText = Regex.Replace(viewText, "\\s+SelectedItemChanged=\"[^\"]*\"", string.Empty);
        XElement view = XElement.Parse(viewText);

        var window = new XElement(presentation + "Window",
            new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "converters", "clr-namespace:CodexBackupManager.App.Converters;assembly=CodexBackupManager"),
            new XAttribute(XNamespace.Xmlns + "sys", "clr-namespace:System.Windows;assembly=PresentationFramework"),
            new XAttribute(XNamespace.Xmlns + "views", "clr-namespace:CodexBackupManager.App.Views;assembly=CodexBackupManager"),
            new XElement(presentation + "Window.Resources", dictionary),
            view);

        return QualifyAppNamespaces(window.ToString());
    }

    internal static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA 스레드가 제한 시간 안에 끝나지 않았습니다.");
        if (caught is not null)
        {
            ExceptionDispatchInfo.Capture(caught).Throw();
        }
    }

    private sealed class CollectingListener(List<string> errors) : TraceListener
    {
        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                lock (errors)
                {
                    errors.Add(message);
                }
            }
        }
    }
}
