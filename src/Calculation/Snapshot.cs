using System.Collections.Generic;

namespace OrbDmgIndicator.Calculation;

/// <summary>
/// 구체(Orb) 종류. 피해가 없는 냉기(Frost)/플라즈마(Plasma)도
/// 슬롯 순서와 우박폭풍 트리거 판정을 위해 포함한다.
/// </summary>
public enum OrbType
{
    Lightning,
    Glass,
    Dark,
    Frost,
    Plasma,
}

/// <summary>
/// 구체 큐의 한 칸(slot). 모든 수치는 게임에서 라이브로 읽은 결과로,
/// 밀집(Focus)·충전된 핵(InfusedCore) 등 ModifyOrbValue 보정이 이미 포함되어 있다.
/// </summary>
public sealed class OrbSnapshot
{
    public OrbType Type { get; }

    /// <summary>지속(Passive) 값. 전기/유리에서 의미가 있다. 암흑은 매 턴 누적량이다.</summary>
    public decimal PassiveVal { get; }

    /// <summary>
    /// 발현(Evoke) 값. 암흑(DarkOrb)은 현재까지 누적된 _evokeVal
    /// (이번 턴 패시브 누적이 더해지기 전 값)이다. 시뮬레이터가 패시브 단계에서 더한다.
    /// </summary>
    public decimal EvokeVal { get; }

    /// <summary>
    /// 패시브 발동 횟수(기본 1). 판금 케이블(GoldPlatedCables)은 첫 슬롯 구체를 2로 만들고,
    /// 그 밖의 다중 발동 효과도 포함한다. 수집 계층이 구체별로
    /// Hook.ModifyOrbPassiveTriggerCount를 조회해 채운다. 발현(Evoke)에는 적용되지 않는다.
    /// </summary>
    public int PassiveTriggerCount { get; }

    public OrbSnapshot(OrbType type, decimal passiveVal, decimal evokeVal, int passiveTriggerCount = 1)
    {
        Type = type;
        PassiveVal = passiveVal;
        EvokeVal = evokeVal;
        PassiveTriggerCount = passiveTriggerCount < 1 ? 1 : passiveTriggerCount;
    }
}

/// <summary>
/// 플레이어 한 명의 구체 관련 상태. CombatSnapshot 안에 좌석 순서(Allies 인덱스)대로 담긴다.
/// 구체는 Defect 전용이 아니라 이벤트·생성형 카드로 만들어진 Defect 카드를 통해
/// 어떤 캐릭터든 영창할 수 있으므로, Defect로 한정하지 않고 전 플레이어를 담는다.
/// 구체 큐가 빈 플레이어는 시뮬레이션에 아무것도 기여하지 않는다.
/// </summary>
public sealed class PlayerSnapshot
{
    /// <summary>구체 큐. 슬롯 순서(좌→우 = index 0..n-1).</summary>
    public IReadOnlyList<OrbSnapshot> Orbs { get; }

    /// <summary>우박폭풍(Hailstorm) 라이브 Amount. 파워 미보유 시 null.</summary>
    public decimal? HailstormAmount { get; }

    /// <summary>그림자 소모(Consuming Shadow) 중첩수 = 발현시킬 마지막 슬롯 개수. 미보유 시 0.</summary>
    public int ConsumingShadowStacks { get; }

    /// <summary>벼락(Thunder) Amount. 전기 발현 시 같은 대상에 추가 피해. 미보유 시 null.</summary>
    public decimal? ThunderAmount { get; }

    public PlayerSnapshot(
        IReadOnlyList<OrbSnapshot> orbs,
        decimal? hailstormAmount,
        int consumingShadowStacks,
        decimal? thunderAmount)
    {
        Orbs = orbs;
        HailstormAmount = hailstormAmount;
        ConsumingShadowStacks = consumingShadowStacks;
        ThunderAmount = thunderAmount;
    }

    /// <summary>우박폭풍 발동 조건: 파워 보유 ∧ 냉기 구체 ≥ 1.</summary>
    public bool HailstormTriggers
    {
        get
        {
            if (HailstormAmount is null)
            {
                return false;
            }
            foreach (OrbSnapshot orb in Orbs)
            {
                if (orb.Type == OrbType.Frost)
                {
                    return true;
                }
            }
            return false;
        }
    }
}

/// <summary>적 한 명의 상태.</summary>
public sealed class EnemySnapshot
{
    /// <summary>적 식별자(게임 Creature의 안정적 id). 결과 매핑용.</summary>
    public int Id { get; }

    public decimal Hp { get; }
    public decimal Block { get; }

    /// <summary>다음 턴 중독 총 피해(PoisonPower.CalculateTotalDamageNextTurn). 방어도 관통.</summary>
    public decimal Poison { get; }

