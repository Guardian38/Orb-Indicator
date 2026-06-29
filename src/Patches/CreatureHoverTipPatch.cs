using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using OrbDmgIndicator.Calculation;
using OrbDmgIndicator.Collection;
using OrbDmgIndicator.Ui;

namespace OrbDmgIndicator.Patches;

/// <summary>
/// 적 호버 툴팁 목록(`Creature.HoverTips`)에 구체 데미지 예측 항목을 끼워넣는다.
///
/// 게임 경로(소스 확정):
///   적 히트박스 focus → NCreature.ShowHoverTips(Entity.HoverTips)
///     → NHoverTipSet.CreateAndShow(Hitbox, hoverTips) → 목록을 박스로 렌더 (NHoverTipSet.Init).
///   `Creature.HoverTips` getter(Creature.cs:212)가 그 목록을 만든다: 의도(intent) 먼저 → 파워(buff/debuff).
///   → 이 getter Postfix로 "의도 직후"에 우리 HoverTip 1개를 Insert한다.
///
/// 현재 단계(임시 가시화):
///   수집 계층(SnapshotCollector) → OrbDamageSimulator로 이 적의 EnemyResult를 산출하고,
///   계산되는 모든 항목(툴팁 줄·잔여 체력·O/P/D·막타·표시여부)을 그대로 나열한다.
///   합산 요약/Shift 상세 분기는 아직 없음(설계: hover_tooltip_design.md §4·5).
///
/// 제약(소스 확정): NHoverTipSet.Init은 `if (item is HoverTip)`로 분기하므로 게임 HoverTip(struct)이어야
///   한다. 생성자는 제목에 LocString(현지화 키)을 요구하고 없는 키는 LocException을 던지므로, loc 테이블을
///   만들기 전까지는 reflection으로 private setter(Title/Description)를 채운다(정식은 모드 loc 테이블).
/// </summary>
[HarmonyPatch(typeof(Creature), "HoverTips", MethodType.Getter)]
public static class CreatureHoverTipPatch
{
    private const string TipId = "GuardianGD.orb_dmg_indicator.prediction";

    // HoverTip(struct)의 Title/Description는 private setter라 reflection으로 채운다(loc 테이블 도입 전 한정).
    private static readonly MethodInfo? _setTitle =
        typeof(HoverTip).GetProperty("Title")?.GetSetMethod(nonPublic: true);

    private static readonly MethodInfo? _setDescription =
        typeof(HoverTip).GetProperty("Description")?.GetSetMethod(nonPublic: true);

    // HoverTip.Icon도 private setter. 제목 옆에 디펙트 아이콘(좌상단 패널 아이콘)을 띄워 다른 툴팁과 정체성 일관.
    private static readonly MethodInfo? _setIcon =
        typeof(HoverTip).GetProperty("Icon")?.GetSetMethod(nonPublic: true);

    private static bool _loggedError;

    private static void Postfix(Creature __instance, ref IEnumerable<IHoverTip> __result)
    {
        if (!__instance.IsEnemy || __instance.CombatId == null)
        {
            return;
        }

        try
        {
            string? body = BuildPredictionText(__instance);
            if (body == null)
            {
                return; // 표시할 것 없음(구체 소스 없음 등).
            }

            List<IHoverTip> list = __result as List<IHoverTip> ?? __result.ToList();
            int insertAt = IntentTipCount(__instance);
            insertAt = Math.Min(insertAt, list.Count);
            list.Insert(insertAt, BuildTip(body));
            __result = list;
        }
        catch (Exception ex)
        {
            // 계산/수집 오류가 게임의 호버 툴팁을 깨지 않도록 삼킨다. 최초 1회만 로깅.
            if (!_loggedError)
            {
                _loggedError = true;
                Log.Error($"[orb_dmg_indicator] hover tip build failed: {ex}");
            }
        }
    }

    /// <summary>의도 툴팁 개수 = "의도 직후" 삽입 위치(getter가 의도를 목록 앞에 넣으므로).</summary>
    private static int IntentTipCount(Creature creature)
    {
        try
        {
            if (creature.IsMonster && creature.Monster?.NextMove?.Intents != null)
            {
                return creature.Monster.NextMove.Intents.Count(i => i.HasIntentTip);
            }
        }
        catch
        {
            // 무시 — 0으로 폴백.
        }
        return 0;
    }

