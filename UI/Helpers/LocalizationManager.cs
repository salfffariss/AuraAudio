using System;
using System.Linq;
using System.Windows;

namespace AuraAudio.UI.Helpers;

/// <summary>
/// Pengelola Lokalisasi Bahasa Dinamis (Dynamic Multi-Language Manager).
/// Mendukung pergantian bahasa instan (ID / EN) secara real-time tanpa me-restart aplikasi.
/// </summary>
public static class LocalizationManager
{
    private static string _currentLanguage = "id";
    public static string CurrentLanguage => _currentLanguage;

    public static event Action? LanguageChanged;

    /// <summary>
    /// Mengganti bahasa aplikasi saat runtime dengan menukar ResourceDictionary teks.
    /// </summary>
    /// <param name="langCode">Kode bahasa ('id' atau 'en')</param>
    public static void SetLanguage(string langCode)
    {
        if (string.IsNullOrWhiteSpace(langCode)) langCode = "id";
        langCode = langCode.Trim().ToLowerInvariant();
        if (langCode != "id" && langCode != "en") langCode = "id";

        try
        {
            var app = Application.Current;
            if (app == null) return;

            var mergedDicts = app.Resources.MergedDictionaries;

            // Cari kamus bahasa yang sedang aktif
            var existingDict = mergedDicts.FirstOrDefault(d => 
                d.Source != null && d.Source.OriginalString.Contains("/UI/Resources/Strings."));

            var newDict = new ResourceDictionary
            {
                Source = new Uri($"/UI/Resources/Strings.{langCode}.xaml", UriKind.RelativeOrAbsolute)
            };

            if (existingDict != null)
            {
                int idx = mergedDicts.IndexOf(existingDict);
                mergedDicts[idx] = newDict;
            }
            else
            {
                mergedDicts.Add(newDict);
            }

            _currentLanguage = langCode;
            LanguageChanged?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalizationManager Error] Gagal mengganti bahasa ke {langCode}: {ex.Message}");
        }
    }

    /// <summary>
    /// Mengambil nilai string terjemahan dari ResourceDictionary berdasarkan kuncinya.
    /// </summary>
    public static string GetString(string key, string fallback = "")
    {
        try
        {
            if (Application.Current != null && Application.Current.TryFindResource(key) is string val)
            {
                return val;
            }
        }
        catch
        {
            // fallback
        }
        return string.IsNullOrEmpty(fallback) ? key : fallback;
    }
}
