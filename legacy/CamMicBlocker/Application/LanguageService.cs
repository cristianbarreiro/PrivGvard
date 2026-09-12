using System.Windows;
using CamMicBlocker.Domain.Interfaces;
using Serilog;

namespace CamMicBlocker.Application;

public sealed class LanguageService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LanguageService>();

    private readonly IStateStore _stateStore;
    private string _currentLanguage = "es";

    public event Action<string>? LanguageChanged;

    public string CurrentLanguage => _currentLanguage;

    public LanguageService(IStateStore stateStore)
    {
        _stateStore = stateStore;
    }

    public void Initialize()
    {
        var state = _stateStore.Load();
        var initialLang = string.IsNullOrWhiteSpace(state.Language) ? "es" : state.Language.ToLowerInvariant();
        SetLanguage(initialLang, saveState: false);
    }

    public void SetLanguage(string langCode, bool saveState = true)
    {
        langCode = langCode.ToLowerInvariant() switch
        {
            "en" => "en",
            _ => "es"
        };

        Log.Information("Switching application language to: {LangCode}", langCode);
        _currentLanguage = langCode;

        try
        {
            var asmName = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name;
            var dictUri = new Uri($"/{asmName};component/UI/Resources/Localization/Strings.{langCode}.xaml", UriKind.Relative);
            var newDict = new ResourceDictionary { Source = dictUri };

            var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
            
            // Remove any previously added localization dictionaries
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                if (merged[i].Source != null && merged[i].Source.OriginalString.Contains("/Localization/Strings."))
                {
                    merged.RemoveAt(i);
                }
            }

            merged.Add(newDict);

            if (saveState)
            {
                var state = _stateStore.Load();
                state.Language = langCode;
                _stateStore.Save(state);
            }

            LanguageChanged?.Invoke(langCode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load language dictionary for {LangCode}", langCode);
        }
    }

    public string GetString(string key, string fallback = "")
    {
        try
        {
            if (System.Windows.Application.Current.Resources.Contains(key))
            {
                return System.Windows.Application.Current.Resources[key] as string ?? fallback;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to resolve localized string key: {Key}", key);
        }

        return fallback;
    }
}
