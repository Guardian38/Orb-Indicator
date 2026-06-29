using System;
using HarmonyLib;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using OrbDmgIndicator.Calculation;

namespace OrbDmgIndicator.Patches;

/// <summary>
/// 적 체력바에 구체 피해 세그먼트(파랑)를 그린다.
///
/// 명세(§4②): 세그먼트 좌→우 = 남는 체력 → (종말, vanilla) → 중독(P) → 구체(O) → 이미 잃은 체력.
/// 구체는 내 턴 종료에 가장 먼저 발동하므로 현재 HP 경계에 가장 가까운(가장 오른쪽) 슬라이스를 차지한다.
/// vanilla는 중독을 가장 오른쪽 [H-P, H]에 그리므로, 구체가 끼면 중독을 구체 폭만큼 왼쪽으로 민다.
///
/// 이음새 처리(레이어 + cap): 체력바 세그먼트는 우측 끝이 막대 끝 모양(뾰족 ＞)인 nine-patch다. 매끈한
/// 이음새는 "왼쪽 세그먼트의 우측 ＞ cap이 오른쪽 세그먼트 위에 얹어져야" 한다 — 즉 왼쪽 세그먼트가
/// 더 위 레이어여야 한다. 따라서 레이어 순서를 적HP(hp) < 구체(orb) < 중독(poison)으로 둔다
/// (CreateOrbForeground의 MoveChild). 구체는 좌측 cap(PatchMarginLeft)만큼 왼쪽으로 확장해(SetSegment)
/// 중독 ＞ 뒤 배경을 파랑으로 채우고, 그 위에 중독의 우측 ＞이 얹어진다. 구체가 최상단이면(기본 AddChild)
/// 거꾸로 구체가 중독 ＞을 덮어 세로 이음새가 생긴다. 구체의 우측 cap(막대 끝 ＞)은 그대로 노출된다.
/// cap 폭은 원본 중독 노드에서 읽는다 — Duplicate로 만든 구체 노드는 C#상 NinePatchRect로 인식 안 될 수 있다.
///
/// 종말(Doom): vanilla 종말 세그먼트는 [0, 종말수치]의 좌측 고정 threshold 마커라 현재 HP 경계 근처의
/// 구체/중독 재배치와 충돌하지 않는다 → 종말이 있어도 구체 세그먼트를 그대로 그린다(공존).
/// O ≤ 0(이 적에 유효 구체 피해 없음): 미개입 → vanilla 중독/종말 그대로.
///
/// 좌표계는 vanilla와 동일: 컨테이너 폭 MaxFgWidth, 세그먼트 [A,B]는 OffsetLeft=Fg(A)(−좌측cap), OffsetRight=Fg(B)−MaxFg.
/// </summary>
[HarmonyPatch(typeof(NHealthBar), "RefreshForeground")]
public static class NHealthBarSegmentPatch
{
    private const string OrbNodeName = "OrbForeground";

    private static readonly AccessTools.FieldRef<NHealthBar, Creature> _creatureRef =
        AccessTools.FieldRefAccess<NHealthBar, Creature>("_creature");
    private static readonly AccessTools.FieldRef<NHealthBar, Control> _hpForegroundRef =
        AccessTools.FieldRefAccess<NHealthBar, Control>("_hpForeground");
    private static readonly AccessTools.FieldRef<NHealthBar, Control> _poisonForegroundRef =
        AccessTools.FieldRefAccess<NHealthBar, Control>("_poisonForeground");
    private static readonly AccessTools.FieldRef<NHealthBar, Control> _hpForegroundContainerRef =
        AccessTools.FieldRefAccess<NHealthBar, Control>("_hpForegroundContainer");
    private static readonly AccessTools.FieldRef<NHealthBar, float> _expectedMaxFgWidthRef =
        AccessTools.FieldRefAccess<NHealthBar, float>("_expectedMaxFgWidth");

    private static readonly Color _orbColor = StsColors.blue; // #87CEEB — HP 숫자 파랑과 동일.

    private static bool _loggedError;

    private static void Postfix(NHealthBar __instance)
    {
        // 레이아웃/노드 생성 오류가 게임 체력바를 깨지 않도록 삼킨다(최초 1회만 로깅).
        try
        {
            Apply(__instance);
        }
        catch (Exception ex)
        {
            if (!_loggedError)
            {
                _loggedError = true;
                Log.Error($"[orb_dmg_indicator] health bar segment failed: {ex}");
            }
        }
    }

