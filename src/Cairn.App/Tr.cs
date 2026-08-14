using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Cairn.Core;

namespace Cairn.App;

/// <summary>
/// One translated string, as something a binding can watch.
///
/// A plain property rather than an indexer on a shared table. An indexer is the tidier
/// shape and it does not work: Avalonia resolves <c>[key]</c> against a non-collection
/// object once and does not re-read it for an <c>Item[]</c> notification, so the language
/// changed and every label already on screen kept its old text. A property named Value is
/// the boring thing that binds the way bindings are documented to.
///
/// One instance per key, shared by every binding that asks for it, rather than one per
/// binding. The pack list and the mod config rows sit inside DataTemplates, which are
/// realised and thrown away as somebody scrolls — an object per binding would subscribe a
/// new handler to a static event on every realised row and never let it go. Per key, the
/// set is bounded by the number of strings in the application and nothing subscribes at all:
/// one static handler walks the cache.
/// </summary>
public sealed class TrKey : INotifyPropertyChanged
{
    private static readonly Dictionary<string, TrKey> Cache = new(StringComparer.Ordinal);

    static TrKey()
    {
        Lang.Changed += (_, _) =>
        {
            TrKey[] all;
            lock (Cache) all = [.. Cache.Values];

            foreach (var entry in all)
                entry.PropertyChanged?.Invoke(entry, new PropertyChangedEventArgs(nameof(Value)));
        };
    }

    private TrKey(string key) => Key = key;

    public static TrKey For(string key)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var entry)) Cache[key] = entry = new TrKey(key);
            return entry;
        }
    }

    public string Key { get; }

    public string Value => Lang.Get(Key);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// <c>Text="{l:Tr tab-modconfig}"</c>, which is where a translated string comes from in the
/// markup.
///
/// The keys are written out in the XAML rather than generated into constants. A generated
/// accessor would catch a typo at build time, which is the usual argument for one — but it
/// would also mean the file a translator opens and the file a developer edits share no
/// vocabulary, and <c>LanguageCoverageTests</c> catches the same typo without that cost.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(TrKey.Value))
        {
            Source = TrKey.For(Key),
            Mode = BindingMode.OneWay,
        };
}
