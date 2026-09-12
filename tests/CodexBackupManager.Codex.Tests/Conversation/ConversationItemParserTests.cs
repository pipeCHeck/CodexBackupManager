using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexBackupManager.Codex.Conversation;
using CodexBackupManager.Domain.Codex.Conversation;
using CodexBackupManager.Domain.Codex.Rollout;
using Xunit;
using ZstdSharp;

namespace CodexBackupManager.Codex.Tests.Conversation;

public sealed class ConversationItemParserTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cbm-tests", Guid.NewGuid().ToString("N"));

    public ConversationItemParserTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private RolloutFileReference WriteFile(
        IEnumerable<string> lines, string fileName = "a.jsonl", RolloutFileKind kind = RolloutFileKind.PlainJsonl)
    {
        string path = Path.Combine(_directory, fileName);
        string content = string.Join("\n", lines) + "\n";

        if (kind == RolloutFileKind.ZstdCompressed)
        {
            using FileStream fileStream = new(path, FileMode.Create, FileAccess.Write);
            using CompressionStream compressionStream = new(fileStream, leaveOpen: true);
            using StreamWriter writer = new(compressionStream);
            writer.Write(content);
        }
        else
        {
            File.WriteAllText(path, content);
        }

        return new RolloutFileReference(path, fileName, "thread-x", null, null, IsArchived: false, kind);
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    private static string EventMsgLine(long ordinal, string itemType, string itemId, string[] texts, string? phase = null)
    {
        string contentJson = string.Join(",", texts.Select(t => "{\"type\":\"text\",\"text\":\"" + Escape(t) + "\"}"));
        string phaseJson = phase is null ? string.Empty : ",\"phase\":\"" + phase + "\"";
        return "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":" + ordinal +
               ",\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"thread_id\":\"thread-x\",\"turn_id\":\"turn-1\"," +
               "\"item\":{\"type\":\"" + itemType + "\",\"id\":\"" + itemId + "\",\"content\":[" + contentJson + "]" + phaseJson + "}}}";
    }

    private static string ResponseItemLine(long ordinal, string role, string[] texts)
    {
        string contentType = role == "assistant" ? "output_text" : "input_text";
        string contentJson = string.Join(",", texts.Select(t => "{\"type\":\"" + contentType + "\",\"text\":\"" + Escape(t) + "\"}"));
        return "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":" + ordinal +
               ",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"id\":\"msg-" + ordinal + "\",\"role\":\"" + role +
               "\",\"content\":[" + contentJson + "]}}";
    }

    /// <summary>content 배열을 원하는 raw JSON으로 직접 지정한다(텍스트가 없는 첨부/이미지 등 표현용).</summary>
    private static string ResponseItemLineRawContent(long ordinal, string role, string contentJson)
        => "{\"timestamp\":\"2026-01-02T03:04:05.000Z\",\"ordinal\":" + ordinal +
           ",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"id\":\"msg-" + ordinal + "\",\"role\":\"" + role +
           "\",\"content\":[" + contentJson + "]}}";

    [Fact]
    public void UserMessage_한_개를_파싱한다()
    {
        RolloutFileReference file = WriteFile([EventMsgLine(1, "UserMessage", "u1", ["안녕하세요"])]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        ConversationMessage message = Assert.Single(result.Messages);
        Assert.Equal(ConversationRole.User, message.Role);
        Assert.Equal("안녕하세요", message.Text);
        Assert.Equal("u1", message.ItemId);
        Assert.Equal(1, message.Ordinal);
        Assert.Null(message.Phase);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public void AgentMessage_final을_파싱하고_phase를_보존한다()
    {
        RolloutFileReference file = WriteFile([EventMsgLine(1, "AgentMessage", "a1", ["완료했습니다"], "final")]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        ConversationMessage message = Assert.Single(result.Messages);
        Assert.Equal(ConversationRole.Assistant, message.Role);
        Assert.Equal(AssistantPhase.Final, message.Phase);
    }

    [Fact]
    public void AgentMessage_commentary도_파싱한다()
    {
        RolloutFileReference file = WriteFile([EventMsgLine(1, "AgentMessage", "a1", ["진행 중입니다"], "commentary")]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        Assert.Equal(AssistantPhase.Commentary, Assert.Single(result.Messages).Phase);
    }

    [Fact]
    public void 여러_text_content_조각을_합친다()
    {
        RolloutFileReference file = WriteFile([EventMsgLine(1, "UserMessage", "u1", ["첫 조각", "둘째 조각"])]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        string text = Assert.Single(result.Messages).Text;
        Assert.Contains("첫 조각", text);
        Assert.Contains("둘째 조각", text);
    }

    [Fact]
    public void 빈_content는_메시지를_만들지_않는다()
    {
        RolloutFileReference file = WriteFile([EventMsgLine(1, "UserMessage", "u1", [])]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        Assert.Empty(result.Messages);
    }

    [Fact]
    public void 알수없는_item_type은_건너뛰고_예외를_던지지_않는다()
    {
        RolloutFileReference file = WriteFile(
        [
            EventMsgLine(1, "Reasoning", "r1", ["숨겨야 함"]),
            EventMsgLine(2, "CommandExecution", "c1", ["ls -la"]),
            EventMsgLine(3, "UserMessage", "u1", ["실제 메시지"]),
        ]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        ConversationMessage message = Assert.Single(result.Messages);
        Assert.Equal("실제 메시지", message.Text);
    }

    [Fact]
    public void event_msg가_없으면_response_item으로_폴백한다()
    {
        RolloutFileReference file = WriteFile([ResponseItemLine(1, "user", ["폴백 메시지"])]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        Assert.True(result.UsedFallback);
        ConversationMessage message = Assert.Single(result.Messages);
        Assert.Equal(ConversationRole.User, message.Role);
        Assert.Equal("폴백 메시지", message.Text);
    }

    [Fact]
    public void response_item_developer_role은_폴백에서도_숨긴다()
    {
        RolloutFileReference file = WriteFile(
        [
            ResponseItemLine(1, "developer", ["<app-context>숨김</app-context>"]),
            ResponseItemLine(2, "user", ["진짜 사용자 메시지"]),
        ]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        ConversationMessage message = Assert.Single(result.Messages);
        Assert.Equal("진짜 사용자 메시지", message.Text);
    }

    [Fact]
    public void role이_assistant이면_마커와_모양이_같아도_숨기지_않는다()
    {
        // OpenAI Codex 공식 소스(codex-rs/core/src/context/) 조사 결과: 우리가 확인한 5개
        // 주입 마커(<app-context>/<recommended_plugins>/<turn_aborted>/<multi_agent_mode>/
        // <environment_context>)는 전부 role="user" 또는 role="developer" fragment이고,
        // role="assistant"로 정의된 fragment(예: 멀티 에이전트 inter-agent 메시지)는 애초에
        // 빈 마커("", "")를 쓴다 — matches_marked_text는 빈 마커와는 절대 매치하지 않는다.
        // 즉 "확인된 마커"는 assistant가 실제로 만들어낼 수 있는 모양이 아니므로, assistant
        // 메시지에 필터를 적용하면 모델이 그 태그를 예시/설명으로 언급한 진짜 출력만 잘못 지운다.
        RolloutFileReference file = WriteFile(
        [
            ResponseItemLine(1, "assistant", ["<environment_context>hello</environment_context>"]),
        ]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        Assert.Equal("<environment_context>hello</environment_context>", Assert.Single(result.Messages).Text);
    }

    [Fact]
    public void 주입된_시스템_컨텍스트는_role이_user여도_숨긴다()
    {
        // 실제 .codex 데이터에서 확인된 사례: role="user" response_item인데 내용이
        // <recommended_plugins> 시스템 주입문인 경우가 있다.
        RolloutFileReference file = WriteFile(
        [
            ResponseItemLine(1, "user", ["<recommended_plugins>\n설치 안 된 플러그인 목록\n</recommended_plugins>"]),
            ResponseItemLine(2, "user", ["진짜 사용자 메시지"]),
        ]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        ConversationMessage message = Assert.Single(result.Messages);
        Assert.Equal("진짜 사용자 메시지", message.Text);
    }

    [Fact]
    public void 확인된_마커_쌍으로_시작하고_끝나는_environment_context는_숨긴다()
    {
        // Phase 3 pre-commit audit 실측: <recommended_plugins>/<app-context>/<turn_aborted>/
        // <multi_agent_mode> 외에 <environment_context>라는 마커도 실제 데이터에서 확인됐다.
        // 공식 Codex 소스(ContextualUserFragment::matches_marked_text)처럼 시작+종료 마커
        // "쌍"이 정확히 일치할 때만 숨긴다 — 임의의 소문자 태그 구조 전체를 숨기지 않는다.
        RolloutFileReference file = WriteFile(
        [
            ResponseItemLine(1, "user", ["<environment_context>\n작업 디렉터리 정보\n</environment_context>"]),
            ResponseItemLine(2, "user", ["진짜 사용자 메시지"]),
        ]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        ConversationMessage message = Assert.Single(result.Messages);
        Assert.Equal("진짜 사용자 메시지", message.Text);
    }

    [Fact]
    public void 마커_대소문자가_달라도_공식_소스와_동일하게_대소문자_구분_없이_숨긴다()
    {
        // 공식 Codex 소스(codex-rs/context-fragments/src/fragment.rs::matches_marked_text)를
        // 실제로 확인한 결과 시작/종료 마커 비교에 eq_ignore_ascii_case를 쓴다 — 즉 대소문자를
        // "구분하지 않는다"(case-insensitive). 대소문자를 엄격히 구분하도록 바꾸면 오히려 공식
        // 동작과 달라지므로, 대문자로 바뀐 마커도 여전히 숨겨지는 게 맞는 동작이다.
        RolloutFileReference file = WriteFile(
        [
            ResponseItemLine(1, "user", ["<ENVIRONMENT_CONTEXT>\n작업 디렉터리 정보\n</ENVIRONMENT_CONTEXT>"]),
            ResponseItemLine(2, "user", ["진짜 사용자 메시지"]),
        ]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        ConversationMessage message = Assert.Single(result.Messages);
        Assert.Equal("진짜 사용자 메시지", message.Text);
    }

    [Fact]
    public void 태그처럼_보이지_않는_꺾쇠로_시작하는_진짜_메시지는_숨기지_않는다()
    {
        // "<3 감사합니다!"는 확인된 마커 목록 어디에도 없으므로 숨기면 안 된다.
        RolloutFileReference file = WriteFile([ResponseItemLine(1, "user", ["<3 감사합니다!"])]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        Assert.Equal("<3 감사합니다!", Assert.Single(result.Messages).Text);
    }

    [Fact]
    public void code_태그로_시작하는_진짜_사용자_메시지는_보존한다()
    {
        // 실제 사용자가 <code>...</code> 같은 정상 HTML/코드 조각으로 메시지를 시작할 수 있다.
        // "소문자 태그로 시작" 같은 구조 규칙으로 판정하면 이런 메시지가 잘못 삭제된다.
        RolloutFileReference file = WriteFile([ResponseItemLine(1, "user", ["<code>hello</code>"])]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        Assert.Equal("<code>hello</code>", Assert.Single(result.Messages).Text);
    }

    [Fact]
    public void summary_태그로_시작하는_진짜_사용자_메시지는_보존한다()
    {
        RolloutFileReference file = WriteFile([ResponseItemLine(1, "user", ["<summary>hello</summary>"])]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        Assert.Equal("<summary>hello</summary>", Assert.Single(result.Messages).Text);
    }

    [Fact]
    public void xml_태그로_시작하는_진짜_사용자_메시지는_보존한다()
    {
        RolloutFileReference file = WriteFile([ResponseItemLine(1, "user", ["<xml>hello</xml>"])]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        Assert.Equal("<xml>hello</xml>", Assert.Single(result.Messages).Text);
    }

    [Fact]
    public void 서로_다른_주입_마커_쌍이_같은_메시지_안에서_각각_조각으로_들어와도_모두_제거하고_실제_텍스트만_남긴다()
    {
        // 실측(2026-08-27 rollout): role=user response_item 하나의 content 배열 안에
        // <recommended_plugins>...</recommended_plugins> 조각과 <environment_context>...</environment_context>
        // 조각이 각각 별도의 content 원소로 함께 들어오는 사례가 있다. 전체를 하나로 합친 뒤
        // "시작 마커 A + 끝 마커 B"로 판정하면 어느 쌍과도 맞지 않아 필터를 통과해버린다.
        // content 원소 단위로 각각 판정해야 두 조각 모두 제거된다.
        RolloutFileReference file = WriteFile(
        [
            ResponseItemLine(1, "user",
            [
                "<recommended_plugins>\n설치 안 된 플러그인 목록\n</recommended_plugins>",
                "<environment_context>\n작업 디렉터리 정보\n</environment_context>",
            ]),
            ResponseItemLine(2, "user", ["진짜 사용자 메시지"]),
        ]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        ConversationMessage message = Assert.Single(result.Messages);
        Assert.Equal("진짜 사용자 메시지", message.Text);
    }

    [Fact]
    public void event_msg가_있으면_같은_파일의_response_item은_무시해_중복을_막는다()
    {
        RolloutFileReference file = WriteFile(
        [
            ResponseItemLine(1, "user", ["wire 포맷 사본"]),
            EventMsgLine(2, "UserMessage", "u1", ["UI 포맷 원본"]),
        ]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        Assert.False(result.UsedFallback);
        ConversationMessage message = Assert.Single(result.Messages);
        Assert.Equal("UI 포맷 원본", message.Text);
    }

    [Fact]
    public void 마지막_줄이_손상되어도_앞선_메시지는_그대로_돌려준다()
    {
        RolloutFileReference file = WriteFile(
        [
            EventMsgLine(1, "UserMessage", "u1", ["정상 메시지"]),
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"typ",
        ]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        Assert.Equal("정상 메시지", Assert.Single(result.Messages).Text);
    }

    [Fact]
    public void 압축된_zst_파일도_동일한_결과를_준다()
    {
        RolloutFileReference file = WriteFile(
            [EventMsgLine(1, "UserMessage", "u1", ["압축 파일 메시지"])],
            fileName: "a.jsonl.zst",
            kind: RolloutFileKind.ZstdCompressed);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        Assert.Equal("압축 파일 메시지", Assert.Single(result.Messages).Text);
    }

    [Fact]
    public void ordinal_cutoff_이후_메시지는_제외한다()
    {
        RolloutFileReference file = WriteFile(
        [
            EventMsgLine(0, "UserMessage", "u1", ["포함됨"]),
            EventMsgLine(1, "AgentMessage", "a1", ["경계선(제외)"], "final"),
            EventMsgLine(2, "UserMessage", "u2", ["이후(제외)"]),
        ]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFileWithOrdinalCutoff(file, maxOrdinalExclusive: 1);

        ConversationMessage message = Assert.Single(result.Messages);
        Assert.Equal("포함됨", message.Text);
    }

    // ── Phase 04_05: 빈 User 메시지 정합성 ──────────────────────────────────────────
    //
    // 공식 Codex 소스(tui/src/chatwidget.rs의 on_user_message_display,
    // app-server-protocol/src/protocol/thread_history.rs의 build_user_inputs)를 확인한 결과,
    // 공식 정책은 정확히 "trim() 후 비어 있고, 텍스트/이미지 등 어떤 콘텐츠도 없으면 메시지 자체를
    // 만들지 않는다"였다. 반대로 이미지 등 텍스트가 아닌 콘텐츠는 "[Image #N]" 같은 placeholder로
    // 표시한다(protocol/src/models.rs의 local_image_label_text). 아래 테스트는 이 확인된 공식
    // 정책을 그대로 반영한다 — 실제 이 PC의 .codex 데이터 4013+1648건 실측에서는 공백만 있거나
    // zero-width만 있는 User 메시지, 첨부-only 메시지가 하나도 발견되지 않았지만(감사 결과 0건),
    // 공식 소스가 이 정책을 명시적으로 확인해주므로 실제 데이터 사례 없이도 반영한다. 다만 공식
    // 소스도 zero-width 문자를 "비어있음"으로 특별 취급하지 않으므로(표준 trim()만 사용), 우리도
    // zero-width/control 문자에 대한 별도 필터는 추가하지 않는다(과설계 방지).

    [Theory]
    [InlineData(" ")]
    [InlineData("\n")]
    [InlineData("\r\n\t")]
    [InlineData("   \n  \t ")]
    public void 공백만_있는_User_response_item은_숨긴다(string whitespaceOnly)
    {
        RolloutFileReference file = WriteFile(
        [
            ResponseItemLine(1, "user", [whitespaceOnly]),
            ResponseItemLine(2, "user", ["진짜 사용자 메시지"]),
        ]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        ConversationMessage message = Assert.Single(result.Messages);
        Assert.Equal("진짜 사용자 메시지", message.Text);
    }

    [Fact]
    public void 공백만_있는_UserMessage_event_msg도_숨긴다()
    {
        RolloutFileReference file = WriteFile(
        [
            EventMsgLine(1, "UserMessage", "u1", ["\n\n  "]),
            EventMsgLine(2, "UserMessage", "u2", ["진짜 사용자 메시지"]),
        ]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        ConversationMessage message = Assert.Single(result.Messages);
        Assert.Equal("진짜 사용자 메시지", message.Text);
    }

    [Fact]
    public void 텍스트_없이_이미지만_있는_User_메시지는_placeholder로_표시한다()
    {
        // 공식 Codex의 "[Image #N]" 표시와 같은 취지 — 사용자 데이터(첨부했다는 사실)를 조용히
        // 지우지 않고, 텍스트가 아닌 콘텐츠가 있었다는 것을 사용자에게 보여준다.
        RolloutFileReference file = WriteFile(
        [
            ResponseItemLineRawContent(1, "user", "{\"type\":\"input_image\",\"image_url\":\"data:...\"}"),
        ]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        ConversationMessage message = Assert.Single(result.Messages);
        Assert.Equal(ConversationRole.User, message.Role);
        Assert.Equal("[이미지]", message.Text);
    }

    [Fact]
    public void 빈_문자열_text_속성만_있으면_첨부파일_placeholder가_아니라_그냥_숨긴다()
    {
        // 실측(2026-08-31 rollout): AgentMessage content 원소가 {"type":"...","text":""}처럼
        // "text" 키는 있지만 값이 빈 문자열인 경우가 실제로 있었다. 이건 이미지/첨부가 아니라
        // 그냥 빈 텍스트이므로 "[첨부 파일]" 같은 오해를 주는 placeholder를 보여주면 안 되고,
        // 공백만 있는 경우와 동일하게 그냥 숨긴다("text" 키의 유무가 아니라 빈 문자열 여부로
        // 첨부 여부를 판단하면 이 케이스를 attachment로 잘못 분류하게 된다).
        RolloutFileReference file = WriteFile(
        [
            ResponseItemLineRawContent(1, "assistant", "{\"type\":\"output_text\",\"text\":\"\"}"),
            ResponseItemLine(2, "assistant", ["진짜 답변"]),
        ]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        ConversationMessage message = Assert.Single(result.Messages);
        Assert.Equal("진짜 답변", message.Text);
    }

    [Fact]
    public void 텍스트도_이미지도_없이_완전히_빈_content_배열은_계속_숨긴다()
    {
        RolloutFileReference file = WriteFile(
        [
            ResponseItemLineRawContent(1, "user", string.Empty),
            ResponseItemLine(2, "user", ["진짜 사용자 메시지"]),
        ]);

        ConversationItemParser.ParseResult result = ConversationItemParser.ParseFile(file);

        ConversationMessage message = Assert.Single(result.Messages);
        Assert.Equal("진짜 사용자 메시지", message.Text);
    }
}
