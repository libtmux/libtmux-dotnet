using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;

namespace LibTmux.Workspace.Cli;

internal sealed record ProgressOptions(string Format, int Lines)
{
    internal const string DefaultFormat = "default";
    internal const int DefaultLines = 3;
    internal const string FormatEnvironment = "TMUXP_PROGRESS_FORMAT";
    internal const string LinesEnvironment = "TMUXP_PROGRESS_LINES";
    internal const string EnabledEnvironment = "TMUXP_PROGRESS";

    internal static ProgressOptions? Resolve(Invocation invocation, IReadOnlyDictionary<string, string?> environment, bool terminal)
    {
        if (!terminal || invocation.Machine || invocation.Flag("no_progress") || environment.GetValueOrDefault(EnabledEnvironment) == "0" || environment.GetValueOrDefault("TERM") == "dumb") return null;
        int lines = DefaultLines;
        if (invocation.Values.GetValueOrDefault("progress_lines") is int selected) lines = selected;
        else if (environment.GetValueOrDefault(LinesEnvironment) is string supplied && (!int.TryParse(supplied, NumberStyles.Integer, CultureInfo.InvariantCulture, out lines) || lines < -1))
            throw new CliException("invalid-progress-lines", "TMUXP_PROGRESS_LINES must be -1 or greater.", 2);
        return new(invocation.Text("progress_format") ?? environment.GetValueOrDefault(FormatEnvironment) ?? DefaultFormat, lines);
    }
}

internal sealed partial class ProgressDisplay : IDisposable
{
    private static readonly Dictionary<string, string> Presets = new(StringComparer.Ordinal)
    {
        ["default"] = "Loading workspace: {session} {bar} {progress} {window}",
        ["minimal"] = "Loading workspace: {session} [{window_progress}]",
        ["window"] = "Loading workspace: {session} {window_bar} {window_progress_rel}",
        ["pane"] = "Loading workspace: {session} {pane_bar} {session_pane_progress}",
        ["verbose"] = "Loading workspace: {session} [window {window_index} of {window_total} · pane {pane_index} of {pane_total}] {window}"
    };
    private readonly ProgressOptions _options;
    private readonly (int Width, int Height) _size;
    private readonly Func<(int Width, int Height)?>? _readSize;
    private readonly StringWriter _frame = new(CultureInfo.InvariantCulture);
    private readonly IAnsiConsole _console;
    private readonly ScriptTail _tail = new();
    private WorkspacePlan? _workspace;
    private WindowPlan? _window;
    private string _session = "";
    private int _windowIndex, _windowsDone, _paneIndex, _panesDone, _totalPanes, _completedPanes;
    private bool _opaque;

