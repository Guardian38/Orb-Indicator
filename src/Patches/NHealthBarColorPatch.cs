using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.addons.mega_text;
using OrbDmgIndicator.Calculation;

namespace OrbDmgIndicator.Patches;

/// <summary>
/// 적 체력바의 HP 숫자 색상을 막타원(killing blow)에 따라 덮어쓴다.
///
/// 명세(§4① · PROJECT.md "체력 숫자 색상"):
///   구체 단독 치사 → 파랑(StsColors.blue), 구체+중독 치사(중독이 마무리) → 초록.
///   둘 다 아니면(KillSource.None) vanilla 색상 유지 → 종말 보라/중독 초록/방어도 등은 게임이 판정.
///
/// vanilla RefreshText는 IsPoisonLethal(초록)·IsDoomLethal(보라)만 알고 구체 피해(O)는 모른다.
/// 그래서 Postfix로 우리 결과를 시간순 체인 맨 앞에 끼운다:
///   - Orb  : 항상 파랑으로 덮어씀(구체가 가장 먼저 발동해 단독 치사 = 최우선 경고).
///   - Poison: 초록. vanilla는 P≥H일 때만 초록이지만 우리 기준은 O+P≥H(구체 보조)라 더 넓다 → 덮어씀.
///   - None : 기본은 vanilla 유지. 단 종말 치사는 vanilla가 O를 몰라 놓치므로 보완한다 — vanilla
///            IsDoomLethal은 `종말 ≥ H−P`만 보지만, 구체가 먼저 HP를 깎으므로 실제 종말 시점 HP는 H−O−P다.
///            구체+중독으론 안 죽고(None) `종말 ≥ H−O−P`면 보라로 덮어쓴다(vanilla와 같은 보라색).
///
/// RefreshText는 자주 호출되므로 계산은 PredictionService가 프레임 단위로 캐시한다.
/// </summary>
[HarmonyPatch(typeof(NHealthBar), "RefreshText")]
public static class NHealthBarColorPatch
{
    private static readonly AccessTools.FieldRef<NHealthBar, Creature> _creatureRef =
        AccessTools.FieldRefAccess<NHealthBar, Creature>("_creature");

    private static readonly AccessTools.FieldRef<NHealthBar, MegaLabel> _hpLabelRef =
        AccessTools.FieldRefAccess<NHealthBar, MegaLabel>("_hpLabel");

    // 구체 단독 치사(파랑) — 디펙트 상징색. 외곽선은 어두운 남색.
    private static readonly Color _orbFontColor = StsColors.blue; // #87CEEB
    private static readonly Color _orbOutlineColor = new Color("12354F");

    // 중독 마무리(초록) — vanilla 중독 치사 색과 동일하게 맞춘다.
    private static readonly Color _poisonFontColor = new Color("76FF40");
    private static readonly Color _poisonOutlineColor = new Color("074700");

    // 종말 마무리(보라) — vanilla IsDoomLethal 색과 동일.
    private static readonly Color _doomFontColor = new Color("FB8DFF");
    private static readonly Color _doomOutlineColor = new Color("2D1263");

    private static bool _loggedError;

    private static void Postfix(NHealthBar __instance)
    {
        // 색상 덮어쓰기 오류가 게임 체력바를 깨지 않도록 삼킨다(최초 1회만 로깅).
        try
        {
            Apply(__instance);
        }
        catch (System.Exception ex)
        {
            if (!_loggedError)
            {
                _loggedError = true;
                Log.Error($"[orb_dmg_indicator] health bar color failed: {ex}");
            }
        }
    }

    private static void Apply(NHealthBar __instance)
    {
        Creature creature = _creatureRef(__instance);
        if (creature == null || !creature.IsEnemy || creature.CombatId == null)
        {
            return;
        }

        // vanilla가 숫자를 그리는 경우에만 개입(사망·무한 체력·숫자 미표시는 건드리지 않음).
        if (creature.CurrentHp <= 0 || !creature.HpDisplay.ShowsNumbers() || creature.HpDisplay.IsInfinite())
        {
            return;
        }

        EnemyResult? result = PredictionService.TryGet(creature);
        if (result == null)
        {
            return;
        }

        Color font;
        Color outline;
        if (result.KillSource == KillSource.Orb)
        {
            font = _orbFontColor;
            outline = _orbOutlineColor;
        }
        else if (result.KillSource == KillSource.Poison)
        {
            font = _poisonFontColor;
            outline = _poisonOutlineColor;
        }
        else if (IsDoomLethalWithOrb(creature, result))
        {
            // 구체+중독으론 안 죽지만(None) 구체가 HP를 깎아 종말 사정권에 든 적 → 보라(vanilla가 놓치는 경우).
            font = _doomFontColor;
            outline = _doomOutlineColor;
        }
        else
        {
            return; // vanilla 색상 유지(방어도/기본/vanilla가 이미 판정한 종말 보라 포함).
        }

        MegaLabel hpLabel = _hpLabelRef(__instance);
        if (hpLabel == null)
        {
            return;
        }

        hpLabel.AddThemeColorOverride("font_color", font);
        hpLabel.AddThemeColorOverride("font_outline_color", outline);
    }

    /// <summary>
    /// 구체 피해를 반영한 종말 치사 판정 — vanilla IsDoomLethal(종말 ≥ H−P)이 구체 O를 몰라 놓치는 경우를 보완.
    /// 종말 발동 시점(적 턴 종료) 실제 HP = H − O − P(구체·중독 차감 후). 종말 ≥ 그 값이면 종말이 마무리.
    /// </summary>
    private static bool IsDoomLethalWithOrb(Creature creature, EnemyResult result)
    {
        if (!creature.HasPower<DoomPower>())
        {
            return false;
        }
        int doom = creature.GetPowerAmount<DoomPower>();
        if (doom <= 0)
        {
            return false;
        }
        int hpAtDoom = creature.CurrentHp - (int)decimal.Round(result.OrbDamage) - (int)decimal.Round(result.PoisonDamage);
        return doom >= hpAtDoom;
    }
}
