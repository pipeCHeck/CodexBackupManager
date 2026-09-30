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
        _h.CodexRunning = true;
        _h.PollCallback!(); // 배너도 함께 그린다

        AssertRendersWithoutBindingErrors(ws);

        ws.SelectNode(ws.Projects[0]);
        AssertRendersWithoutBindingErrors(ws);
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
        return window.ToString().Replace(
            "\"clr-namespace:CodexBackupManager.App.Converters\"",
            "\"clr-namespace:CodexBackupManager.App.Converters;assembly=CodexBackupManager\"");
    }

    private static XElement AppResourceDictionary()
    {
        string appDir = Path.Combine(RepositoryFixtures.RepositoryRoot, "src", "CodexBackupManager.App");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XDocument app = XDocument.Load(Path.Combine(appDir, "App.xaml"));
        XElement resources = app.Root!.Element(presentation + "Application.Resources")!;
        return new XElement(presentation + "ResourceDictionary", resources.Elements().Select(e => new XElement(e)));
    }

    private static void AssertRendersWithoutBindingErrors(ImportWorkspaceViewModel viewModel)
        => AssertRendersWithoutBindingErrors(viewModel, BuildWindowXaml());

    private static void AssertRendersWithoutBindingErrors(object viewModel, string windowXaml)
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

        string text = window.ToString();
        // 파싱 과정에서 붙은 뷰 쪽 xmlns 선언도 어셈블리를 명시하도록 맞춘다.
        text = text.Replace("\"clr-namespace:CodexBackupManager.App.Views\"", "\"clr-namespace:CodexBackupManager.App.Views;assembly=CodexBackupManager\"");
        text = text.Replace("\"clr-namespace:CodexBackupManager.App.Converters\"", "\"clr-namespace:CodexBackupManager.App.Converters;assembly=CodexBackupManager\"");
        return text;
    }

    private static void Pump()
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
