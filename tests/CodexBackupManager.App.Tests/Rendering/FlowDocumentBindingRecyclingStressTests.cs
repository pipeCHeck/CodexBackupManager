using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CodexBackupManager.App.Rendering;
using CodexBackupManager.App.ViewModels;
using CodexBackupManager.Domain.Codex.Conversation;
using Xunit;

namespace CodexBackupManager.App.Tests.Rendering;

/// <summary>
/// 실제 사용자가 보고한 크래시("긴 대화를 스크롤하면 앱이 반복적으로 종료됨")의 회귀 테스트.
/// </summary>
/// <remarks>
/// <para>
/// <b>Root cause(실측 확인):</b> <see cref="ConversationMessageViewModel.Body"/>는 메시지 하나당
/// <see cref="System.Windows.Documents.FlowDocument"/> 인스턴스 하나를 계속 재사용한다(즉시 생성이든
/// <c>Lazy&lt;T&gt;</c>든 동일 — Phase 4 04_02/04_03 양쪽 다 재현됨, eager/lazy 자체는 원인이 아니다).
/// <c>VirtualizationMode="Recycling"</c>인 <c>ListBox</c>는 스크롤에 따라 같은 항목을 서로 다른
/// <c>RichTextBox</c> 컨테이너로 여러 번 바꿔 보여줄 수 있는데, 빠르게 스크롤 방향을 바꾸면 컨테이너
/// 재활용 순서가 겹쳐서 "이 FlowDocument가 아직 다른 살아있는 RichTextBox의 Document로 남아있는"
/// 상태에서 새 컨테이너에 재대입되는 경우가 실제로 생긴다. <c>FlowDocument</c>는
/// <c>FrameworkContentElement</c>라 논리 부모가 하나만 허용되므로
/// <c>RichTextBox.set_Document</c>가 <c>ArgumentException</c>("문서가 이미 다른 RichTextBox에
/// 속합니다")을 던진다. <see cref="FlowDocumentBinding.SetDocument"/>가 대입 전에 이전 소유자를
/// 강제로 떼어내도록 고쳐서 해결했다(<see cref="FlowDocumentBinding"/> remarks 참고).
/// </para>
/// <para>
/// 실제 WPF STA Dispatcher + 실제 가상화 <c>ListBox</c> + 실제 <c>RichTextBox</c> +
/// <see cref="FlowDocumentBinding"/>으로 재현한다 — <c>vm.Body</c> getter만 확인하는 단위 테스트로는
/// 이 버그를 잡을 수 없었다(컨테이너 재활용 자체가 필요하기 때문).
/// </para>
/// </remarks>
public sealed class FlowDocumentBindingRecyclingStressTests
{
    /// <summary>
    /// Phase 08_01 — <see cref="FlowDocumentBinding"/>의 핵심 수정(재사용되는 <see cref="System.Windows.Documents.FlowDocument"/>를
    /// 다른 살아있는 <see cref="RichTextBox"/>에 재대입하기 전에 이전 소유자에서 강제로 떼어낸다)을
    /// 실제 ListBox/스크롤/가상화 없이 밀리초 단위로 결정적으로 검증한다. 아래
    /// <see cref="빠른_스크롤_왕복에서도_컨테이너_재활용이_크래시하지_않는다"/>는 이 로직이 실제
    /// WPF 가상화 재활용 상황에서도 트리거되는지 확인하는 통합 스트레스 테스트이고, 이 테스트는 그
    /// 로직 자체의 정확성만 별도로 빠르게 보장한다 — CI가 느려도 이 테스트는 영향받지 않는다.
    /// </summary>
    [Fact]
    public void 같은_FlowDocument를_다른_RichTextBox에_대입하면_이전_소유자에서_강제로_떼어낸다()
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                var document = new System.Windows.Documents.FlowDocument();
                var first = new RichTextBox();
                var second = new RichTextBox();

                FlowDocumentBinding.SetDocument(first, document);
                Assert.Same(document, first.Document);

                // first가 아직 document를 갖고 있는 상태에서 같은 document를 second에도 대입한다 —
                // 수정 전 코드였다면 여기서 RichTextBox.set_Document가
                // ArgumentException("문서가 이미 다른 RichTextBox에 속합니다")을 던졌다.
                FlowDocumentBinding.SetDocument(second, document);

