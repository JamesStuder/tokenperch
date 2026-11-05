using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Media;
using ClaudeTray.Core;

namespace ClaudeTray;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action<object?> run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run(parameter);
}

public static class Palette
{
    public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#30A46C"));
    public static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#F5A524"));
    public static readonly IBrush Red = new SolidColorBrush(Color.Parse("#E5484D"));
    public static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#3E9BEA"));

    public static IBrush Level(double percent) => percent >= 90 ? Red : percent >= 70 ? Orange : Green;
    public static Color LevelColor(double percent) => ((SolidColorBrush)Level(percent)).Color;
}

public sealed record LimitRow(string Label, string Percent, double Value, IBrush Brush, bool High, string ResetIn, string Pace, bool PaceWarn);
public sealed record DayRow(string Name, string Total, double Value, bool Today);
public sealed record ModelRow(string Name, string Total, double Value, string Split);

public sealed record AgentModelRow(string Role, string Model, string Where);

/// <summary>A tab at the top of the panel: Claude Code, or another agent (OpenCode, Hermes).</summary>
public sealed class AgentTabVM(string name, Agent? agent, ICommand select) : ObservableObject
{
    bool _selected;
    public string Name { get; } = name;
    /// <summary>Null for the Claude Code tab.</summary>
    public Agent? Agent { get; } = agent;
    public ICommand Select { get; } = select;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>One of another agent's sessions; a click opens it again in a terminal.</summary>
public sealed class AgentSessionVM(AgentSession s, string glyph, IBrush brush, ICommand open) : ObservableObject
{
    public AgentSession Info { get; } = s;
    public string Glyph { get; } = glyph;
    public IBrush GlyphBrush { get; } = brush;
    public string Name => Info.Title;
    public ICommand Open { get; } = open;
    public string Ago => Format.Ago(Info.Updated, DateTimeOffset.Now);
    public string Text => string.Join(" · ", new[]
    {
        Info.Model,
        Info.Tokens > 0 ? Format.Human(Info.Tokens) + " tokens" : "",
        ShortDir(Info.Directory),
    }.Where(x => x.Length > 0));

    static string ShortDir(string d) =>
        d.Length > 0 && d.StartsWith(Paths.Home, StringComparison.Ordinal) ? "~" + d[Paths.Home.Length..] : d;

    public void Tick() => Raise(nameof(Ago));
}

public sealed class ModelChoiceVM(ModelChoice choice, bool current, ICommand pick) : ObservableObject
{
    public ModelChoice Choice { get; } = choice;
    public string Name => Choice.Name;
    public bool Current { get; } = current;
    public ICommand Pick { get; } = pick;
    public FontWeight Weight => Current ? FontWeight.Bold : FontWeight.Normal;
}

public sealed class SessionVM : ObservableObject
{
    readonly MainViewModel _owner;
    bool _confirming;

    public SessionVM(SessionInfo info, MainViewModel owner, string glyph, IBrush glyphBrush)
    {
        Info = info;
        Glyph = glyph;
        GlyphBrush = glyphBrush;
        _owner = owner;
        Open = new RelayCommand(_ => owner.OpenSession(this));
        Remove = new RelayCommand(_ => ClickRemove());
    }

    public SessionInfo Info { get; }
    public string Glyph { get; }
    public IBrush GlyphBrush { get; }
    public string Name => Info.Name;
    public string Text => Info.Text;
    public bool HasText => Info.Text.Length > 0;
    public FontWeight NameWeight => Info.Group == SessionGroup.Needs ? FontWeight.Bold : FontWeight.Normal;
    public string Ago => (Info.Open ? "open · " : "") + Format.Ago(Info.Updated, DateTimeOffset.Now);
    public ICommand Open { get; }
    public ICommand Remove { get; }

    public bool Confirming { get => _confirming; private set { if (Set(ref _confirming, value)) { Raise(nameof(RemoveText)); Raise(nameof(RemoveTip)); } } }
    public string RemoveText => Confirming ? "Remove" : "✕";
    public string RemoveTip => Confirming
        ? (Info.Group == SessionGroup.Working ? "Click again: stop and remove" : "Click again to remove")
        : "Remove from list";

    void ClickRemove()
    {
        if (Confirming) { _owner.RemoveSession(this); return; }
        Confirming = true;
        _ = Task.Delay(4000).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => Confirming = false));
    }

    public void Tick() => Raise(nameof(Ago));
}

public sealed class SessionGroupVM : ObservableObject
{
    bool _showAll;
    List<SessionVM> _all = [];

    public SessionGroupVM(SessionGroup group, string title, string glyph, IBrush brush)
    {
        Group = group;
        Title = title;
        Glyph = glyph;
        Brush = brush;
        ToggleAll = new RelayCommand(_ => { _showAll = !_showAll; Refresh(); });
    }

    public SessionGroup Group { get; }
    public string Title { get; }
    public string Glyph { get; }
    public IBrush Brush { get; }
    public ObservableCollection<SessionVM> Items { get; } = [];
    public ICommand ToggleAll { get; }

    public string Header => $"{Title}  ·  {_all.Count}";
    public bool Visible => _all.Count > 0;
    bool Capped => Group == SessionGroup.Done && _all.Count > 10;
    public bool ShowToggle => Capped;
    public string ToggleText => _showAll ? "Show fewer" : $"Show all {_all.Count}";

    public void SetAll(List<SessionVM> all)
    {
        _all = all;
        Refresh();
    }

    public void ResetShowAll() { if (_showAll) { _showAll = false; Refresh(); } }

    void Refresh()
    {
        Items.Clear();
        foreach (var s in Capped && !_showAll ? _all.Take(10) : _all) Items.Add(s);
        Raise(nameof(Header)); Raise(nameof(Visible)); Raise(nameof(ShowToggle)); Raise(nameof(ToggleText));
    }
}
