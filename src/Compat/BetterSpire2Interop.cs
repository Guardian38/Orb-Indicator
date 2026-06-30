using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Logging;
using OrbDmgIndicator.Calculation;
using OrbDmgIndicator.Patches;

namespace OrbDmgIndicator.Compat;

/// <summary>
/// BetterSpire2 "받는 피해(incoming damage)" 라벨 연계 — 우리 구체 치사 판정을 주입한다.
///
/// BetterSpire2의 <c>DamageTracker.Recalculate()</c>는 적이 자기 턴 전에 죽을 예정이면
/// <c>fatalEnemies</c> 집합에 넣어 받는 피해 합산에서 제외한다. 하지만 자체 구체 시뮬레이션은
/// 유리·암흑 발현·전기 발현 등을 누락한다(상세 <c>Compatible/betterspire2/integration_plan.md §2.3</c>).
///
/// 우리는 <c>fatalEnemies</c>가 갓 생성된 직후 Transpiler로 <see cref="SeedFatalEnemies"/>를 호출해
/// <see cref="PredictionService"/>의 권위 있는 결과(<c>RemainingHp ≤ 0</c>)를 **합집합으로 미리 채운다**.
/// 이 한 지점 이후 BetterSpire2의 모든 단계(전기·우박·중독 루프, list 구성, 2차 적 정리)가
/// <c>fatalEnemies</c>를 존중하므로 파이프라인 전체가 일관된다.
///
/// **약한 의존(soft dependency)**: BetterSpire2가 감지되지 않으면 패치하지 않는다(0 비용·0 리스크).
/// 모드 로드 순서가 보장되지 않으므로(<c>modding_overview.md</c>) 초기화 시점에 한 번,
/// 전투 진입 시점(모든 모드 로드 완료 보장)에 한 번 더 시도한다(<see cref="EnsurePatched"/>).
/// </summary>
public static class BetterSpire2Interop
{
    private static bool _patched;
    private static bool _gaveUp;
    private static bool _loggedSeedError;

    /// <summary>
    /// BetterSpire2 감지 시 <c>Recalculate</c>에 seed Transpiler를 1회 적용한다(멱등).
    /// </summary>
    /// <param name="loadComplete">
    /// true면 모든 모드 로드가 끝난 시점(전투 진입)이라, 그래도 타입이 없으면 미설치로 확정하고 포기한다.
    /// false(초기화 시점)면 로드 순서상 아직 BetterSpire2가 안 떴을 수 있어 포기하지 않고 다음 기회를 둔다.
    /// </param>
    internal static void EnsurePatched(bool loadComplete)
    {
        if (_patched || _gaveUp)
        {
            return;
        }

        Harmony? harmony = ModEntry.Harmony;
        if (harmony == null)
        {
            return;
        }

        Type? trackerType = AccessTools.TypeByName("BetterSpire2.DamageTracker");
        if (trackerType == null)
        {
            // 아직 BetterSpire2 어셈블리가 로드되지 않았을 수 있다(모드 로드 순서 비보장).
            // 전투 진입(loadComplete) 시점에도 없으면 미설치로 확정하고 더 시도하지 않는다.
            if (loadComplete)
            {
                _gaveUp = true;
            }
            return;
        }

        try
        {
            MethodInfo? recalc = AccessTools.Method(trackerType, "Recalculate");
            if (recalc == null)
            {
                _gaveUp = true;
                Log.Error($"[{ModEntry.ModId}] BetterSpire2.DamageTracker.Recalculate not found — incoming-damage seeding disabled.");
                return;
            }

            harmony.Patch(recalc, transpiler: new HarmonyMethod(typeof(BetterSpire2Interop), nameof(Transpiler)));
            _patched = true;
            Log.Info($"[{ModEntry.ModId}] BetterSpire2 detected — seeding orb-kills into incoming-damage fatalEnemies.");
        }
        catch (Exception ex)
        {
            // 깨진 Transpiler를 매 프레임 재시도하지 않는다(원본 보존, 우리 모드 자체 기능 무영향).
            _gaveUp = true;
            Log.Error($"[{ModEntry.ModId}] BetterSpire2 patch failed — incoming-damage seeding disabled: {ex}");
        }
    }

    /// <summary>
    /// <c>fatalEnemies = new HashSet&lt;Creature&gt;()</c> 직후에 seed 호출을 끼운다.
    /// 갓 생성된 빈 HashSet을 <c>Dup</c>으로 복제해 우리 메서드에 넘기면, 참조로 채운 뒤 원래 <c>stloc</c>이 저장한다.
    /// 메서드 내 두 번째 <c>HashSet&lt;Creature&gt;</c>(activeCreatures)는 건드리지 않도록 첫 매치에만 삽입한다.
    /// 앵커를 못 찾으면 원본을 그대로 반환(무회귀)하고 로깅한다.
    /// </summary>
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo seed = AccessTools.Method(typeof(BetterSpire2Interop), nameof(SeedFatalEnemies));
        bool injected = false;

        foreach (CodeInstruction instr in instructions)
        {
            yield return instr;

            if (!injected
                && instr.opcode == OpCodes.Newobj
                && instr.operand is ConstructorInfo ctor
                && ctor.DeclaringType == typeof(HashSet<Creature>))
            {
                yield return new CodeInstruction(OpCodes.Dup);
                yield return new CodeInstruction(OpCodes.Call, seed);
                injected = true;
            }
        }

        if (!injected)
        {
            Log.Error($"[{ModEntry.ModId}] BetterSpire2 transpiler anchor (HashSet<Creature>) not found — seeding skipped (BetterSpire2 IL changed?).");
        }
    }

    /// <summary>
    /// 우리 예측상 자기 턴 전에 죽는 적(<c>RemainingHp ≤ 0</c>)을 <paramref name="fatalEnemies"/>에 **합집합**으로 추가한다.
    /// 기존 항목(인페르노 등 BetterSpire2 자체 출처)은 보존한다. 전투 상태는 BetterSpire2와 동일하게
    /// <c>CombatManager.Instance.DebugOnlyGetState()</c>로 얻고, 적별 결과는 프레임 캐시된 <see cref="PredictionService"/>를 재사용한다.
    /// 패치된 외부 메서드 안에서 호출되므로 전체 try/catch로 BetterSpire2를 깨지 않는다.
    /// </summary>
    public static void SeedFatalEnemies(HashSet<Creature> fatalEnemies)
    {
        if (fatalEnemies == null)
        {
            return;
        }

        try
        {
            CombatState? state = CombatManager.Instance?.DebugOnlyGetState();
            if (state == null)
            {
                return;
            }

            foreach (Creature enemy in state.Enemies)
            {
                if (enemy == null || enemy.IsDead)
                {
                    continue;
                }
                EnemyResult? result = PredictionService.TryGet(enemy);
                if (result != null && result.RemainingHp <= 0m)
                {
                    fatalEnemies.Add(enemy);
                }
            }
        }
        catch (Exception ex)
        {
            if (!_loggedSeedError)
            {
                _loggedSeedError = true;
                Log.Error($"[{ModEntry.ModId}] BetterSpire2 SeedFatalEnemies failed: {ex}");
            }
        }
    }
}
