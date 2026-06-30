using System;

namespace OrbDmgIndicator.Calculation;

/// <summary>구체 피해 한 건의 대상 선정 방식. 값은 게임에서 읽은 라이브 수치(밀집·핵 등 반영).</summary>
internal enum SimTarget
{
    /// <summary>우박폭풍·유리(지속/발현) — 모든 생존 적.</summary>
    AllAlive,

    /// <summary>암흑 발현 — 현재 생존 중 최저 HP(방어도 무시 선정). 값은 결정론이나 <b>대상은 현재 상태에
    /// 의존</b>한다 — 직전 전기 타격이 HP를 바꾸면 최저 HP 적이 달라지므로 반드시 적용 시점에 재평가한다.</summary>
    LowestHp,

    /// <summary>전기(지속/발현) — 무작위 단일. 보수 패스/전멸 DFS/witness가 대상 정책을 달리한다.</summary>
    Lightning,
}

/// <summary>
/// 발동 순서대로 선형화한 구체 피해 한 건(우박/패시브/발현). 값·대상 방식·툴팁 분류를 담는다.
/// 피해 값과 대상 <i>방식</i>은 적 상태와 무관(밀집·핵 등은 라이브 값에 이미 반영)하므로 한 번만 만들고,
/// 실제 대상 <i>선정</i>(전체/최저HP/전기)·방어도·소모형·사망은 적용 시점에 처리한다.
/// </summary>
internal readonly struct DamageEvent
{
    public SimTarget Target { get; }
    public decimal Value { get; }
    public DamageCategory Category { get; }

    public DamageEvent(SimTarget target, decimal value, DamageCategory category)
    {
        Target = target;
        Value = value;
        Category = category;
    }
}

/// <summary>
/// 적 한 명의 가변 전투 상태 — 시뮬레이터의 EnemyWork와 전멸 DFS가 공유하는 경량 값 타입.
/// 구조체라 복제(분기)가 저렴하다. 카테고리별 피해 집계 같은 표시용 부기는 담지 않는다.
/// </summary>
internal struct DmgState
{
    public int Id;
    public decimal Hp;
    public decimal Block;

    /// <summary>단단한 껍질 잔여 HP 손실 상한(소모형). null = 미보유.</summary>
    public decimal? Shell;

    /// <summary>미끈거림 잔여 스택(소모형). 1 이상 HP 손실을 1로 캡하고 1씩 깎는다.</summary>
    public int Slip;

    /// <summary>Buffer 잔여 무효화 횟수(소모형).</summary>
    public int Buffer;

    public bool Alive;

    public static DmgState From(EnemySnapshot s) => new DmgState
    {
        Id = s.Id,
        Hp = s.Hp,
        Block = s.Block,
        Shell = s.HpLossCapThisTurn,
        Slip = s.SlipperyStacks,
        Buffer = s.BufferStacks,
        Alive = true,
    };
}

/// <summary>
/// 구체 피해 한 건의 핵심 파이프라인 — 게임 피해 처리 순서를 그대로 따르는 단일 출처.
/// EnemyWork 경로(<see cref="OrbDamageSimulator"/>)와 전멸 DFS(LightningWipeSolver)가 함께 호출해
/// 두 곳의 피해 계산이 갈라지지 않게 한다.
///
/// 순서: ① 방어도 이전 보정(Cap: 영체화·HardToKill) → ② 방어도 차감 → ③ HP 손실 보정(껍질→미끈거림→Buffer).
/// </summary>
internal static class DamageKernel
{
    /// <summary>
    /// <paramref name="s"/>에 raw 피해 한 건을 적용하고 실제 HP 손실(&gt; 0)을 반환한다.
    /// 방어도·껍질·미끈거림·Buffer에 전부 흡수되면 0을 반환하고, HP가 0 이하가 되면 <c>Alive=false</c>로 만든다.
    /// </summary>
    public static decimal Apply(ref DmgState s, OrbDamageSimulator.IDamageHook hook, decimal raw)
    {
        if (raw <= 0m || !s.Alive)
        {
            return 0m;
        }

        // ① 방어도 이전: 가산·승산·상한. 영체화는 여기서 타격을 1로 깎아 방어도 손실까지 제한한다.
        decimal dmg = hook.Cap(s.Id, raw);
        if (dmg <= 0m)
        {
            return 0m;
        }

        // ② 방어도 차감.
        decimal absorbed = Math.Min(s.Block, dmg);
        s.Block -= absorbed;
        decimal toHp = dmg - absorbed;
        if (toHp <= 0m)
        {
            return 0m; // 방어도에 완전 흡수.
        }

        // ③ HP 손실 보정 — 소모형(시뮬레이터가 잔여 예산을 직접 추적해 다중 타격에 정확).
        // 게임 순서: 단단한 껍질(BeforeOstyLate) → 미끈거림(AfterOsty) → Buffer(AfterOstyLate).
        if (s.Shell.HasValue)
        {
            decimal applied = Math.Min(toHp, s.Shell.Value);
            s.Shell = s.Shell.Value - applied;
            toHp = applied;
            if (toHp <= 0m)
            {
                return 0m; // 껍질 전부 흡수 → 미끈거림·Buffer 미소모(B-1과 동일 근사).
            }
        }
        if (s.Slip > 0 && toHp >= 1m)
        {
            toHp = 1m;
            s.Slip--;
        }
        if (s.Buffer > 0)
        {
            s.Buffer--;
            return 0m; // 이 타격의 HP 손실을 통째로 무효.
        }

        s.Hp -= toHp;
        if (s.Hp <= 0m)
        {
            s.Alive = false;
        }
        return toHp;
    }
}
