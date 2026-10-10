using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;

namespace SlayTheDivergence.Ui;

/// <summary>
/// Toasts in the top-right corner: they slide in, count down on a thin bar, pause while hovered, close on ×, and fade
/// out, with the ones below sliding up into the gap. An "N resyncs" pill above them opens this run's history.
/// </summary>
/// <remarks>
/// Built from stock Godot controls in code, like the rest of the game's mods: no scene, no custom Node subclass (the
/// game does not register script types from mod assemblies). Labels use the game's theme font.
/// </remarks>
public static class ToastLayer
{
    private const float Width = 360f;
    private const float Margin = 20f;
    private const float Top = 172f; // below the top bar
    private const float Gap = 10f;
    private const int MaxToasts = 4;
    private const int MaxHistoryRows = 30;

    private static CanvasLayer? _layer;
    private static Control? _column;
    private static Button? _pill;
    private static PanelContainer? _historyPanel;
    private static VBoxContainer? _historyRows;
    private static readonly List<Toast> Toasts = new();
    private static bool _subscribed;

    private sealed class Toast
    {
        public required string Key;
        public required PanelContainer Panel;
        public required StyleBoxFlat Style;
        public required Label Title;
        public required Label Subtitle;
        public required Label Details;
        public required ColorRect Bar;
        public Tween? Life;
        public Tween? Move;
        public bool Shown;
        public bool Closing;
        public bool Hovered;
        public int Repeats = 1;
        public string BaseTitle = "";
    }

    public static void Show(string key, NoticeKind kind, string title, string? subtitle, IReadOnlyList<string> details)
    {
        if (!Settings.ShowOnScreenText) return;
        Run(() =>
        {
            var existing = Toasts.FirstOrDefault(t => t.Key == key && !t.Closing);
            if (existing != null)
            {
                existing.Repeats = existing.BaseTitle == title ? existing.Repeats + 1 : 1;
                Fill(existing, kind, title, subtitle, details);
                Pulse(existing);
                StartLife(existing, Duration(kind));
                return;
            }
            var toast = Create(key);
            Fill(toast, kind, title, subtitle, details);
            Toasts.Insert(0, toast);
            while (Toasts.Count > MaxToasts) Dismiss(Toasts[^1]);
            StartLife(toast, Duration(kind));
            Relayout();
        });
    }

    /// <summary>Turns a visible toast into another one in place (say, "drifted" into "back in sync"); no-op if it is gone.</summary>
    public static void Morph(string key, NoticeKind kind, string title, string? subtitle, IReadOnlyList<string> details)
    {
        if (!Settings.ShowOnScreenText) return;
        Run(() =>
        {
            var toast = Toasts.FirstOrDefault(t => t.Key == key && !t.Closing);
            if (toast == null) return;
            toast.Repeats = 1;
            Fill(toast, kind, title, subtitle, details);
            Pulse(toast);
            StartLife(toast, Duration(kind));
        });
    }

    private static float Duration(NoticeKind kind) => kind switch
    {
        NoticeKind.Kicked => 12f,
        NoticeKind.Warning => 9f,
        NoticeKind.Resynced => 7f,
        NoticeKind.Recovered => 3f,
        _ => 5f,
    };

    private static Color Accent(NoticeKind kind) => kind switch
    {
        NoticeKind.Resynced => new Color(0.38f, 0.78f, 0.79f),
        NoticeKind.Repairing => new Color(0.88f, 0.71f, 0.35f),
        NoticeKind.Warning => new Color(0.94f, 0.54f, 0.24f),
        NoticeKind.Kicked => new Color(0.88f, 0.35f, 0.29f),
        NoticeKind.Recovered => new Color(0.42f, 0.75f, 0.44f),
        _ => Colors.White,
    };

    // ── building ─────────────────────────────────────────────────────────

    private static void Run(Action action)
    {
        try
        {
            if (!EnsureLayer()) return;
            action();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not show the resync notice: {e}");
        }
    }

