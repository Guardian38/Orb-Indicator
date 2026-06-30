using System;
using System.Collections.Generic;
using System.Linq;

namespace OrbDmgIndicator.Calculation;

/// <summary>
/// 턴 종료 해소 순서를 그대로 시뮬레이션해 적별 결과를 산출한다(명세 §3·§4).
///
/// 순수 로직 — 게임 타입에 의존하지 않는다. 입력 <see cref="CombatSnapshot"/>은
/// 수집 계층이 라이브로 채우며, 출력은 UI 계층이 색상·세그먼트·툴팁으로 렌더한다.
///
/// 발동 순서를 한 번 <see cref="DamageEvent"/> 시퀀스로 선형화한 뒤 순서대로 적용한다. 피해 적용
/// 파이프라인·경량 상태·이벤트 타입은 OrbSimulationCore(<see cref="DamageKernel"/>·<see cref="DmgState"/>·
/// <see cref="DamageEvent"/>)로 분리해 공유한다. 전기(무작위 단일)는 발동 시점 생존 적이 정확히 1명일
/// 때만 반영한다(보수). 다수 적 전기 "전멸 확정" 표기는 후속.
/// </summary>
public static class OrbDamageSimulator
{
    /// <summary>
    /// 한 번의 구체 타격에 대한 **방어도 이전·비소모형** 피해 보정(가산·승산·상한). 시뮬레이터는 순수
    /// 로직(시퀀스·대상·사망·방어도 소모)을 담당하고, 비소모형 상한(영체화→1, HardToKill→Amount)은 이 훅에
    /// 위임한다. 라이브는 게임 `Hook.ModifyDamage(...All)`을 호출, 단위 테스트는 항등 구현을 주입한다.
    ///
    /// **소모형** 보정(단단한 껍질의 턴 누적 상한·Buffer의 무효화)은 타격이 진행되며 깎이는 상태라 게임
    /// 훅의 라이브 값으론 다중 타격이 부정확하다. 그래서 이 훅이 아니라 <see cref="EnemySnapshot"/>
    /// (HpLossCapThisTurn·BufferStacks)로 받아 시뮬레이터가 직접 추적·소모한다(방어도 이후).
    /// </summary>
    public interface IDamageHook
    {
        /// <summary>방어도 이전: 가산·승산·상한(Cap). 영체화→1, HardToKill→Amount. (비소모형)</summary>
        decimal Cap(int enemyId, decimal raw);
    }

    /// <summary>보정 없음 — 순수 방어도 차감만. 단위 테스트·게임 비의존 경로용 기본값.</summary>
    private sealed class IdentityHook : IDamageHook
    {
        public static readonly IdentityHook Instance = new IdentityHook();
        public decimal Cap(int enemyId, decimal raw) => raw;
    }

    /// <summary>얇은 진입점 — 시뮬레이션 1회를 <see cref="Run"/> 컨텍스트로 실행한다.</summary>
    public static IReadOnlyDictionary<int, EnemyResult> Simulate(CombatSnapshot snapshot, IDamageHook? damageHook = null)
    {
        return new Run(snapshot, damageHook ?? IdentityHook.Instance).Execute();
    }

    /// <summary>
    /// 시뮬레이션 1회의 실행 컨텍스트. 플레이어·피해 훅을 보관하고, 발동 순서를 이벤트 시퀀스로 선형화해
    /// 순서대로 적용한다.
    /// </summary>
    private sealed class Run
    {
        private readonly IDamageHook _hook;
        private readonly IReadOnlyList<EnemySnapshot> _enemySnaps;
        private readonly List<PlayerWork> _players;

        public Run(CombatSnapshot snapshot, IDamageHook hook)
        {
            _hook = hook;
            _enemySnaps = snapshot.Enemies;
            _players = snapshot.Players.Select(p => new PlayerWork(p)).ToList();
        }

        public IReadOnlyDictionary<int, EnemyResult> Execute()
        {
            List<DamageEvent> events = BuildEvents();

            // 전기는 발동 시점 생존 적이 정확히 1명일 때만 그 적에게 적용(보수). 다수 적 전멸 확정은 후속.
            List<EnemyWork> pass = RunLinear(events, alive => alive.Count == 1 ? alive[0] : null);

            var results = new Dictionary<int, EnemyResult>(pass.Count);
            foreach (EnemyWork e in pass)
            {
                results[e.Source.Id] = BuildResult(e);
            }
            return results;
        }