    /// <summary>
    /// 이 적에 대해 계산되는 모든 항목을 나열한 본문을 만든다. 구체 소스가 없으면 null.
    /// </summary>
    private static string? BuildPredictionText(Creature enemy)
    {
        var combat = enemy.CombatState;
        CombatSnapshot? snapshot = SnapshotCollector.TryCollect(combat);
        if (snapshot == null || snapshot.Players.Count == 0)
        {
            return null; // 구체를 가진 플레이어가 없으면 표시하지 않는다.
        }

        // 피해 보정(영체화·단단한 껍질 등)을 게임 파이프라인에 위임. combat은 snapshot이 non-null이면 non-null.
        var damageHook = new LiveDamageHook(combat!);
        IReadOnlyDictionary<int, EnemyResult> results = OrbDamageSimulator.Simulate(snapshot, damageHook);
        if (!results.TryGetValue((int)enemy.CombatId!.Value, out EnemyResult? result) || result == null)
        {
            return null;
        }

        var sb = new StringBuilder();

        // 계산된 툴팁 줄(유효 피해 > 0, 발동 시간순). 종말 제외는 시뮬레이터가 이미 처리.
        if (result.TooltipLines.Count > 0)
        {
            foreach (TooltipLine line in result.TooltipLines)
            {
                sb.Append(CategoryTag(line.Category))
                  .Append("  ")
                  .Append(TooltipStyle.Number(Num(line.Amount)))
                  .Append(' ').Append(ModLocalization.Get("tooltip.damage")).Append('\n');
            }
        }
        else
        {
            sb.Append(ModLocalization.Get("tooltip.no_orb_damage")).Append('\n');
        }

        // 잔여 체력 / 처치. 구분선은 박스 너비(폰트 자동크기)에 따라 넘쳐 중첩되므로 빈 줄로 띄운다.
        sb.Append('\n');
        sb.Append(result.RemainingHp <= 0m
            ? ModLocalization.Get("tooltip.lethal")
            : $"{ModLocalization.Get("tooltip.remaining")}  {TooltipStyle.Number(Num(result.RemainingHp))}");

        // 디버그 덤프 — 기본 꺼짐, 콘솔 `orbdebug`로 토글(TooltipDebug). 개발용 raw 수치 검증.
        if (TooltipDebug.Enabled)
        {
            TooltipDebug.AppendDump(sb, result);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 피해원인 라벨을 게임의 "인라인 아이콘 + 현지화 이름" 태그로 만든다. 아이콘은 RichTextLabel BBCode
    /// `[img=top]res://...png[/img]`(게임 hover tip Description = MegaRichTextLabel이라 렌더됨), 이름은
    /// 게임 모델의 LocString.GetRawText()(현재 언어 자동 적용). 모델 canonical 인스턴스는 ModelDb로 조회.
    /// 발현(Evoke)도 같은 구체이므로 패시브와 동일 아이콘/이름을 쓴다.
    /// </summary>
    private static string CategoryTag(DamageCategory category)
    {
        (string icon, string name) = category switch
        {
            DamageCategory.Hailstorm => PowerTag<HailstormPower>(),
            DamageCategory.LightningPassive => OrbTag<LightningOrb>(),
            DamageCategory.LightningEvoke => OrbTag<LightningOrb>(),
            DamageCategory.GlassPassive => OrbTag<GlassOrb>(),
            DamageCategory.GlassEvoke => OrbTag<GlassOrb>(),
            DamageCategory.DarkEvoke => OrbTag<DarkOrb>(),
            DamageCategory.Poison => PowerTag<PoisonPower>(),
            _ => ("", category.ToString()),
        };
        // 피해원인 이름은 기본색(게임 본문 관례 — 제목만 금색, 본문은 무채색 + 수치만 강조). 아이콘은 앞에 인라인.
        return icon.Length > 0 ? icon + " " + name : name;
    }

    /// <summary>구체 모델 → (인라인 아이콘 BBCode, 현지화 이름). 아이콘 경로 = AssetPaths 첫 항목(IconPath).</summary>
    private static (string icon, string name) OrbTag<T>() where T : OrbModel
    {
        OrbModel orb = ModelDb.GetById<OrbModel>(ModelDb.GetId<T>());
        return (TooltipStyle.Icon(orb.AssetPaths.First()), orb.Title.GetRawText());
    }

    /// <summary>파워 모델 → (인라인 아이콘 BBCode, 현지화 이름). 아이콘 경로 = ResolvedBigIconPath(PNG, atlas .tres 아님).</summary>
    private static (string icon, string name) PowerTag<T>() where T : PowerModel
    {
        PowerModel power = ModelDb.GetById<PowerModel>(ModelDb.GetId<T>());
        return (TooltipStyle.Icon(power.ResolvedBigIconPath), power.Title.GetRawText());
    }

    private static string Num(decimal value)
    {
        // 정수면 소수점 없이, 아니면 그대로.
        return value == decimal.Truncate(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
    }

    private static HoverTip BuildTip(string description)
    {
        // 공개 setter(Id/IsInstanced/IsDebuff/IsSmart)는 초기화자로, private setter(Title/Description)는
        // 박싱 후 reflection으로 채운다. 고유 Id + IsInstanced=true 로 RemoveDupes 합치기를 회피한다.
        object boxed = new HoverTip
        {
            Id = TipId,
            IsInstanced = true,
            IsDebuff = false,
            IsSmart = false,
        };
        _setTitle?.Invoke(boxed, new object[] { ModLocalization.Get("tooltip.title") });
        _setDescription?.Invoke(boxed, new object[] { description });

        // 제목 옆 아이콘 = 디펙트 좌상단 패널 아이콘(CharacterModel.IconTexture). 경로 하드코딩이 아니라
        // 게임 모델 속성을 그대로 쓰므로, 게임이 이 아이콘에 스킨을 입히면 자동으로 따라간다.
        Texture2D? icon = DefectIcon();
        if (icon != null)
        {
            _setIcon?.Invoke(boxed, new object[] { icon });
        }
        return (HoverTip)boxed;
    }

    /// <summary>디펙트 캐릭터의 좌상단 패널 아이콘. 조회 실패 시 null(아이콘 없이 표시).</summary>
    private static Texture2D? DefectIcon()
    {
        try
        {
            return ModelDb.GetById<CharacterModel>(ModelDb.GetId<Defect>()).IconTexture;
        }
        catch
        {
            return null;
        }
    }
}
