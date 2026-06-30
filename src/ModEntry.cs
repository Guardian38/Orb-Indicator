using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace OrbDmgIndicator;

/// <summary>
/// 모드 진입점.
///
/// 로더 동작(ModManager.TryLoadMod):
///  - DLL을 게임 자신의 AssemblyLoadContext에 로드한 뒤, [ModInitializer]가 붙은
///    타입을 찾아 그 안의 지정 메서드를 호출한다(ModManager.cs:899-909).
///  - [ModInitializer]가 없으면 로더가 알아서 Harmony 인스턴스를 만들고 PatchAll을
///    호출한다(ModManager.cs:915-917). 우리는 초기화 로그·예외 처리를 직접 통제하기
///    위해 명시적으로 ModInitializer를 둔다.
///  - 지정 메서드는 반드시 static 이어야 한다(아니면 CallModInitializer가 거부, :958-965).
/// </summary>
[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public const string ModId = "orb_dmg_indicator";

    private static Harmony? _harmony;

    /// <summary>런타임 Harmony 인스턴스 — 약한 의존 연계(BetterSpire2 등)가 늦은 시점에 추가 패치할 때 쓴다.</summary>
    internal static Harmony? Harmony => _harmony;

    public static void Initialize()
    {
        // 게임의 Log를 그대로 쓰면 모드 메시지가 게임 로그 파일에 함께 남아
        // 다른 모드 로딩 로그와 한 흐름에서 추적된다.
        Log.Info($"[{ModId}] initializing...");

        // Harmony id는 모드별로 유일해야 한다. author.modId 규약을 따른다
        // (로더가 PatchAll 대행 시 쓰는 규약과 동일, ModManager.cs:916).
        _harmony = new Harmony($"GuardianGD.{ModId}");

        // 이 어셈블리 안의 [HarmonyPatch] 클래스를 모두 적용한다.
        // 아직 패치가 없으므로 지금은 0건 — 골격 단계의 정상 상태다.
        _harmony.PatchAll(Assembly.GetExecutingAssembly());

        int patched = _harmony.GetPatchedMethods().Count();
        Log.Info($"[{ModId}] loaded. Applied {patched} Harmony patch(es).");

        // 약한 의존 연계 1차 시도(BetterSpire2 받는 피해 seed). 모드 로드 순서가 보장되지 않아
        // 이 시점엔 BetterSpire2가 아직 안 떴을 수 있으므로, 미감지면 첫 전투 때 재시도한다(PredictionService).
        Compat.BetterSpire2Interop.EnsurePatched(loadComplete: false);
    }
}