        // ── 이벤트 시퀀스 빌드 (명세 §3 Phase 1~3을 슬롯/좌석 순서대로 선형화) ──
        // 피해 값·대상 방식은 적 상태와 무관(밀집·핵 등은 라이브 값에 반영)하므로 한 번 만든다.
        private List<DamageEvent> BuildEvents()
        {
            var events = new List<DamageEvent>();

            // Phase 1 · 우박폭풍 (전원 좌석순, 모든 alive에 1회)
            foreach (PlayerWork p in _players)
            {
                if (p.Source.HailstormTriggers)
                {
                    events.Add(new DamageEvent(SimTarget.AllAlive, p.Source.HailstormAmount!.Value, DamageCategory.Hailstorm));
                }
            }

            // Phase 2 · 구체 패시브 (전원 좌석순, 슬롯 좌→우, 발동 횟수만큼 반복)
            // 타입별 묶음이 아니라 슬롯 순서대로 인터리브해야 방어도 소모·사망 순서가 정확하다.
            foreach (PlayerWork p in _players)
            {
                foreach (OrbWork orb in p.Orbs)
                {
                    for (int t = 0; t < orb.PassiveTriggerCount; t++)
                    {
                        switch (orb.Type)
                        {
                            case OrbType.Glass:
                            {
                                // 발동마다 값 1 감소(게임 _passiveVal -= 1, 최소 0). t회차 = PassiveVal − t.
                                decimal glassVal = orb.PassiveVal - t;
                                if (glassVal > 0m)
                                {
                                    events.Add(new DamageEvent(SimTarget.AllAlive, glassVal, DamageCategory.GlassPassive));
                                }
                                break;
                            }

                            case OrbType.Lightning:
                                if (orb.PassiveVal > 0m)
                                {
                                    events.Add(new DamageEvent(SimTarget.Lightning, orb.PassiveVal, DamageCategory.LightningPassive));
                                }
                                break;

                            case OrbType.Dark:
                                // 피해 없음 — 발현용 누적값만 갱신(매 발동): _evokeVal += PassiveVal.
                                orb.RunningEvoke += orb.PassiveVal;
                                break;

                            // Frost / Plasma: 피해 없음.
                        }
                    }
                }
            }

            // Phase 3 · 그림자 소모 발현 (전원 좌석순, 마지막 슬롯부터 stacks개)
            foreach (PlayerWork p in _players)
            {
                int stacks = p.Source.ConsumingShadowStacks;
                for (int i = 0; i < stacks && p.Remaining.Count > 0; i++)
                {
                    OrbWork orb = p.Remaining[p.Remaining.Count - 1];
                    p.Remaining.RemoveAt(p.Remaining.Count - 1);

                    switch (orb.Type)
                    {
                        case OrbType.Glass:
                            events.Add(new DamageEvent(SimTarget.AllAlive, orb.EvokeVal, DamageCategory.GlassEvoke));
                            break;

                        case OrbType.Lightning:
                        {
                            // 벼락(Thunder)은 별도 줄 없이 전기 발현에 합산(같은 대상, 방어도 가능).
                            decimal value = orb.EvokeVal + (p.Source.ThunderAmount ?? 0m);
                            if (value > 0m)
                            {
                                events.Add(new DamageEvent(SimTarget.Lightning, value, DamageCategory.LightningEvoke));
                            }
                            break;
                        }

                        case OrbType.Dark:
                            // 값은 누적 _evokeVal로 결정론이나, 대상(최저 HP)은 적용 시점 상태에 의존한다
                            // (직전 전기 타격이 최저 HP 적을 바꿀 수 있음).
                            events.Add(new DamageEvent(SimTarget.LowestHp, orb.RunningEvoke, DamageCategory.DarkEvoke));
                            break;

                        // Frost / Plasma: 발현 피해 없음.
                    }
                }
            }

            return events;
        }

