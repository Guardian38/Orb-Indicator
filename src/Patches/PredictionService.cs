using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Logging;
using OrbDmgIndicator.Calculation;
using OrbDmgIndicator.Collection;

namespace OrbDmgIndicator.Patches;

/// <summary>
/// 라이브 전투 상태 → 적별 EnemyResult를 산출하고 **프레임 단위로 캐시**한다.
///
/// 체력바 패치(RefreshText 색상·RefreshForeground 세그먼트)는 한 갱신 웨이브에서 적마다 호출되는데,
/// OrbDamageSimulator.Simulate는 한 번에 전 적의 결과 딕셔너리를 만든다. 적별로 매번 전체 시뮬레이션을
/// 돌리면 O(적^2)가 되므로, 같은 프레임 안에서는 첫 호출 결과를 재사용한다(HP/방어도 변화는 프레임을
/// 넘겨 일어나므로 프레임 키로 충분). 동시에 두 전투가 진행되지 않아 단일 캐시로 안전하다.
/// </summary>
internal static class PredictionService
{
    private static IReadOnlyDictionary<int, EnemyResult>? _cache;
    private static ulong _cacheFrame = ulong.MaxValue;
    private static bool _loggedError;

    /// <summary>이 적의 예측 결과. 구체 보유 플레이어가 없거나 계산 실패 시 null.</summary>
    public static EnemyResult? TryGet(Creature enemy)
    {
        if (enemy == null || enemy.CombatId == null)
        {
            return null;
        }

        IReadOnlyDictionary<int, EnemyResult>? results = GetResults(enemy.CombatState);
        if (results != null && results.TryGetValue((int)enemy.CombatId.Value, out EnemyResult? result))
        {
            return result;
        }
        return null;
    }

    private static IReadOnlyDictionary<int, EnemyResult>? GetResults(ICombatState? combat)
    {
        // 전투 중(=모든 모드 로드 완료 보장) 약한 의존 연계 패치를 확정 시도한다. 이미 해결됐으면 즉시 반환(멱등).
        Compat.BetterSpire2Interop.EnsurePatched(loadComplete: true);

        ulong frame = Engine.GetProcessFrames();
        if (_cacheFrame == frame)
        {
            return _cache; // null도 유효한 캐시값(이 프레임엔 구체 소스 없음).
        }
        _cacheFrame = frame;

        try
        {
            CombatSnapshot? snapshot = SnapshotCollector.TryCollect(combat);
            if (snapshot == null || snapshot.Players.Count == 0)
            {
                _cache = null;
                return null;
            }

            var damageHook = new LiveDamageHook(combat!); // snapshot이 non-null이면 combat도 non-null.
            _cache = OrbDamageSimulator.Simulate(snapshot, damageHook);
            return _cache;
        }
        catch (System.Exception ex)
        {
            // 계산/수집 오류가 체력바를 깨지 않도록 삼킨다. 최초 1회만 로깅.
            if (!_loggedError)
            {
                _loggedError = true;
                Log.Error($"[orb_dmg_indicator] prediction compute failed: {ex}");
            }
            _cache = null;
            return null;
        }
    }
}
