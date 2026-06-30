using System;
using System.Collections.Generic;
using System.Linq;

namespace OrbDmgIndicator.Calculation;

/// <summary>
/// 전기(무작위 단일) 다수 적 "전멸 확정" 판정 (명세 §5.1).
///
/// 합계 비교(Σ전기 ≥ Σ체력)는 틀린다 — 무작위 단일 타격은 오버킬로 낭비되므로 타격 크기 대 적 체력
/// 매칭을 봐야 한다. 그래서 <b>순서 충실 DFS</b>로 판정한다: 비전기 이벤트(우박·유리 AoE·암흑 발현)는
/// 결정론이라 그대로 적용하고, <b>전기 타격만</b> 생존 적 전체로 분기한다(적대적 전수). 한 분기라도
/// 생존자를 남기면 미보장.
///
/// <para>
/// <b>암흑 발현 대상은 전기에 의존</b>: 암흑 발현은 "현재 최저 HP"를 노리므로, 직전 전기 타격이 누구를
/// 깎았느냐에 따라 대상이 달라진다. 따라서 암흑 발현 대상은 고정 선계산하지 않고 각 분기의 현재 상태에서
/// 재평가한다(<see cref="ApplyDeterministic"/>). 암흑 자체는 무작위가 없어 분기하지 않지만, 상태 의존이라
/// 전기 분기 뒤에서 다시 계산되어야 정확하다.
/// </para>
///
/// DFS는 정확하지만 분기 수가 ~n^k(n=적 수, k=전기 타격 수)다. 현실적으로 작아 충분하나, 탐색 노드가
/// <see cref="NodeBudget"/>을 넘으면 보수 닫힌식으로 fallback한다(false positive 없음).
///
/// 모든 피해 적용은 <see cref="DamageKernel"/>을 거치므로 영체화/HardToKill(Cap), 단단한 껍질·미끈거림·
/// Buffer(소모형), 방어도, 사망이 시뮬레이터 본 경로와 동일하게 처리된다.
/// </summary>
internal sealed class LightningWipeSolver
{
    /// <summary>전멸 확정 DFS의 총 탐색 노드 상한. 초과 시 보수 닫힌식 fallback으로 전환한다.</summary>
    private const int NodeBudget = 20000;

    /// <summary>DFS 탐색 예산 초과 신호(내부 제어 흐름).</summary>
    private sealed class BudgetExceededException : Exception { }

    private readonly IReadOnlyList<EnemySnapshot> _enemies;
    private readonly IReadOnlyList<DamageEvent> _events;
    private readonly OrbDamageSimulator.IDamageHook _hook;

    private readonly List<int> _choice = new();   // 현재 경로의 전기 대상 id 스택
    private readonly List<int> _witness = new();   // 마지막으로 전멸한 분기의 전기 대상 id 열
    private int _nodes;

    public LightningWipeSolver(
        IReadOnlyList<EnemySnapshot> enemies,
        IReadOnlyList<DamageEvent> events,
        OrbDamageSimulator.IDamageHook hook)
    {
        _enemies = enemies;
        _events = events;
        _hook = hook;
    }

    /// <summary>
    /// 전기로 전멸이 보장되면 true와 함께 그것을 만든 한 분기의 전기 대상 id 열(<paramref name="witness"/>)을
    /// 돌려준다. 표시 계층은 이 분기를 그대로 재생해 전원 처치 수치를 보여준다. 미보장이면 false.
    /// </summary>
    public bool TryFindGuaranteedWipe(out List<int> witness)
    {
        _choice.Clear();
        _witness.Clear();
        _nodes = 0;
        try
        {
            bool guaranteed = Search(SnapshotStates(), 0);
            witness = new List<int>(_witness);
            return guaranteed;
        }
        catch (BudgetExceededException)
        {
            return ClosedFormFallback(out witness);
        }
    }

    /// <summary>
    /// 순서 충실 DFS — 비전기 이벤트는 현재 상태에 그대로 적용하고, 전기 이벤트만 생존 적 전체로 분기한다.
    /// 한 분기라도 생존자를 남기면 미보장(즉시 false). 전멸 분기에 도달하면 그 경로의 대상 열을 witness로 기록.
    /// </summary>
    private bool Search(DmgState[] st, int idx)
    {
        if (++_nodes > NodeBudget)
        {
            throw new BudgetExceededException();
        }

        // 비전기 이벤트는 상태를 그대로 진행(이 호출이 소유한 st라 in-place로 무방).
        // 암흑 발현(LowestHp)도 여기서 처리되며, 이 경로의 직전 전기 타격이 반영된 HP로 대상을 재평가한다.
        while (idx < _events.Count && _events[idx].Target != SimTarget.Lightning)
        {
            ApplyDeterministic(st, _events[idx]);
            idx++;
        }

        if (idx == _events.Count)
        {
            if (AllDead(st))
            {
                _witness.Clear();
                _witness.AddRange(_choice); // 마지막으로 전멸한 분기 = 마지막 기록(사용자: 마지막 경우).
                return true;
            }
            return false; // 생존자 → 이 분기 미전멸.
        }

        // 전기 이벤트 — 생존 적 전수 분기(적대적). 대상 없으면 건너뜀(선택 미기록).
        List<int> alive = AliveIndices(st);
        if (alive.Count == 0)
        {
            return Search(st, idx + 1);
        }

        decimal value = _events[idx].Value;
        foreach (int ti in alive)
        {
            var clone = (DmgState[])st.Clone();
            DamageKernel.Apply(ref clone[ti], _hook, value);
            _choice.Add(st[ti].Id);
            bool ok = Search(clone, idx + 1);
            _choice.RemoveAt(_choice.Count - 1);
            if (!ok)
            {
                return false; // 한 분기라도 생존자 → 전체 미보장.
            }
        }
        return true;
    }