        // ── 선형 패스 — 이벤트를 순서대로 적용, 전기 대상은 정책(델리게이트)으로 위임 ──
        private List<EnemyWork> RunLinear(List<DamageEvent> events, Func<List<EnemyWork>, EnemyWork?> lightning)
        {
            var enemies = _enemySnaps.Select(s => new EnemyWork(s)).ToList();
            var alive = new List<EnemyWork>(enemies);

            foreach (DamageEvent ev in events)
            {
                switch (ev.Target)
                {
                    case SimTarget.AllAlive:
                        foreach (EnemyWork e in alive.ToList())
                        {
                            ApplyOrbDmg(e, ev.Value, ev.Category, alive);
                        }
                        break;

                    case SimTarget.LowestHp:
                        if (alive.Count > 0)
                        {
                            // 적용 시점의 최저 HP(직전 전기 반영). 동률은 가장 이른 슬롯(OrderBy 안정 정렬).
                            EnemyWork target = alive.OrderBy(e => e.State.Hp).First();
                            ApplyOrbDmg(target, ev.Value, ev.Category, alive);
                        }
                        break;

                    case SimTarget.Lightning:
                        if (alive.Count == 0)
                        {
                            break; // 대상 없음.
                        }
                        EnemyWork? hit = lightning(alive);
                        if (hit != null)
                        {
                            ApplyOrbDmg(hit, ev.Value, ev.Category, alive);
                        }
                        break;
                }
            }

            return enemies;
        }

        /// <summary>
        /// 구체 계열 1회 피해 적용(EnemyWork 경로) — <see cref="DamageKernel"/>로 게임 파이프라인을 통과시키고
        /// 유효 피해를 카테고리·총합에 기록한다. 사망 시 생존 목록에서 제거한다.
        /// </summary>
        private void ApplyOrbDmg(EnemyWork e, decimal raw, DamageCategory category, List<EnemyWork> alive)
        {
            decimal toHp = DamageKernel.Apply(ref e.State, _hook, raw);
            if (toHp <= 0m)
            {
                return;
            }
            e.OrbDamage += toHp;
            e.AddCategoryDamage(category, toHp);
            if (!e.State.Alive)
            {
                alive.Remove(e);
            }
        }

        // ── 출력 ──

        private static EnemyResult BuildResult(EnemyWork e)
        {
            decimal o = e.OrbDamage;
            decimal h = e.Source.Hp;

            // 중독은 적 턴 시작에 발동 — 단단한 껍질 예산이 그 직전 리셋되므로(BeforeSideTurnStart),
            // 아군 턴 구체 소모와 독립인 새 전체 예산(Amount)으로 상한된다. 껍질의 턴 누적 상한
            // 특성상 다중 발동(촉매제)이어도 총 HP 손실 = min(총 중독, Amount).
            decimal p = ComputePoison(e);

            // §4① 막타 기준 색상 — 시간순 누적 임계. 종말(보라)은 판정하지 않고 vanilla에 위임한다.
            KillSource kill;
            if (o >= h)
            {
                kill = KillSource.Orb;
            }
            else if (h <= o + p)
            {
                kill = KillSource.Poison;
            }
            else
            {
                kill = KillSource.None;
            }

            // §4③ 툴팁 줄 — 구체 카테고리(유효 > 0) + 중독(구체가 있고 적이 살아남을 때 보조).
            var lines = new List<TooltipLine>();
            bool hasOrbLine = false;
            foreach (DamageCategory cat in OrbCategoryOrder)
            {
                decimal amount = e.CategoryDamage(cat);
                if (amount > 0m)
                {
                    lines.Add(new TooltipLine(cat, amount));
                    hasOrbLine = true;
                }
            }
            // 중독 줄: 구체 줄이 있고, 구체만으로는 안 죽어(O < H) 실제 중독을 받을 때만.
            if (hasOrbLine && p > 0m && o < h)
            {
                lines.Add(new TooltipLine(DamageCategory.Poison, p));
            }

            decimal remaining = h - (o + p);

            return new EnemyResult(
                e.Source.Id, h, o, p, kill, lines, remaining, hasOrbLine);
        }

