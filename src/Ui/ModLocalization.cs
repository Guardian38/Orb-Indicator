using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;

namespace OrbDmgIndicator.Ui;

/// <summary>
/// 모드 자체 고정 문자열의 현지화 — DLL 옆 <c>localization/{언어코드}.lang</c>(JSON) 파일에서 로드한다.
/// (BetterSpire2와 동일한 외부 .lang 방식. 번역 검수 시 코드 재빌드 없이 파일만 고치면 된다.)
///
/// 파일명 = 게임 3글자 언어 코드(<see cref="LocManager"/>.Language; eng·kor·zhs·jpn…). 현재 언어 파일이
/// 없거나 키가 없으면 <b>영어(eng)로 fallback</b>, 그래도 없으면 키 문자열을 그대로 돌려준다.
/// 게임 키워드(우박폭풍·전기 등)는 게임 LocString이 자동 현지화하므로 여기 두지 않는다.
///
/// JSON 파싱은 Godot <see cref="Json"/>(게임 환경 보장)으로 한다 — System.Text.Json 의존성 회피.
/// </summary>
internal static class ModLocalization
{
    private const string DefaultLang = "eng";

    private static Dictionary<string, string> _english = new();
    private static bool _englishLoaded;
    private static Dictionary<string, string>? _current;
    private static string? _loadedLang;

    /// <summary>키에 해당하는 현재 언어 문자열. 없으면 영어 → 키 순으로 fallback.</summary>
    public static string Get(string key)
    {
        EnsureLoaded();
        if (_current != null && _current.TryGetValue(key, out string? v))
        {
            return v;
        }
        if (_english.TryGetValue(key, out string? e))
        {
            return e;
        }
        return key;
    }

    /// <summary>현재 게임 언어에 맞는 테이블을 (변경 시) 로드한다. 영어 테이블은 fallback용으로 1회 로드.</summary>
    private static void EnsureLoaded()
    {
        if (!_englishLoaded)
        {
            _english = LoadFile(DefaultLang) ?? new Dictionary<string, string>();
            _englishLoaded = true;
        }

        string lang = LocManager.Instance?.Language ?? DefaultLang;
        if (_loadedLang == lang)
        {
            return; // 언어 안 바뀜 — 재로드 불필요.
        }
        _loadedLang = lang;
        _current = lang == DefaultLang ? _english : LoadFile(lang);
    }

    /// <summary>DLL 옆 localization/{code}.lang 을 읽어 평면 dictionary로. 없거나 오류면 null.</summary>
    private static Dictionary<string, string>? LoadFile(string code)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(dir))
            {
                return null;
            }
            string path = Path.Combine(dir, "localization", code + ".lang");
            if (!File.Exists(path))
            {
                return null;
            }

            var json = new Json();
            if (json.Parse(File.ReadAllText(path)) != Error.Ok)
            {
                Log.Error($"[orb_dmg_indicator] .lang parse error ({code}): {json.GetErrorMessage()} @ line {json.GetErrorLine()}");
                return null;
            }

            var dict = json.Data.AsGodotDictionary();
            var result = new Dictionary<string, string>(dict.Count);
            foreach (Variant k in dict.Keys)
            {
                result[k.AsString()] = dict[k].AsString();
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.Error($"[orb_dmg_indicator] localization load failed ({code}): {ex.Message}");
            return null;
        }
    }
}
