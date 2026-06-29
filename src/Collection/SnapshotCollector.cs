using System.Collections.Generic;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using OrbDmgIndicator.Calculation;

namespace OrbDmgIndicator.Collection;

/// <summary>
/// 라이브 게임 상태(ICombatState) → 순수 입력(CombatSnapshot) 변환.
///
/// 명세 근거(source_analysis.md):
///  - 플레이어는 좌석 순서(combatState.Players)로 순회하고, 구체 큐가 빈 플레이어는 제외한다
///    (구체는 Defect 전용이 아니라 어떤 플레이어든 보유 가능).
///  - 구체 값은 라이브 PassiveVal/EvokeVal을 읽으면 밀집·충전된 핵 등 ModifyOrbValue 보정이 이미 반영됨.
///  - 우박폭풍/벼락/그림자 소모는 해당 플레이어 Creature의 파워에서 읽는다.
///  - 중독은 반드시 PoisonPower.CalculateTotalDamageNextTurn()(촉매제 증폭 포함)을 호출한다.
///
/// 발동 횟수:
///  - PassiveTriggerCount: 판금 케이블(GoldPlatedCables) 등 다중 발동을 구체별로
///    Hook.ModifyOrbPassiveTriggerCount로 조회해 채운다. 읽기 전용으로만 쓰며, 짝을 이루는
///    AfterModifyingOrbPassiveTriggerCount(유물 Flash 등 부작용)는 호출하지 않는다.
///
/// 종말(Doom)은 의도적으로 조회하지 않는다 — 종말 치사(보라)는 vanilla 체력바 로직에 위임하고,
/// 이 모드는 구체+중독 막타(파랑/초록)만 판정한다(자원 절약). 상세 source_analysis.md.
/// </summary>
public static class SnapshotCollector
{
    public static CombatSnapshot? TryCollect(ICombatState? combat)
    {
        if (combat == null || !combat.IsLiveCombat())
        {
            return null;
        }

        // ── 플레이어(좌석순, 구체 보유자만) ──
        var players = new List<PlayerSnapshot>();
        foreach (Player player in combat.Players)
        {
            PlayerCombatState? pcs = player.PlayerCombatState;
            if (pcs == null)
            {
                continue;
            }

            IReadOnlyList<OrbModel> orbs = pcs.OrbQueue.Orbs;
            if (orbs.Count == 0)
            {
                continue; // 빈 큐는 패시브·우박·발현·벼락 전부 발동 불가 → 제외.
            }

            var orbSnaps = new List<OrbSnapshot>(orbs.Count);
            foreach (OrbModel orb in orbs)
            {
                OrbType? type = MapOrbType(orb);
                if (type == null)
                {
                    continue; // 알 수 없는 구체(방어적 무시).
                }
                // 판금 케이블 등 다중 발동을 게임 훅으로 조회(읽기 전용 — modifiers 무시, After 훅 미호출).
                int triggerCount = Hook.ModifyOrbPassiveTriggerCount(combat, orb, 1, out _);
                orbSnaps.Add(new OrbSnapshot(type.Value, orb.PassiveVal, orb.EvokeVal, triggerCount));
            }

            Creature c = player.Creature;
            decimal? hailstorm = c.HasPower<HailstormPower>() ? c.GetPowerAmount<HailstormPower>() : null;
            int consumingShadow = c.GetPowerAmount<ConsumingShadowPower>();
            decimal? thunder = c.HasPower<ThunderPower>() ? c.GetPowerAmount<ThunderPower>() : null;

            players.Add(new PlayerSnapshot(orbSnaps, hailstorm, consumingShadow, thunder));
        }

        // ── 적 ──
        var enemies = new List<EnemySnapshot>();
        foreach (Creature e in combat.Enemies)
        {
            if (!e.IsAlive || e.CombatId == null)
            {
                continue;
            }
            PoisonPower? poisonPower = e.GetPower<PoisonPower>();
            int poison = poisonPower?.CalculateTotalDamageNextTurn() ?? 0;
            // 미끈거림 인스턴스별 캡용: 중독 스택(Amount)과 발동 횟수(TriggerCount=min(Amount, 1+상대 촉매제)).
            // 게임 PoisonPower.TriggerCount가 private이라 동일 식으로 재계산(상대 = 적의 상대편 = 플레이어들).
            int poisonAmount = poisonPower?.Amount ?? 0;
            int poisonTriggerCount = 0;
            if (poisonAmount > 0)
            {
                int accelerant = 0;
                foreach (Player ally in combat.Players)
                {
                    Creature ac = ally.Creature;
                    if (ac.IsAlive)
                    {
                        accelerant += ac.GetPowerAmount<AccelerantPower>();
                    }
                }
                int triggers = 1 + accelerant;
                poisonTriggerCount = triggers < poisonAmount ? triggers : poisonAmount;
            }
            // 소모형 HP 손실 보정(시뮬레이터가 다중 타격에 정확히 깎는다).
            HardenedShellPower? shell = e.GetPower<HardenedShellPower>();
            decimal? hpLossCap = shell?.DisplayAmount; // 아군 턴 잔여 누적 상한 — 구체 피해용
            decimal? poisonHpLossCap = shell?.Amount;  // 적 턴 전체 상한(매 턴 리셋) — 중독 피해용
            int bufferStacks = e.GetPowerAmount<BufferPower>();
            // 미끈거림(SlipperyPower): 1 이상 HP 손실을 1로 캡하는 소모형. 실제 적(Inklet·Vantom) 보유.
            int slipperyStacks = e.GetPowerAmount<SlipperyPower>();
            enemies.Add(new EnemySnapshot(
                id: (int)e.CombatId.Value,
                hp: e.CurrentHp,
                block: e.Block,
                poison: poison,
                hpLossCapThisTurn: hpLossCap,
                bufferStacks: bufferStacks,
                poisonHpLossCap: poisonHpLossCap,
                slipperyStacks: slipperyStacks,
                poisonAmount: poisonAmount,
                poisonTriggerCount: poisonTriggerCount));
        }

        return new CombatSnapshot(players, enemies);
    }

    private static OrbType? MapOrbType(OrbModel orb)
    {
        return orb switch
        {
            LightningOrb => OrbType.Lightning,
            GlassOrb => OrbType.Glass,
            DarkOrb => OrbType.Dark,
            FrostOrb => OrbType.Frost,
            PlasmaOrb => OrbType.Plasma,
            _ => null,
        };
    }
}
