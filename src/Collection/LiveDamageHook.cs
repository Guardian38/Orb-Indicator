using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using OrbDmgIndicator.Calculation;

namespace OrbDmgIndicator.Collection;

/// <summary>
/// 시뮬레이터의 **방어도 이전·비소모형** 피해 보정을 게임 파이프라인에 위임한다.
///
/// 게임은 구체 피해를 raw 그대로 적용하지 않고 Hook.ModifyDamage(...All)로 가산·승산·**상한(Cap)**을
/// 먼저 적용한다(소스 확정). 영체화(IntangiblePower)→1, 질긴 생존력(HardToKillPower)→Amount 의 상한이
/// 여기(방어도 이전)서 걸린다. 이들은 타격마다 소모되지 않는 **비소모형**이라 매 타격 라이브 호출로 정확하다.
///
/// 구체는 ValueProp.Unpowered 라 취약(Vulnerable)/약화(Weak) 등 PoweredAttack 전용 보정은 적용되지 않는다.
///
/// 방어도 **이후**의 **소모형** 보정(단단한 껍질의 턴 누적 상한·Buffer의 다음 N회 무효)은 이 훅으로 처리하지
/// 않는다 — 타격이 진행되며 깎이는 상태라 라이브 호출은 다중 타격에서 과대평가한다. 대신 수집 계층이
/// EnemySnapshot(HpLossCapThisTurn·BufferStacks)으로 담고 시뮬레이터가 직접 추적·소모한다.
/// </summary>
public sealed class LiveDamageHook : OrbDamageSimulator.IDamageHook
{
    private const ValueProp OrbProps = ValueProp.Unpowered;

    private readonly ICombatState _combat;
    private readonly IRunState _run;

    public LiveDamageHook(ICombatState combat)
    {
        _combat = combat;
        _run = combat.RunState;
    }

    public decimal Cap(int enemyId, decimal raw)
    {
        Creature? target = _combat.GetCreature((uint)enemyId);
        if (target == null)
        {
            return raw;
        }
        // 방어도 이전: 가산·승산·상한 전부. 영체화·HardToKill 상한이 여기서 적용된다.
        return Hook.ModifyDamage(
            _run, _combat, target, dealer: null, raw, OrbProps, cardSource: null,
            ModifyDamageHookType.All, CardPreviewMode.None, out _);
    }
}