    /// <summary>비전기 이벤트(전체/최저HP)를 상태에 적용. 전기는 호출하지 않는다(분기 전용).</summary>
    private void ApplyDeterministic(DmgState[] st, DamageEvent ev)
    {
        if (ev.Target == SimTarget.AllAlive)
        {
            for (int i = 0; i < st.Length; i++)
            {
                if (st[i].Alive)
                {
                    DamageKernel.Apply(ref st[i], _hook, ev.Value);
                }
            }
        }
        else if (ev.Target == SimTarget.LowestHp)
        {
            int best = LowestHpIndex(st);
            if (best >= 0)
            {
                DamageKernel.Apply(ref st[best], _hook, ev.Value);
            }
        }
    }

    // ── 닫힌식 fallback (예산 초과 시) ──

    /// <summary>
    /// 보수 닫힌식: 전멸 보장 ⟺ k ≥ Σ ceil(r_i / d_i).
    /// r_i = (HP+Block) − 전기 이전 확정 AoE(전기보다 앞선 슬롯의 우박·유리만), d_i = Cap(i, v_min).
    /// v_min·전기 이전 AoE만 크레딧 → 필요 타격을 과대평가 → 미보장 쪽으로만 치우쳐 false positive 없음.
    /// 보장이면 표시용 witness를 focus-fire 그리디로 구성한다.
    /// </summary>
    private bool ClosedFormFallback(out List<int> witness)
    {
        witness = new List<int>();

        int firstLightning = -1;
        for (int i = 0; i < _events.Count; i++)
        {
            if (_events[i].Target == SimTarget.Lightning)
            {
                firstLightning = i;
                break;
            }
        }
        if (firstLightning < 0)
        {
            return false; // 전기 없음 — 호출되지 않아야 함(방어적).
        }

        DmgState[] st = SnapshotStates();
        for (int i = 0; i < firstLightning; i++)
        {
            ApplyDeterministic(st, _events[i]);
        }

        var lightningValues = _events.Where(e => e.Target == SimTarget.Lightning).Select(e => e.Value).ToList();
        int k = lightningValues.Count;
        decimal vMin = lightningValues.Min();

        long needed = 0;
        foreach (DmgState s in st)
        {
            if (!s.Alive)
            {
                continue;
            }
            decimal perHit = _hook.Cap(s.Id, vMin);
            if (perHit <= 0m)
            {
                return false; // 이 적은 전기로 못 죽임 → 미보장.
            }
            decimal r = s.Hp + s.Block; // 전기는 방어도부터 깎으므로 보수적으로 합산.
            needed += (long)Math.Ceiling(r / perHit);
            if (needed > k)
            {
                return false;
            }
        }

        // 보장 확정 — focus-fire(최저 HP+Block 우선) 그리디로 구체적 분기를 만들어 표시에 쓴다.
        return TryGreedyWitness(out witness);
    }

    /// <summary>focus-fire 그리디로 전멸 분기를 시뮬레이션해 전기 대상 id 열을 만든다. 전멸 실패 시 false(보수).</summary>
    private bool TryGreedyWitness(out List<int> witness)
    {
        witness = new List<int>();
        DmgState[] st = SnapshotStates();

        foreach (DamageEvent ev in _events)
        {
            if (ev.Target != SimTarget.Lightning)
            {
                ApplyDeterministic(st, ev);
                continue;
            }

            List<int> alive = AliveIndices(st);
            if (alive.Count == 0)
            {
                continue;
            }

            int best = alive[0];
            decimal bestVal = st[alive[0]].Hp + st[alive[0]].Block;
            foreach (int i in alive)
            {
                decimal v = st[i].Hp + st[i].Block;
                if (v < bestVal)
                {
                    bestVal = v;
                    best = i;
                }
            }
            witness.Add(st[best].Id);
            DamageKernel.Apply(ref st[best], _hook, ev.Value);
        }

        return AllDead(st);
    }

    // ── 상태 헬퍼 ──

    private DmgState[] SnapshotStates()
    {
        var arr = new DmgState[_enemies.Count];
        for (int i = 0; i < arr.Length; i++)
        {
            arr[i] = DmgState.From(_enemies[i]);
        }
        return arr;
    }

    private static List<int> AliveIndices(DmgState[] st)
    {
        var list = new List<int>();
        for (int i = 0; i < st.Length; i++)
        {
            if (st[i].Alive)
            {
                list.Add(i);
            }
        }
        return list;
    }

    /// <summary>생존 중 최저 HP 적의 인덱스(동률은 가장 이른 슬롯). 없으면 -1.</summary>
    private static int LowestHpIndex(DmgState[] st)
    {
        int best = -1;
        decimal min = decimal.MaxValue;
        for (int i = 0; i < st.Length; i++)
        {
            if (st[i].Alive && st[i].Hp < min)
            {
                min = st[i].Hp;
                best = i;
            }
        }
        return best;
    }

    private static bool AllDead(DmgState[] st)
    {
        foreach (DmgState s in st)
        {
            if (s.Alive)
            {
                return false;
            }
        }
        return true;
    }
}