        /// <summary>
        /// 적 턴 시작 중독의 유효 HP 손실. 미끈거림(Slippery) 미보유면 단순 상한(min(총 중독, 껍질 전체 예산)).
        /// 미끈거림 보유 시: 중독은 매 발동이 별도 HP 손실 인스턴스라 인스턴스마다 1로 캡한다.
        /// i번째 발동 = `PoisonAmount − i`, 발동 횟수 = PoisonTriggerCount. 각 인스턴스에 단단한 껍질(턴 전체
        /// 예산, 소모형) → 미끈거림(1로 캡, 스택 1 소모) 순. 미끈거림 스택은 구체 페이즈가 먼저 소비한 잔여를 쓴다.
        /// </summary>
        private static decimal ComputePoison(EnemyWork e)
        {
            if (e.State.Slip <= 0 || e.Source.PoisonTriggerCount <= 0)
            {
                decimal total = e.Source.Poison;
                if (e.Source.PoisonHpLossCap.HasValue)
                {
                    total = Math.Min(total, e.Source.PoisonHpLossCap.Value);
                }
                return total;
            }

            decimal shellBudget = e.Source.PoisonHpLossCap ?? decimal.MaxValue;
            int slip = e.State.Slip;
            decimal sum = 0m;
            for (int i = 0; i < e.Source.PoisonTriggerCount; i++)
            {
                decimal inst = e.Source.PoisonAmount - i; // 게임: i번째 발동 피해 = Amount − i

                decimal applied = Math.Min(inst, shellBudget);
                shellBudget -= applied;
                inst = applied;
                if (inst <= 0m)
                {
                    continue; // 껍질 전부 흡수 → 미끈거림 미소모(근사).
                }

                if (slip > 0 && inst >= 1m)
                {
                    inst = 1m;
                    slip--;
                }
                sum += inst;
            }
            return sum;
        }

        /// <summary>툴팁에 나오는 구체 카테고리(발동 시간순). 중독은 별도로 뒤에 붙인다.</summary>
        private static readonly DamageCategory[] OrbCategoryOrder =
        {
            DamageCategory.Hailstorm,
            DamageCategory.LightningPassive,
            DamageCategory.GlassPassive,
            DamageCategory.DarkEvoke,
            DamageCategory.LightningEvoke,
            DamageCategory.GlassEvoke,
        };
    }

    // ── 내부 작업 구조 ──

    private sealed class EnemyWork
    {
        public EnemySnapshot Source { get; }
        public DmgState State;
        public decimal OrbDamage;

        private readonly Dictionary<DamageCategory, decimal> _categoryDamage = new();

        public EnemyWork(EnemySnapshot src)
        {
            Source = src;
            State = DmgState.From(src);
        }

        public void AddCategoryDamage(DamageCategory cat, decimal amount)
        {
            _categoryDamage[cat] = (_categoryDamage.TryGetValue(cat, out decimal v) ? v : 0m) + amount;
        }

        public decimal CategoryDamage(DamageCategory cat)
        {
            return _categoryDamage.TryGetValue(cat, out decimal v) ? v : 0m;
        }
    }

    private sealed class PlayerWork
    {
        public PlayerSnapshot Source { get; }
        public List<OrbWork> Orbs { get; }

        /// <summary>발현(그림자 소모)으로 소모되는 큐. Orbs와 같은 순서로 시작.</summary>
        public List<OrbWork> Remaining { get; }

        public PlayerWork(PlayerSnapshot src)
        {
            Source = src;
            Orbs = src.Orbs.Select(o => new OrbWork(o)).ToList();
            Remaining = new List<OrbWork>(Orbs);
        }
    }

    private sealed class OrbWork
    {
        public OrbType Type { get; }
        public decimal PassiveVal { get; }
        public decimal EvokeVal { get; }
        public int PassiveTriggerCount { get; }

        /// <summary>암흑 발현값 — 이벤트 빌드 중 패시브 누적(_evokeVal += PassiveVal)을 반영.</summary>
        public decimal RunningEvoke;

        public OrbWork(OrbSnapshot o)
        {
            Type = o.Type;
            PassiveVal = o.PassiveVal;
            EvokeVal = o.EvokeVal;
            PassiveTriggerCount = o.PassiveTriggerCount;
            RunningEvoke = o.EvokeVal;
        }
    }
}