    /// <summary>
    /// 중독 파워의 현재 스택(= `PoisonPower.Amount`). 미보유 시 0. 미끈거림 인스턴스별 캡 계산용 —
    /// 중독은 매 발동이 별도 HP 손실 인스턴스라, i번째 발동 피해 = `Amount − i`(게임 `AfterSideTurnStart`).
    /// </summary>
    public int PoisonAmount { get; }

    /// <summary>
    /// 중독 발동 횟수(= `min(Amount, 1 + Σ상대 촉매제)`, 게임 `PoisonPower.TriggerCount`). 미보유 시 0.
    /// 촉매제(Accelerant)가 없으면 1(턴당 1회). 미끈거림 캡을 인스턴스 단위로 적용할 때 인스턴스 수.
    /// </summary>
    public int PoisonTriggerCount { get; }

    // 종말(Doom)은 의도적으로 수집하지 않는다 — 종말 치사(보라)는 vanilla 체력바 로직에 위임하고,
    // 이 모드는 구체+중독 막타(파랑/초록)만 판정한다(자원 절약). 상세 source_analysis.md.

    /// <summary>
    /// 단단한 껍질(HardenedShell)의 이번 턴(아군) **잔여** HP 손실 상한(= `DisplayAmount`, Amount−이미받은피해).
    /// 미보유 시 null. 소모형이라 시뮬레이터가 타격마다 깎는다(다중 타격 정확성). 방어도 이후 적용.
    /// 구체 피해(아군 턴 종료)에만 적용 — 중독은 적 턴이라 별도 예산(<see cref="PoisonHpLossCap"/>).
    /// </summary>
    public decimal? HpLossCapThisTurn { get; }

    /// <summary>
    /// 단단한 껍질(HardenedShell)의 **전체 턴 상한**(= `Amount`). 미보유 시 null.
    /// 껍질 예산은 매 턴 시작(`BeforeSideTurnStart`)에 리셋되고, 중독은 적 턴 시작
    /// (`AfterSideTurnStart`)에 그 새 예산으로 상한된다. 따라서 중독 총피해 ≤ Amount이며,
    /// 아군 턴 구체 소모(<see cref="HpLossCapThisTurn"/>)와 독립이다 (중독 표기 = min(Poison, 이 값)).
    /// </summary>
    public decimal? PoisonHpLossCap { get; }

    /// <summary>
    /// Buffer 잔여 횟수(다음 N회 HP 손실 무효). 미보유 시 0. 소모형이라 시뮬레이터가 무효화마다 1씩 깎는다.
    /// </summary>
    public int BufferStacks { get; }

    /// <summary>
    /// 미끈거림(Slippery) 잔여 스택. 1 이상 HP 손실 인스턴스를 1로 캡하고 1 소모하는 소모형
    /// (Buffer 형제, 단 0이 아니라 1로). 미보유 시 0. 게임 페이즈 순서: 단단한 껍질 → 미끈거림 → Buffer.
    /// 시뮬레이터가 구체 타격마다 직접 추적·소모한다(다중 타격 정확성). 중독 캡은 미구현(후속).
    /// </summary>
    public int SlipperyStacks { get; }

    public EnemySnapshot(
        int id,
        decimal hp,
        decimal block,
        decimal poison,
        decimal? hpLossCapThisTurn = null,
        int bufferStacks = 0,
        decimal? poisonHpLossCap = null,
        int slipperyStacks = 0,
        int poisonAmount = 0,
        int poisonTriggerCount = 0)
    {
        Id = id;
        Hp = hp;
        Block = block;
        Poison = poison;
        HpLossCapThisTurn = hpLossCapThisTurn;
        BufferStacks = bufferStacks < 0 ? 0 : bufferStacks;
        PoisonHpLossCap = poisonHpLossCap;
        SlipperyStacks = slipperyStacks < 0 ? 0 : slipperyStacks;
        PoisonAmount = poisonAmount < 0 ? 0 : poisonAmount;
        PoisonTriggerCount = poisonTriggerCount < 0 ? 0 : poisonTriggerCount;
    }
}

/// <summary>전투 상태 스냅샷 — 시뮬레이션의 입력.</summary>
public sealed class CombatSnapshot
{
    /// <summary>
    /// 좌석 순서(combatState.Allies 인덱스)로 정렬된, 구체를 보유한 플레이어들.
    /// 구체 큐가 빈 플레이어는 수집 계층에서 제외되므로 여기엔 기여자만 담긴다
    /// (상대 좌석 순서는 보존됨).
    /// </summary>
    public IReadOnlyList<PlayerSnapshot> Players { get; }

    public IReadOnlyList<EnemySnapshot> Enemies { get; }

    public CombatSnapshot(IReadOnlyList<PlayerSnapshot> players, IReadOnlyList<EnemySnapshot> enemies)
    {
        Players = players;
        Enemies = enemies;
    }
}
