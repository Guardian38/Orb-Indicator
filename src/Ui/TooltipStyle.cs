namespace OrbDmgIndicator.Ui;

/// <summary>
/// 호버 툴팁/UI 텍스트의 BBCode 스타일을 한 곳에 모은 "스타일 토큰" 집합.
///
/// RichTextLabel BBCode 자체엔 CSS class·cascading이 없지만, 시각 토큰(색·아이콘 크기)을 여기 상수로
/// 두고 헬퍼로만 텍스트를 조립하면 같은 효과를 낸다 — <b>한 곳을 바꾸면 모든 사용처에 일괄 반영</b>된다.
/// 새 UI 텍스트도 여기 헬퍼를 거쳐 만들면 게임과 일관된 스타일이 자동 유지된다.
///
/// 색 태그는 게임 RichTextTags(<c>[gold]</c>·<c>[blue]</c> 등, StsColors 기반)를 그대로 쓴다 —
/// 게임 hover tip의 MegaRichTextLabel이 동일 효과로 렌더하므로 게임 텍스트와 구별되지 않는다.
/// </summary>
internal static class TooltipStyle
{
    // ── 시각 토큰 (바꾸면 전체 cascade) ───────────────────────────────
    /// <summary>수치 색 — 게임 관례상 하늘색(StsColors.blue, 구체 세그먼트색과 동일).</summary>
    public const string NumberColor = "blue";

    /// <summary>인라인 아이콘 한 변 크기(px). 게임 sprite_fonts 인라인 아이콘 높이에 맞춘다(인게임 조정).</summary>
    public const int IconSize = 28;

    // ── 조립 헬퍼 ────────────────────────────────────────────────────
    // 피해원인 이름은 기본색으로 둔다(게임 본문 관례 — 제목만 금색, 본문 키워드는 무채색). 수치만 강조.

    /// <summary>수치를 하늘색으로.</summary>
    public static string Number(string text) => Color(NumberColor, text);

    /// <summary>임의 게임 색 태그로 감싼다.</summary>
    public static string Color(string color, string text) => $"[{color}]{text}[/{color}]";

    /// <summary>
    /// 인라인 아이콘. 풀사이즈 텍스처(orbs/powers PNG)를 줄 높이에 맞춰 <see cref="IconSize"/>로 축소한다.
    /// Godot BBCode width shorthand `[img=N]` 사용 — `[img=top ...]`처럼 valign을 앞에 두면 그게 width
    /// 자리를 먹어 크기 지정이 무시되므로 valign은 붙이지 않는다(게임의 `[img=top]`은 원본이 이미 작아 무의미).
    /// </summary>
    public static string Icon(string resPath) => $"[img={IconSize}]{resPath}[/img]";
}