    private static void Apply(NHealthBar __instance)
    {
        Creature creature = _creatureRef(__instance);
        Control hpFg = _hpForegroundRef(__instance);
        Control poisonFg = _poisonForegroundRef(__instance);
        Control? orbFg = FindOrbForeground(hpFg);

        // 개입하지 않는 모든 경로에서 기존 구체 노드는 숨긴다(이전 프레임 잔상 방지).
        if (creature == null || !creature.IsEnemy || creature.CombatId == null
            || creature.CurrentHp <= 0 || creature.HpDisplay.IsInfinite())
        {
            HideOrb(orbFg);
            return;
        }

        EnemyResult? result = PredictionService.TryGet(creature);
        if (result == null)
        {
            HideOrb(orbFg);
            return;
        }

        int H = creature.CurrentHp;
        int O = (int)decimal.Round(result.OrbDamage);
        int P = (int)decimal.Round(result.PoisonDamage);
        if (O <= 0)
        {
            HideOrb(orbFg); // 유효 구체 피해 없음 → vanilla 중독/종말 그대로.
            return;
        }

        // 종말은 좌측 고정 threshold 마커라 구체/중독 재배치와 충돌하지 않는다 → 그대로 진행(공존).
        float maxFg = MaxFgWidth(__instance);
        float rightEdge = Fg(creature, maxFg, H) - maxFg; // 현재 HP 경계(가장 오른쪽)의 OffsetRight.
        // 좌측 cap 폭은 원본 중독 노드(진짜 NinePatchRect)에서 읽는다. orbFg는 Duplicate()로 만들어
        // C# 마샬링상 NinePatchRect로 인식 안 될 수 있어(`node is NinePatchRect` false) cap 당김이
        // 스킵된다 → 그 경우에도 cap을 강제 적용하기 위함.
        float capLeft = poisonFg is NinePatchRect pnp ? pnp.PatchMarginLeft : 6f;

        orbFg ??= CreateOrbForeground(hpFg, poisonFg);
        orbFg.Visible = true;

        if (O >= H)
        {
            // 구체 단독 치사 — 구체가 [0,H] 전체를 덮고 적HP·중독은 숨김.
            SetSegmentFull(orbFg, rightEdge);
            hpFg.Visible = false;
            poisonFg.Visible = false;
            return;
        }

        int orbLeftHp = H - O; // 구체 세그먼트 왼쪽 경계.
        // 구체 = [H-O, H]. 자기 좌측 cap만큼 왼쪽(중독/적HP)을 덮어 이음새를 매끄럽게.
        SetSegment(orbFg, Fg(creature, maxFg, orbLeftHp), rightEdge, capLeft);

        if (O + P >= H)
        {
            // 중독이 마무리 — 중독이 [0, H-O](가장 왼쪽), 적HP 숨김.
            poisonFg.Visible = true;
            SetSegmentFull(poisonFg, Fg(creature, maxFg, orbLeftHp) - maxFg);
            hpFg.Visible = false;
            return;
        }

        // 비치사 — 적HP [0, H-O-P], 중독 [H-O-P, H-O], 구체 [H-O, H].
        int redRightHp = H - O - P;
        hpFg.Visible = true;
        hpFg.OffsetRight = Fg(creature, maxFg, redRightHp) - maxFg; // 가장 왼쪽이라 OffsetLeft은 그대로(0).
        if (P > 0)
        {
            poisonFg.Visible = true;
            // 중독 = [H-O-P, H-O]. 자기 좌측 cap만큼 적HP를 덮는다.
            SetSegment(poisonFg, Fg(creature, maxFg, redRightHp), Fg(creature, maxFg, orbLeftHp) - maxFg, capLeft);
        }
        else
        {
            poisonFg.Visible = false;
        }
    }

    /// <summary>
    /// 가장 왼쪽이 아닌 세그먼트 배치 — 자기 좌측 cap(PatchMarginLeft)만큼 왼쪽으로 겹쳐 왼쪽 이웃의
    /// 우측 끝(뾰족함)을 덮는다(vanilla hp→중독 이음새와 동일). left=Fg(A)−좌측cap, right=Fg(B)−MaxFg.
    /// </summary>
    private static void SetSegment(Control node, float leftBoundaryPx, float offsetRight, float patchMarginLeft)
    {
        // patchMarginLeft는 원본 중독 노드에서 읽은 값. Duplicate 노드(orbFg)는 `is NinePatchRect`가
        // false일 수 있어 노드 자체에서 cap을 못 읽으므로 호출자가 명시적으로 넘긴다.
        node.OffsetLeft = Math.Max(0f, leftBoundaryPx - patchMarginLeft);
        node.OffsetRight = offsetRight;
    }

    /// <summary>가장 왼쪽 세그먼트 배치 — 겹침 없이 [0, B].</summary>
    private static void SetSegmentFull(Control node, float offsetRight)
    {
        node.OffsetLeft = 0f;
        node.OffsetRight = offsetRight;
    }

    private static float MaxFgWidth(NHealthBar bar)
    {
        float expected = _expectedMaxFgWidthRef(bar);
        return expected > 0f ? expected : _hpForegroundContainerRef(bar).Size.X;
    }

    /// <summary>vanilla GetFgWidth와 동일 공식 — 경계가 적HP 막대와 정확히 맞도록.</summary>
    private static float Fg(Creature creature, float maxFg, int amount)
    {
        if (creature.MaxHp <= 0)
        {
            return 0f;
        }
        float val = (float)amount / creature.MaxHp * maxFg;
        return Math.Max(val, creature.CurrentHp > 0 ? 12f : 0f);
    }

    private static Control? FindOrbForeground(Control? hpFg)
    {
        return hpFg?.GetParent()?.GetNodeOrNull<Control>(OrbNodeName);
    }

    private static Control CreateOrbForeground(Control hpFg, Control poisonFg)
    {
        // 중독 막대(_poisonForeground)를 복제 → 파랑 틴트로 깨끗한 단색.
        var dup = (Control)poisonFg.Duplicate();
        dup.Name = OrbNodeName;
        dup.SelfModulate = _orbColor;
        Node parent = hpFg.GetParent();
        parent.AddChild(dup);

        // 레이어 순서: 적HP(hp) < 구체(orb) < 중독(poison). 구체를 중독보다 "아래"로 내려야
        // 중독의 오른쪽 ＞ cap이 구체(파랑) 위에 얹어진다. 구체는 좌측 cap만큼 왼쪽으로 확장해
        // 그 ＞ 뒤 배경을 파랑으로 채운다. 구체가 최상단이면(기본 AddChild) 거꾸로 구체가 중독 ＞을
        // 덮어 세로 이음새가 생긴다. poison 바로 앞으로 이동(같은 부모일 때만).
        if (poisonFg.GetParent() == parent)
        {
            parent.MoveChild(dup, poisonFg.GetIndex());
        }
        return dup;
    }

    private static void HideOrb(Control? orbFg)
    {
        if (orbFg != null)
        {
            orbFg.Visible = false;
        }
    }
}
