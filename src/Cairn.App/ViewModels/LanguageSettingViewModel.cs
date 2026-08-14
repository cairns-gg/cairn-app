using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Cairn.Core;

namespace Cairn.App.ViewModels;

/// <summary>
/// Which language the interface speaks, as the row in Preferences.
///
/// Its own type rather than three more properties on <see cref="PreferencesViewModel"/>,
/// which needs a pack store, a game store, a runtime store and two caches to exist. This
/// needs none of them — it reads a setting and calls <see cref="Lang"/> — and a setting that
/// can only be tested by first building five things it never touches is a setting nobody
/// writes a test for.
/// </summary>
public partial class LanguageSettingViewModel : ViewModelBase
{
    /// <summary>
    /// The languages this build ships, with "work it out" first.
    ///
    /// Named in their own language rather than in the current one. Somebody who has ended up
    /// in a language they cannot read has to be able to find their way back out, and
    /// "Deutsch" is findable from any starting point in a way "German" is not.
    /// </summary>
    public IReadOnlyList<string> Choices { get; } =
        [Lang.Get("prefs-language-automatic"), .. Lang.Available.Select(NativeName)];

    /// <summary>
    /// Out of the catalog rather than out of CultureInfo, which cannot answer here: the
    /// repository builds with InvariantGlobalization, so there is no ICU and every culture
    /// reports its own tag as its name. Each lang file names itself instead.
    /// </summary>
    private static string NativeName(string code) =>
        LanguageCatalog.Load(code, LanguageChoice.OverrideDir).Name;

    /// <summary>
    /// Applied as it is picked, like the interface scale: every label binds through
    /// <see cref="TrKey"/>, so the window somebody is looking at answers immediately and
    /// there is nothing to restart.
    /// </summary>
    [ObservableProperty] public partial string Selected { get; set; } = Chosen();

    private static string Chosen()
    {
        var (code, source) = LanguageChoice.Resolve(CairnSettings.Load().Language);

        return source == LanguageSource.Chosen
            ? NativeName(code)
            : Lang.Get("prefs-language-automatic");
    }

    /// <summary>
    /// What Automatic worked out, so the row is not a shrug.
    ///
    /// It names which of the two it followed, because "Cairn guessed from your system" and
    /// "your game is set to this" are different claims and only one of them is worth acting
    /// on. And it says when CAIRN_LANG is in force, rather than showing a choice that is not.
    /// </summary>
    public string Note
    {
        get
        {
            var (code, source) = LanguageChoice.Resolve(CairnSettings.Load().Language);

            return source switch
            {
                LanguageSource.Environment =>
                    Lang.Get("prefs-language-from-env", LanguageChoice.EnvironmentVariable, code),
                LanguageSource.Game => Lang.Get("prefs-language-from-game", NativeName(code)),
                LanguageSource.System => Lang.Get("prefs-language-from-system", NativeName(code)),
                _ => "",
            };
        }
    }

    partial void OnSelectedChanged(string value)
    {
        var automatic = value == Lang.Get("prefs-language-automatic");
        var code = automatic ? null : Lang.Available.FirstOrDefault(c => NativeName(c) == value);

        // A name that matches nothing is a list that has moved under us, not a choice.
        if (!automatic && code is null) return;

        // Through Update, so choosing a language cannot erase the interface scale — which is
        // the bug that kept this row from existing at all. See CairnSettings.
        CairnSettings.Update(s => s.Language = code);

        var (resolved, _) = LanguageChoice.Resolve(code);
        Lang.Use(resolved, LanguageChoice.OverrideDir);

        OnPropertyChanged(nameof(Note));
    }
}
