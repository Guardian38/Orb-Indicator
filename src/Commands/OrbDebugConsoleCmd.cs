using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using OrbDmgIndicator.Ui;

namespace OrbDmgIndicator.Commands;

/// <summary>
/// 콘솔 명령 <c>orbdebug</c> — 구체 데미지 예측 툴팁의 디버그 덤프 표시를 토글한다.
///
/// 게임이 모드 어셈블리의 <see cref="AbstractConsoleCmd"/> 서브타입을 자동 등록한다
/// (DevConsole 생성자 → <c>ReflectionHelper.GetSubtypesInMods&lt;AbstractConsoleCmd&gt;()</c>).
/// <see cref="DebugOnly"/>는 기본 true지만, 모드 구동 시(<c>ModManager.IsRunningModded()</c>) 허용된다.
/// public + 매개변수 없는 생성자라야 <c>Activator.CreateInstance</c>로 인스턴스화된다.
/// </summary>
public class OrbDebugConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "orbdebug";

    public override string Args => "";

    public override string Description => "Toggle the orb damage prediction tooltip's debug dump.";

    public override bool IsNetworked => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        TooltipDebug.Enabled = !TooltipDebug.Enabled;
        return new CmdResult(success: true, $"orb damage debug dump: {(TooltipDebug.Enabled ? "ON" : "OFF")}");
    }
}