    internal ProgressDisplay(ProgressOptions options, int width, int height, bool color, Func<(int Width, int Height)?>? readSize = null)
    {
        _options = options;
        _size = (width, height);
        _readSize = readSize;
        _console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = color ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = ColorSystemSupport.Standard,
            Out = new FrameOutput(_frame, Math.Max(1, width - 1), height)
        });
    }

    internal int Rows { get; set; }
    internal bool Panel => _options.Lines != 0;
    internal bool WorkComplete => !_opaque && _totalPanes > 0 && _completedPanes == _totalPanes;
    internal bool SizeUnchanged => _readSize is null || _readSize() == _size;
    internal string ClearSequence => string.Concat(Enumerable.Repeat("\u001b[1A\r\u001b[2K", Rows));

    internal static (int Width, int Height)? ErrorSize()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return null;
        const nuint TerminalWindowSize = 0x5413;
        return ReadWindowSize(2, TerminalWindowSize, out WindowSize size) == 0 && size.Columns >= 2 && size.Rows >= 2 ? (size.Columns, size.Rows) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowSize { internal ushort Rows, Columns, PixelWidth, PixelHeight; }

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ReadWindowSize(int descriptor, nuint request, out WindowSize size);

    internal void StartWorkspace(WorkspacePlan plan, string? session = null)
    {
        _workspace = plan;
        _window = null;
        _session = session ?? plan.Name;
        _windowIndex = _windowsDone = _paneIndex = _panesDone = _completedPanes = 0;
        _totalPanes = plan.Windows.Sum(window => window.Panes.Length);
        _opaque = false;
        _tail.Reset();
    }

    internal void StartWindow(WindowPlan window, int ordinal) { _window = window; _windowIndex = ordinal; _paneIndex = _panesDone = 0; }
    internal void StartPane(int ordinal) => _paneIndex = ordinal;
    internal void CompletePane() { _panesDone++; _completedPanes++; }
    internal void CompleteWindow() => _windowsDone++;
    internal void StartBridge() { _opaque = true; _tail.Reset(); }
    internal void Script(string channel, string text) => _tail.Append(channel == "stdout" ? 0 : 1, text);
    internal void Reset() => _tail.Reset();

    internal string Format()
    {
        if (_opaque) return "Loading Python workspace extensions";
        int windows = _workspace?.Windows.Length ?? 0;
        int panes = _window?.Panes.Length ?? 0;
        string windowProgress = _windowIndex > 0 ? Fraction(_windowIndex, windows) : "";
        string paneProgress = _paneIndex > 0 ? Fraction(_paneIndex, panes) : "";
        Dictionary<string, object> fields = new(StringComparer.Ordinal)
        {
            ["workspace_path"] = _workspace?.Source ?? "",
            ["session"] = _session,
            ["window"] = _window?.Name ?? "",
            ["window_index"] = _windowIndex,
            ["window_total"] = windows,
            ["window_progress"] = windowProgress,
            ["pane_index"] = _paneIndex,
            ["pane_total"] = panes,
            ["pane_progress"] = paneProgress,
            ["progress"] = string.Join(" · ", new[] { windowProgress.Length > 0 ? windowProgress + " win" : "", paneProgress.Length > 0 ? paneProgress + " pane" : "" }.Where(value => value.Length > 0)),
            ["windows_done"] = _windowsDone,
            ["windows_remaining"] = Math.Max(0, windows - _windowsDone),
            ["window_progress_rel"] = Fraction(_windowsDone, windows),
            ["pane_done"] = _panesDone,
            ["pane_remaining"] = Math.Max(0, panes - _panesDone),
            ["pane_progress_rel"] = Fraction(_panesDone, panes),
            ["session_pane_total"] = _totalPanes,
            ["session_panes_done"] = _completedPanes,
            ["session_panes_remaining"] = Math.Max(0, _totalPanes - _completedPanes),
            ["session_pane_progress"] = Fraction(_completedPanes, _totalPanes),
            ["overall_percent"] = _totalPanes > 0 ? _completedPanes * 100 / _totalPanes : 0,
            ["summary"] = $"[{_windowsDone} win, {_completedPanes} panes]",
            ["bar"] = Bar(_completedPanes, _totalPanes),
            ["pane_bar"] = Bar(_completedPanes, _totalPanes),
            ["window_bar"] = Bar(_windowsDone, windows),
            ["status_icon"] = ""
        };
        string template = Presets.GetValueOrDefault(_options.Format, _options.Format);
        StringBuilder result = new();
        for (int index = 0; index < template.Length; index++)
        {
            char character = template[index];
            if (character is '{' or '}' && index + 1 < template.Length && template[index + 1] == character) { result.Append(character); index++; }
            else if (character == '{' && template.IndexOf('}', index + 1) is int end && end > index)
            {
                string name = template[(index + 1)..end];
                result.Append(fields.TryGetValue(name, out object? value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : template[index..(end + 1)]);
                index = end;
            }
            else result.Append(character);
        }
        return result.ToString();
    }

    private static string Fraction(int done, int total) => total > 0 ? $"{done}/{total}" : "";
    private static string Bar(int done, int total)
    {
        if (total <= 0) return "";
        int filled = Math.Clamp(done * 10 / total, 0, 10);
        return new string('█', filled) + new string('░', 10 - filled);
    }

    internal string Render()
    {
        _frame.GetStringBuilder().Clear();
        int available = Math.Max(0, _size.Height - 2);
        int count = _options.Lines < 0 ? available : Math.Min(_options.Lines, available);
        IEnumerable<string> lines = new[] { Format() }.Concat(_tail.Visible(count));
        _console.Write(new Rows(lines.Select(line => new Text(Clip(line), new Style(Color.Cyan)))));
        return _frame.ToString().Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    private string Clip(string text)
    {
        string safe = string.Concat(text.Select(character => char.IsControl(character) ? $"\\u{(int)character:x4}" : character.ToString()));
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(safe);
        StringBuilder clipped = new();
        int cells = 0;
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            cells += element.GetCellWidth();
            if (cells > Math.Max(1, _size.Width - 1)) break;
            clipped.Append(element);
        }
        return clipped.ToString();
    }

    public void Dispose() => _frame.Dispose();

    private sealed class FrameOutput(TextWriter writer, int width, int height) : IAnsiConsoleOutput
    {
        public TextWriter Writer => writer;
        public bool IsTerminal => true;
        public int Width => width;
        public int Height => height;
        public void SetEncoding(Encoding encoding) { }
    }

    private sealed class ScriptTail
    {
        private const int LineLimit = 200;
        private const int CharacterLimit = 65536;
        private const int FragmentLimit = 4096;
        private readonly Queue<(long Order, string Text)> _lines = new();
        private readonly StreamTail[] _streams = [new(), new()];
        private int _characters;
        private long _order;

        internal void Append(int channel, string text)
        {
            StreamTail stream = _streams[channel];
            foreach (char character in text)
            {
                if (stream.AfterCarriageReturn) { stream.AfterCarriageReturn = false; if (character == '\n') continue; }
                if (character is '\r' or '\n')
                {
                    string line = KeepEnd(stream.Text.ToString());
                    _lines.Enqueue((++_order, line));
                    _characters += line.Length;
                    stream.Text.Clear();
                    stream.AfterCarriageReturn = character == '\r';
                    while (_lines.Count > LineLimit || _characters > CharacterLimit) _characters -= _lines.Dequeue().Text.Length;
                }
                else { stream.Text.Append(character); stream.Order = ++_order; }
            }
            if (stream.Text.Length > FragmentLimit) { string retained = KeepEnd(stream.Text.ToString()); stream.Text.Clear().Append(retained); }
        }

        internal IEnumerable<string> Visible(int count) => _lines.Concat(_streams.Where(stream => stream.Text.Length > 0).Select(stream => (stream.Order, stream.Text.ToString()))).OrderBy(line => line.Item1).TakeLast(count).Select(line => line.Item2);
        internal void Reset()
        {
            _lines.Clear();
            _characters = 0;
            _order = 0;
            foreach (StreamTail stream in _streams) { stream.Text.Clear(); stream.AfterCarriageReturn = false; stream.Order = 0; }
        }

        private static string KeepEnd(string text)
        {
            if (text.Length <= FragmentLimit) return text;
            TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext()) if (elements.ElementIndex >= text.Length - FragmentLimit) return text[elements.ElementIndex..];
            return "";
        }

        private sealed class StreamTail
        {
            internal StringBuilder Text { get; } = new();
            internal bool AfterCarriageReturn { get; set; }
            internal long Order { get; set; }
        }
    }
}
