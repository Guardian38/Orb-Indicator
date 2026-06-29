using System.Text;
using OrbDmgIndicator.Calculation;

namespace OrbDmgIndicator.Ui;

/// <summary>
/// 호버 툴팁의 디버그 덤프(원천 수치 노출) — 본 표시와 분리한 개발용 도구.
///
/// 기본 <b>꺼짐</b>. 콘솔 명령 <c>orbdebug</c>(<see cref="OrbDmgIndicator.Commands.OrbDebugConsoleCmd"/>)로 토글한다.
/// 전기 전멸 확정(§5.1) 등 향후 기능 개발 시 계산값을 눈으로 검증하는 용도라, 평소 사용자에겐 보이지 않게 둔다.
/// </summary>
internal static class TooltipDebug
{
    /// <summary>켜지면 툴팁 끝에 raw 수치 덤프를 붙인다. 콘솔 <c>orbdebug</c>로 토글.</summary>
    public static bool Enabled;

    /// <summary>EnemyResult의 원천 수치(HP·O·P·막타·표시여부)를 한 줄로 덤프한다.</summary>
    public static void AppendDump(StringBuilder sb, EnemyResult result)
    {
        sb.Append("\n\n[debug] HP=").Append(result.CurrentHp)
          .Append(" O=").Append(result.OrbDamage)
          .Append(" P=").Append(result.PoisonDamage)
          .Append(" kill=").Append(result.KillSource)
          .Append(" show=").Append(result.ShowTooltip);
    }
}
