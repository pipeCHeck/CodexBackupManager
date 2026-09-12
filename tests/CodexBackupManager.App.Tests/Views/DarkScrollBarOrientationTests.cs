using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Xml.Linq;
using CodexBackupManager.App.Tests.TestSupport;
using Xunit;

namespace CodexBackupManager.App.Tests.Views;

/// <summary>
/// Phase 04_07 — <c>App.xaml</c>의 암시적 다크 <see cref="ScrollBar"/> 스타일이 orientation을
/// 올바르게 따라가는지 확인한다.
/// </summary>
/// <remarks>
/// <para>
/// <b>배경.</b> <see cref="Track"/>의 <see cref="Track.Orientation"/>은
/// <see cref="ScrollBar.Orientation"/>과 별개의 의존 속성이라, <c>ControlTemplate</c>에서 명시적으로
/// <c>TemplateBinding</c>하지 않으면 horizontal <c>ScrollBar</c>에서도 <c>Track</c>이 계속 세로로
/// 배치된다 — 처음 다크 ScrollBar 스타일을 도입했을 때 실제로 이 연결이 빠져 있었다. 또한 Track 클릭으로
/// "한 페이지" 스크롤할 때 vertical은 PageUp/PageDown, horizontal은 PageLeft/PageRight 커맨드를 써야
/// <see cref="ScrollViewer"/>에 올바른 <c>ScrollEventType</c>이 전달된다.
/// </para>
/// <para>
/// <c>App.xaml</c>을 직접 <see cref="XamlReader"/>로 새로 인스턴스화하지 않는다(같은 프로세스에서 여러
/// 테스트가 <see cref="System.Windows.Application"/>을 동시에 만들 수 없다는 기존 제약,
/// <see cref="Rendering.FlowDocumentBindingRecyclingStressTests"/> remarks 참고). 대신 실제
/// <c>App.xaml</c> 소스 파일에서 이 스타일이 의존하는 리소스(브러시 3개 + <c>DarkScrollBarThumb</c> +
/// <c>ScrollBar</c> 암시적 스타일)만 그대로 오려내 독립적인 <see cref="ResourceDictionary"/>로
/// 파싱한다 — 그래서 실제 프로덕션 마크업을 검증하면서도 Application 싱글턴 문제를 피한다.
/// </para>
/// </remarks>
public sealed class DarkScrollBarOrientationTests
{
    [Fact]
    public void Vertical_ScrollBar는_PageUp_PageDown과_역방향_Track을_쓴다()
    {
        RunOnStaThread(() =>
        {
            Style style = LoadScrollBarStyleFromAppXaml();

            var scrollBar = new ScrollBar { Orientation = Orientation.Vertical, Style = style };
            scrollBar.ApplyTemplate();

            var track = Assert.IsType<Track>(scrollBar.Template.FindName("PART_Track", scrollBar));
            Assert.Equal(Orientation.Vertical, track.Orientation);
            Assert.True(track.IsDirectionReversed);
            Assert.Equal(ScrollBar.PageUpCommand, track.DecreaseRepeatButton.Command);
            Assert.Equal(ScrollBar.PageDownCommand, track.IncreaseRepeatButton.Command);
        });
    }

    [Fact]
    public void Horizontal_ScrollBar는_Track_Orientation을_따라가고_PageLeft_PageRight를_쓴다()
    {
        RunOnStaThread(() =>
        {
            Style style = LoadScrollBarStyleFromAppXaml();

            var scrollBar = new ScrollBar { Orientation = Orientation.Horizontal, Style = style };
            scrollBar.ApplyTemplate();

            var track = Assert.IsType<Track>(scrollBar.Template.FindName("PART_Track", scrollBar));
            Assert.Equal(Orientation.Horizontal, track.Orientation); // Track이 ScrollBar.Orientation을 따라가는지(핵심 회귀 지점).
            Assert.False(track.IsDirectionReversed);
            Assert.Equal(ScrollBar.PageLeftCommand, track.DecreaseRepeatButton.Command);
            Assert.Equal(ScrollBar.PageRightCommand, track.IncreaseRepeatButton.Command);

            // Style.Triggers: horizontal에서는 두께를 Height로, 폭은 Auto로 바꾼다(9px 두께 유지).
            Assert.True(double.IsNaN(scrollBar.Width));
            Assert.Equal(9d, scrollBar.Height);
        });
    }

    [Fact]
    public void Vertical_ScrollBar는_두께가_9px_Width다()
    {
        RunOnStaThread(() =>
        {
            Style style = LoadScrollBarStyleFromAppXaml();

            var scrollBar = new ScrollBar { Orientation = Orientation.Vertical, Style = style };
            scrollBar.ApplyTemplate();

            Assert.Equal(9d, scrollBar.Width);
        });
    }

    /// <summary>
    /// <c>App.xaml</c> 소스에서 다크 ScrollBar 스타일과 그 의존 리소스만 오려내
    /// 독립 <see cref="ResourceDictionary"/>로 만든다.
    /// </summary>
    private static Style LoadScrollBarStyleFromAppXaml()
    {
        string appXamlPath = Path.Combine(
            RepositoryFixtures.RepositoryRoot, "src", "CodexBackupManager.App", "App.xaml");
        XDocument document = XDocument.Load(appXamlPath);

        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        string[] requiredBrushKeys = ["ScrollThumb", "ScrollThumbHover", "ScrollThumbPressed"];
        var brushes = document.Descendants(presentation + "SolidColorBrush")
            .Where(e => requiredBrushKeys.Contains((string?)e.Attribute(x + "Key")))
            .ToList();
        Assert.Equal(requiredBrushKeys.Length, brushes.Count); // App.xaml 구조가 바뀌면 여기서 바로 드러난다.

        XElement thumbStyle = document.Descendants(presentation + "Style")
            .Single(e => (string?)e.Attribute(x + "Key") == "DarkScrollBarThumb");

        XElement scrollBarStyle = document.Descendants(presentation + "Style")
            .Single(e => (string?)e.Attribute("TargetType") == "ScrollBar" && e.Attribute(x + "Key") is null);

        var wrapper = new XElement(
            presentation + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName),
            brushes.Select(b => new XElement(b)),
            new XElement(thumbStyle),
            new XElement(scrollBarStyle));

        var dictionary = (ResourceDictionary)XamlReader.Parse(wrapper.ToString());
        return Assert.IsType<Style>(dictionary[typeof(ScrollBar)]);
    }

    /// <summary>
    /// WPF 의존 객체는 STA 스레드가 필요하다(xunit 기본 스레드는 STA가 아닐 수 있음). 다른 테스트가
    /// <see cref="System.Windows.Application"/>을 만들지 않는 것과 마찬가지로 이 테스트도 만들지 않는다
    /// — Style/Template만 있으면 되고 Application 리소스 조회는 필요 없다.
    /// </summary>
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
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA 스레드가 제한 시간 안에 끝나지 않았습니다.");

        if (caught is not null)
        {
            ExceptionDispatchInfo.Capture(caught).Throw();
        }
    }
}
