using System.Collections.Generic;

namespace OrbDmgIndicator.Calculation;

/// <summary>
/// 적을 사망시키는 막타(killing blow) 데미지원 — 체력 숫자 색상을 결정한다.
/// 명세 §4①: 시간순 체인(구체 → 중독)에서 누가 마무리하느냐.
/// 종말(보라)은 이 모드가 판정하지 않고 vanilla 체력바 로직에 위임한다 → None일 때 게임 색상 유지.
/// </summary>
public enum KillSource
{
    None,
    Orb,    // 파랑 — 구체 단독 치사
    Poison, // 초록 — 중독이 마무리
}

/// <summary>
/// 툴팁 한 줄의 데미지 분류. enum 선언 순서 = 툴팁 표시 순서(발동 시간순).
/// </summary>
public enum DamageCategory
{
    Hailstorm,        // 우박폭풍
    LightningPassive, // 전기 구체(지속)
    GlassPassive,     // 유리 구체(지속)
    DarkEvoke,        // 암흑 구체(그림자 소모 발현)
    LightningEvoke,   // 전기 구체(발현, 벼락 합산)
    GlassEvoke,       // 유리 구체(발현)
    Poison,           // 중독(구체가 있을 때 보조 표기)
}

/// <summary>툴팁 한 줄 — 분류와 유효 피해 수치. 라벨·아이콘은 UI 계층이 매핑한다.</summary>
public readonly struct TooltipLine
{
    public DamageCategory Category { get; }
    public decimal Amount { get; }

    public TooltipLine(DamageCategory category, decimal amount)
    {
        Category = category;
        Amount = amount;
    }
}

/// <summary>적 한 명에 대한 계산 결과(체력바 색상·세그먼트·툴팁의 원천 데이터).</summary>
public sealed class EnemyResult
{
    public int EnemyId { get; }

    /// <summary>H — 현재 HP.</summary>
    public decimal CurrentHp { get; }

    /// <summary>O — 이 적에게 들어가는 총 유효 구체 피해(방어도 차감 후).</summary>
    public decimal OrbDamage { get; }

    /// <summary>P — 중독 피해(방어도 관통).</summary>
    public decimal PoisonDamage { get; }

    public KillSource KillSource { get; }

    /// <summary>
    /// 세그먼트 색칠 순서(좌→우): 남는 체력 → (종말, vanilla) → 중독(P) → 구체(O) → 이미 잃은 체력.
    /// 구체 세그먼트 폭. UI는 O/P와 KillSource로 막타 좌측 세그먼트를 숨긴다.
    /// </summary>
    public decimal OrbSegment => OrbDamage;
    public decimal PoisonSegment => PoisonDamage;

    /// <summary>툴팁 줄(유효 피해 > 0인 것만, 표시 순서대로). 종말은 제외.</summary>
    public IReadOnlyList<TooltipLine> TooltipLines { get; }

    /// <summary>툴팁 스코프(구체 + 중독)의 잔여 체력 = H − (O + P). ≤ 0이면 처치.</summary>
    public decimal RemainingHp { get; }

    /// <summary>툴팁을 표시할지 — 구체 계열 유효 피해가 최소 1줄 &gt; 0일 때만.</summary>
    public bool ShowTooltip { get; }

    public EnemyResult(
        int enemyId,
        decimal currentHp,
        decimal orbDamage,
        decimal poisonDamage,
        KillSource killSource,
        IReadOnlyList<TooltipLine> tooltipLines,
        decimal remainingHp,
        bool showTooltip)
    {
        EnemyId = enemyId;
        CurrentHp = currentHp;
        OrbDamage = orbDamage;
        PoisonDamage = poisonDamage;
        KillSource = killSource;
        TooltipLines = tooltipLines;
        RemainingHp = remainingHp;
        ShowTooltip = showTooltip;
    }
}