                Assert.Same(document, second.Document);
                Assert.NotSame(document, first.Document); // 강제로 떼어내져 빈 문서로 교체됐다.
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
        Assert.Null(caught);
    }

    [Fact]
    public void 빠른_스크롤_왕복에서도_컨테이너_재활용이_크래시하지_않는다()
    {
        Exception? caught = null;

        var thread = new Thread(() =>
        {
            try
            {
                caught = RunStressScrollOnCurrentThread();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        // messageCount 축소(80) 이후 로컬에서는 1~3초면 끝난다(예전 600개 버전은 21~22초로 30초
        // 제한과 거의 여유가 없었다) — 이제는 실행 시간 자체가 넉넉하므로, CI가 느려도 정상 종료는
        // 훨씬 앞서 끝나고, 이 60초는 오직 "진짜 deadlock"만 잡아내는 안전망 역할만 한다.
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA 스크롤 스트레스 스레드가 제한 시간 안에 끝나지 않았습니다.");

        Assert.Null(caught);
    }

    /// <summary>
    /// 이 스레드에서 실제 Window/ListBox/RichTextBox를 만들고 빠르게 왕복 스크롤한다.
    /// 예외가 나면 그 예외를 돌려준다(<c>System.Windows.Application</c>은 프로세스에 하나만 만들 수
    /// 있어 xunit처럼 같은 프로세스에서 여러 테스트가 도는 환경과 맞지 않으므로, <see cref="Application"/>
    /// 없이 <see cref="Dispatcher"/>만으로 메시지 루프를 돌린다).
    /// </summary>
    private static Exception? RunStressScrollOnCurrentThread()
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        Exception? caught = null;

        dispatcher.UnhandledException += (_, args) =>
        {
            caught = args.Exception;
            args.Handled = true;
            dispatcher.InvokeShutdown();
        };

        var messages = new ObservableCollection<ConversationMessageViewModel>();
        // Phase 08_01 — 600개(원래 재현 2000개보다 이미 줄인 값)로 GitHub Actions CI에서 단독
        // 실행에도 21~22초가 걸려 30초 timeout과 여유가 거의 없었다(CI 병렬 부하에서는 실제로
        // FAIL했다). 80개로 더 줄여도 버그는 여전히 매 회 재현된다(RED로 반복 확인) — 실행 시간은
        // 약 1~2초로 줄어 CI 부하에도 충분한 여유를 갖는다.
        const int messageCount = 80;
        for (int i = 0; i < messageCount; i++)
        {
            string text = (i % 5) switch
            {
                0 => $"# 제목 {i}\n\n설명 문단입니다.\n\n1. 첫째\n2. 둘째\n3. 셋째",
                1 => $"- 불릿 하나\n- 불릿 둘\n\n일반 문단 {i}",
                2 => $"```\nvar x = {i};\n```",
                3 => $"인라인 `code_{i}` 포함.",
                _ => $"평범한 메시지 본문 {i}.\n둘째 줄입니다.",
            };

            var domainMessage = new ConversationMessage
            {
                Role = i % 2 == 0 ? ConversationRole.User : ConversationRole.Assistant,
                Text = text,
            };
            messages.Add(ConversationMessageViewModel.FromDomain(domainMessage));
        }

        var listBox = new ListBox { ItemsSource = messages, BorderThickness = new Thickness(0) };
        VirtualizingPanel.SetIsVirtualizing(listBox, true);
        VirtualizingPanel.SetVirtualizationMode(listBox, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(listBox, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(listBox, true);

        var template = new DataTemplate();
        var richTextBoxFactory = new FrameworkElementFactory(typeof(RichTextBox));
        richTextBoxFactory.SetValue(RichTextBox.IsReadOnlyProperty, true);
        richTextBoxFactory.SetValue(RichTextBox.BorderThicknessProperty, new Thickness(0));
        richTextBoxFactory.SetValue(RichTextBox.BackgroundProperty, Brushes.Transparent);
        richTextBoxFactory.SetBinding(FlowDocumentBinding.DocumentProperty, new Binding("Body"));
        template.VisualTree = richTextBoxFactory;
        listBox.ItemTemplate = template;

        var window = new Window { Width = 900, Height = 700, Content = listBox, ShowActivated = false };

        window.Loaded += async (_, _) =>
        {
            try
            {
                await StressScrollAsync(listBox);
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            finally
            {
                dispatcher.InvokeShutdown();
            }
        };

        window.Show();
        Dispatcher.Run();
        window.Close();

        return caught;
    }

    private static async Task StressScrollAsync(ListBox listBox)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        ScrollViewer? scrollViewer = FindScrollViewer(listBox);
        if (scrollViewer is null)
        {
            throw new InvalidOperationException("ScrollViewer를 찾지 못했습니다.");
        }

        double max = scrollViewer.ScrollableHeight;
        if (max <= 0)
        {
            throw new InvalidOperationException("ScrollableHeight가 0입니다 — 컨테이너 재활용을 재현할 만한 콘텐츠 높이가 없습니다.");
        }

        // 왕복을 여러 번 반복해 컨테이너 재활용을 충분히 stress한다(실제 크래시는 첫 왕복 안에서도
        // 재현됐다). Phase 08_01 — 40px 촘촘한 단계는 그대로 유지한다: 실제로 시도해 보니 몇 개의
        // 큰 지점으로 "점프"하는 방식(예: 상대 위치 0/50/100% 등 고정된 지점만 방문)은 실행은
        // 훨씬 빨라지지만 실제 컨테이너 재활용 race를 더 이상 재현하지 못했다(RED 확인 — 버그를
        // 되돌려도 통과했다). 대신 messageCount(아래, 600 → 80)를 줄여 ScrollableHeight 자체를
        // 훨씬 작게 만들었다 — 같은 촘촘한 40px 알고리즘이 이제 훨씬 적은 단계로 끝나면서도
        // (RED로 재확인) 버그를 여전히 매번 재현한다.
        for (int round = 0; round < 3; round++)
        {
            for (double offset = 0; offset <= max; offset += 40)
            {
                scrollViewer.ScrollToVerticalOffset(offset);
                await Dispatcher.Yield(DispatcherPriority.Render);
            }

            for (double offset = max; offset >= 0; offset -= 40)
            {
                scrollViewer.ScrollToVerticalOffset(offset);
                await Dispatcher.Yield(DispatcherPriority.Render);
            }
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv)
        {
            return sv;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            ScrollViewer? found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