    private static bool EnsureLayer()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer)) return true;
        var game = NGame.Instance;
        if (game == null) return false;

        Toasts.Clear();
        _layer = new CanvasLayer { Name = "SlayTheDivergenceNotices", Layer = 120 };
        var root = new Control { Name = "Root", MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(root);

        _column = new Control { Name = "Column", MouseFilter = Control.MouseFilterEnum.Ignore };
        _column.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _column.OffsetLeft = -(Width + Margin);
        _column.OffsetRight = -Margin;
        _column.OffsetTop = Top;
        _column.OffsetBottom = Top;
        root.AddChild(_column);

        _pill = new Button { Name = "HistoryPill", Visible = false, FocusMode = Control.FocusModeEnum.None };
        _pill.AddThemeStyleboxOverride("normal", Box(new Color(0.07f, 0.08f, 0.11f, 0.88f), new Color(1f, 1f, 1f, 0.25f), 1, 14, 12, 4));
        _pill.AddThemeStyleboxOverride("hover", Box(new Color(0.12f, 0.14f, 0.18f, 0.95f), new Color(1f, 1f, 1f, 0.5f), 1, 14, 12, 4));
        _pill.AddThemeStyleboxOverride("pressed", Box(new Color(0.12f, 0.14f, 0.18f, 0.95f), new Color(1f, 1f, 1f, 0.5f), 1, 14, 12, 4));
        _pill.AddThemeFontSizeOverride("font_size", 15);
        _pill.Pressed += ToggleHistory;
        _pill.Resized += Relayout;
        _column.AddChild(_pill);

        _historyPanel = new PanelContainer { Name = "History", Visible = false, MouseFilter = Control.MouseFilterEnum.Stop, ZIndex = 1 };
        _historyPanel.AddThemeStyleboxOverride("panel", Box(new Color(0.06f, 0.07f, 0.1f, 0.96f), new Color(1f, 1f, 1f, 0.25f), 1, 8, 14, 12));
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(Width - 28, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _historyRows = new VBoxContainer();
        _historyRows.AddThemeConstantOverride("separation", 10);
        _historyRows.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_historyRows);
        _historyPanel.AddChild(scroll);
        _column.AddChild(_historyPanel);

        if (!_subscribed)
        {
            ResyncHistory.Changed += OnHistoryChanged;
            _subscribed = true;
        }
        // Hover pause by polling: the panels ignore the mouse (see Create), so they get no enter/exit signals.
        var hoverPoll = new Timer { Name = "HoverPoll", WaitTime = 0.1, Autostart = true };
        hoverPoll.Timeout += PollHover;
        _layer.AddChild(hoverPoll);
        game.AddChildSafely(_layer);
        OnHistoryChanged();
        return true;
    }

    private static Toast Create(string key)
    {
        var style = Box(new Color(0.06f, 0.07f, 0.1f, 0.94f), Colors.White, 0, 8, 16, 12);
        style.BorderWidthLeft = 4;
        style.ShadowColor = new Color(0, 0, 0, 0.45f);
        style.ShadowSize = 8;
        style.ShadowOffset = new Vector2(0, 3);
        // The countdown bar runs flush along the bottom edge.
        style.ContentMarginBottom = 0;

        // The panel lets the mouse through, so enemies under a toast can still be hovered and targeted; only × takes clicks.
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, 0) };
        panel.AddThemeStyleboxOverride("panel", style);
        panel.Size = new Vector2(Width, 0);

        var body = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        body.AddThemeConstantOverride("separation", 4);
        panel.AddChild(body);

        var header = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var title = NewLabel(20, Colors.White, wrap: true);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        title.CustomMinimumSize = new Vector2(Width - 70, 0);
        header.AddChild(title);
        var close = new Button { Text = "×", Flat = true, FocusMode = Control.FocusModeEnum.None, MouseFilter = Control.MouseFilterEnum.Stop };
        close.AddThemeFontSizeOverride("font_size", 22);
        close.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.55f));
        close.AddThemeColorOverride("font_hover_color", Colors.White);
        close.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        header.AddChild(close);
        body.AddChild(header);

        var subtitle = NewLabel(15, new Color(1, 1, 1, 0.75f), wrap: true);
        body.AddChild(subtitle);
        var details = NewLabel(15, new Color(1, 1, 1, 0.92f), wrap: true);
        body.AddChild(details);

        var spacer = new Control { CustomMinimumSize = new Vector2(0, 6), MouseFilter = Control.MouseFilterEnum.Ignore };
        body.AddChild(spacer);
        var bar = new ColorRect { CustomMinimumSize = new Vector2(0, 3), MouseFilter = Control.MouseFilterEnum.Ignore };
        body.AddChild(bar);

        var toast = new Toast
        {
            Key = key, Panel = panel, Style = style, Title = title, Subtitle = subtitle, Details = details, Bar = bar,
        };
        close.Pressed += () => Dismiss(toast);
        panel.Resized += Relayout;
        _column!.AddChild(panel);
        // Input goes to the last child first (z_index only changes drawing), so the history list stays on top of toasts.
        if (_historyPanel != null) _column.MoveChild(_historyPanel, -1);
        return toast;
    }

    private static void Fill(Toast toast, NoticeKind kind, string title, string? subtitle, IReadOnlyList<string> details)
    {
        var accent = Accent(kind);
        toast.BaseTitle = title;
        toast.Style.BorderColor = accent;
        toast.Title.Text = toast.Repeats > 1 ? $"{title}  ×{toast.Repeats}" : title;
        toast.Title.AddThemeColorOverride("font_color", accent.Lightened(0.25f));
        toast.Subtitle.Text = subtitle ?? "";
        toast.Subtitle.Visible = !string.IsNullOrEmpty(subtitle);
        toast.Details.Text = string.Join("\n", details.Select(d => "• " + d));
        toast.Details.Visible = details.Count > 0;
        toast.Bar.Color = new Color(accent, 0.8f);
        // Shrinking content does not shrink a free-standing container on its own.
        toast.Panel.Size = new Vector2(Width, 0);
    }

    private static Label NewLabel(int size, Color color, bool wrap)
    {
        var label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.6f));
        label.AddThemeConstantOverride("outline_size", 4);
        if (wrap)
        {
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.CustomMinimumSize = new Vector2(Width - 32, 0);
        }
        return label;
    }

    private static StyleBoxFlat Box(Color bg, Color border, int borderWidth, int radius, int padX, int padY)
    {
        var box = new StyleBoxFlat { BgColor = bg, BorderColor = border };
        box.SetBorderWidthAll(borderWidth);
        box.SetCornerRadiusAll(radius);
        box.ContentMarginLeft = box.ContentMarginRight = padX;
        box.ContentMarginTop = box.ContentMarginBottom = padY;
        return box;
    }

    // ── motion ───────────────────────────────────────────────────────────

    private static void PollHover()
    {
        foreach (var toast in Toasts)
        {
            if (toast.Closing || !GodotObject.IsInstanceValid(toast.Panel)) continue;
            bool over = toast.Panel.GetGlobalRect().HasPoint(toast.Panel.GetGlobalMousePosition());
            if (over == toast.Hovered) continue;
            toast.Hovered = over;
            if (over) toast.Life?.Pause();
            else toast.Life?.Play();
        }
    }

    private static void StartLife(Toast toast, float seconds)
    {
        toast.Life?.Kill();
        toast.Bar.PivotOffset = Vector2.Zero;
        toast.Bar.Scale = Vector2.One;
        toast.Life = toast.Panel.CreateTween();
        toast.Life.TweenProperty(toast.Bar, "scale:x", 0f, seconds);
        toast.Life.TweenCallback(Callable.From(() => Dismiss(toast)));
        if (toast.Hovered) toast.Life.Pause();
    }

    private static void Pulse(Toast toast)
    {
        toast.Panel.PivotOffset = toast.Panel.Size / 2;
        var t = toast.Panel.CreateTween();
        t.TweenProperty(toast.Panel, "scale", new Vector2(1.03f, 1.03f), 0.08);
        t.TweenProperty(toast.Panel, "scale", Vector2.One, 0.18).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
    }

    private static void Dismiss(Toast toast)
    {
        if (toast.Closing) return;
        toast.Closing = true;
        toast.Life?.Kill();
        toast.Move?.Kill();
        Toasts.Remove(toast);
        var panel = toast.Panel;
        if (GodotObject.IsInstanceValid(panel))
        {
            var t = panel.CreateTween().SetParallel();
            t.TweenProperty(panel, "modulate:a", 0f, 0.25);
            t.TweenProperty(panel, "position:x", panel.Position.X + 80f, 0.25).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
            t.Chain().TweenCallback(Callable.From(panel.QueueFree));
        }
        Relayout();
    }

    private static void Relayout()
    {
        if (_column == null || !GodotObject.IsInstanceValid(_column)) return;
        float y = 0;
        if (_pill != null && _pill.Visible)
        {
            _pill.Position = new Vector2(Width - _pill.Size.X, 0);
            y = _pill.Size.Y + Gap;
            if (_historyPanel != null && _historyPanel.Visible)
            {
                _historyPanel.Position = new Vector2(0, y);
            }
        }
        foreach (var toast in Toasts)
        {
            var panel = toast.Panel;
            if (!GodotObject.IsInstanceValid(panel)) continue;
            if (!toast.Shown)
            {
                toast.Shown = true;
                panel.Position = new Vector2(Width + 60f, y);
                toast.Move = panel.CreateTween().SetParallel();
                toast.Move.TweenProperty(panel, "position:x", 0f, 0.4).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
                toast.Move.TweenProperty(panel, "modulate:a", 1f, 0.25);
            }
            else if (!Mathf.IsEqualApprox(panel.Position.Y, y))
            {
                toast.Move?.Kill();
                toast.Move = panel.CreateTween().SetParallel();
                toast.Move.TweenProperty(panel, "position", new Vector2(0, y), 0.25).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                toast.Move.TweenProperty(panel, "modulate:a", 1f, 0.15);
            }
            y += panel.Size.Y + Gap;
        }
    }

    // ── history ──────────────────────────────────────────────────────────

    private static void OnHistoryChanged()
    {
        if (_pill == null || !GodotObject.IsInstanceValid(_pill)) return;
        int n = ResyncHistory.All.Count;
        _pill.Visible = n > 0;
        _pill.Text = n == 1 ? "1 resync" : $"{n} resyncs";
        if (n == 0 && _historyPanel != null) _historyPanel.Visible = false;
        if (_historyPanel != null && _historyPanel.Visible) FillHistory();
        Relayout();
    }

    /// <summary>The run is over: close the history and hide the pill until the next notice (the entries stay until the next run).</summary>
    public static void HideForMenu()
    {
        // Not through Run: nothing to hide if no notice was ever shown, and no reason to build the layer for it.
        if (_layer == null || !GodotObject.IsInstanceValid(_layer)) return;
        try
        {
            if (_historyPanel != null) _historyPanel.Visible = false;
            if (_pill != null) _pill.Visible = false;
            Relayout();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not hide the resync notices: {e.Message}");
        }
    }

    /// <summary>Opens or closes the history list, as clicking the pill does.</summary>
    public static void SetHistoryOpen(bool open) => Run(() =>
    {
        if (_historyPanel == null || _historyPanel.Visible == open) return;
        ToggleHistory();
    });

    private static void ToggleHistory()
    {
        if (_historyPanel == null) return;
        _historyPanel.Visible = !_historyPanel.Visible;
        if (_historyPanel.Visible) FillHistory();
        Relayout();
    }

    private static void FillHistory()
    {
        if (_historyRows == null || _historyPanel == null) return;
        foreach (var child in _historyRows.GetChildren()) child.QueueFree();
        // The newest few: the full list is in the log and in "divergence history".
        foreach (var entry in ResyncHistory.All.Reverse().Take(MaxHistoryRows))
        {
            var row = new VBoxContainer();
            row.AddThemeConstantOverride("separation", 1);
            var head = NewLabel(15, Accent(entry.Kind).Lightened(0.25f), wrap: true);
            head.CustomMinimumSize = new Vector2(Width - 40, 0);
            head.Text = $"{entry.At:HH:mm}  {entry.Title}" + (entry.Outcome != null ? $"  ({entry.Outcome})" : "");
            row.AddChild(head);
            foreach (var line in entry.Lines)
            {
                var l = NewLabel(13, new Color(1, 1, 1, 0.8f), wrap: true);
                l.CustomMinimumSize = new Vector2(Width - 40, 0);
                l.Text = "   " + line;
                row.AddChild(l);
            }
            _historyRows.AddChild(row);
        }
        var scroll = (ScrollContainer)_historyRows.GetParent();
        scroll.CustomMinimumSize = new Vector2(Width - 28, Mathf.Min(360f, 30f + 46f * ResyncHistory.All.Count));
        _historyPanel.Size = new Vector2(Width, 0);
    }
}
