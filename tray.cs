using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class Program {
    [STAThread]
    static int Main(string[] args) {
        if (args.Length == 2 && args[0] == "--describe") {
            var text = Encoding.UTF8.GetBytes(String.Join("\n", Tray.Describe(Account.Parse(File.ReadAllText(args[1])))));
            using (var output = Console.OpenStandardOutput()) output.Write(text, 0, text.Length);
            return 0;
        }
        bool created;
        using (var single = new Mutex(true, "ClaudeTransplantTray", out created)) {
            if (!created) return 0;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Tray(Config.Load()));
        }
        return 0;
    }
}

static class Json {
    static readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    public static object Parse(string text) {
        try { return serializer.DeserializeObject(text); } catch { return null; }
    }
    public static string Text(IDictionary<string, object> row, string key) {
        object value;
        return row != null && row.TryGetValue(key, out value) && value is string ? (string)value : null;
    }
    public static bool Flag(IDictionary<string, object> row, string key) {
        object value;
        return row != null && row.TryGetValue(key, out value) && value is bool && (bool)value;
    }
    public static int? Number(IDictionary<string, object> row, string key) {
        object value;
        if (row == null || !row.TryGetValue(key, out value) || value == null) return null;
        if (value is int) return (int)value;
        if (value is decimal) return (int)(decimal)value;
        if (value is double) return (int)(double)value;
        return null;
    }
    public static IDictionary<string, object> Row(IDictionary<string, object> row, string key) {
        object value;
        return row != null && row.TryGetValue(key, out value) ? value as IDictionary<string, object> : null;
    }
    public static IEnumerable<object> List(IDictionary<string, object> row, string key) {
        object value;
        return row != null && row.TryGetValue(key, out value) && value is object[] ? (object[])value : new object[0];
    }
}

class Config {
    public string Node;
    public string Script;
    public string[] NodeArgs;
    public static Config Load() {
        var row = Json.Parse(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tray.json"))) as IDictionary<string, object>;
        return new Config {
            Node = Json.Text(row, "node"),
            Script = Json.Text(row, "script"),
            NodeArgs = Json.List(row, "nodeArgs").OfType<string>().ToArray()
        };
    }
}

class Account {
    public string Selector;
    public string Label;
    public int? Sessions;
    public bool Active;
    public bool SignedIn;
    public string Pending;
    public string PendingAction;
    public static List<Account> Parse(string text) {
        var rows = Json.Parse(text) as object[];
        if (rows == null) return null;
        return rows.OfType<IDictionary<string, object>>().Select(row => new Account {
            Selector = Json.Text(row, "account") + " " + Json.Text(row, "org"),
            Label = Json.Text(row, "label") ?? Json.Text(row, "email") ?? Json.Text(row, "account"),
            Sessions = Json.Number(row, "sessions"),
            Active = Json.Flag(row, "active"),
            SignedIn = Json.Flag(row, "signedIn"),
            Pending = Json.Text(row, "pending"),
            PendingAction = Json.Text(row, "pendingAction")
        }).ToList();
    }
    public string Summary {
        get {
            var parts = new List<string> { Label };
            if (Sessions.HasValue) parts.Add(Sessions.Value + (Sessions.Value == 1 ? " session" : " sessions"));
            if (Active) parts.Add("signed in");
            return String.Join(" · ", parts);
        }
    }
    public string PendingSummary {
        get {
            if (Pending == null) return null;
            var work = Pending == "undo" ? "Undo waiting" : Pending == "cloud" ? "cloud cleanup waiting" : "move waiting";
            var action = PendingAction == "finish" ? "press Finish" : PendingAction == "restart" ? "Finish restarts Claude" : PendingAction == "sign-in" ? "sign Claude into this account" : PendingAction == "blocked" ? "needs attention" : "retrying automatically";
            return work + ", " + action;
        }
    }
}

class Tray : ApplicationContext {
    readonly Config config;
    readonly NotifyIcon icon = new NotifyIcon();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly Control ui = new Control();
    List<Account> accounts = new List<Account>();
    string status = "Loading accounts";
    bool running;
    bool approvalAttempted;
    string[] operationArgs;
    IDictionary<string, object> plan;
    IDictionary<string, object> done;
    IDictionary<string, object> failure;

    public Tray(Config config) {
        this.config = config;
        ui.CreateControl();
        icon.Icon = Glyph();
        icon.Text = "Claude Transplant";
        icon.ContextMenuStrip = menu;
        icon.Visible = true;
        icon.MouseUp += (sender, e) => { if (e.Button == MouseButtons.Left) ShowMenu(); };
        menu.Opening += (sender, e) => Build();
        Build();
        Refresh();
    }

    static Icon Glyph() {
        using (var bitmap = new Bitmap(32, 32))
        using (var canvas = Graphics.FromImage(bitmap))
        using (var disc = new SolidBrush(Color.FromArgb(37, 99, 235)))
        using (var arrow = new Pen(Color.White, 3.2f) { EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor }) {
            canvas.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            canvas.Clear(Color.Transparent);
            canvas.FillEllipse(disc, 1, 1, 30, 30);
            canvas.DrawLine(arrow, 8, 12, 23, 12);
            canvas.DrawLine(arrow, 24, 20, 9, 20);
            return Icon.FromHandle(bitmap.GetHicon());
        }
    }

    public static IEnumerable<string> Describe(List<Account> accounts) {
        if (accounts == null) return new[] { "Accounts could not be read" };
        if (accounts.Count == 0) return new[] { "No Claude Desktop accounts found" };
        return accounts.SelectMany(account => account.PendingSummary == null ? new[] { account.Summary } : new[] { account.Summary, "  " + account.PendingSummary });
    }

    void ShowMenu() {
        var show = typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (show != null) show.Invoke(icon, null);
    }

    void Build() {
        menu.Items.Clear();
        var title = new ToolStripMenuItem("Claude Transplant") { Enabled = false };
        title.Font = new Font(title.Font, FontStyle.Bold);
        menu.Items.Add(title);
        menu.Items.Add(new ToolStripMenuItem(status) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        foreach (var line in Describe(accounts)) menu.Items.Add(new ToolStripMenuItem(line) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        var pending = accounts != null && accounts.Any(account => account.Pending != null);
        Add("Move sessions…", !running && accounts != null && accounts.Count > 1, ChooseMove);
        Add("Finish", !running && pending, () => Start(new[] { "finish", "--json" }));
        Add("Keep local", !running && pending, () => Start(new[] { "keep-local", "--json" }));
        Add("Undo last move", !running, ConfirmUndo);
        Add("Refresh", !running, Refresh);
        menu.Items.Add(new ToolStripSeparator());
        Add("Quit", true, Quit);
    }

    void Add(string text, bool enabled, Action action) {
        var item = new ToolStripMenuItem(text) { Enabled = enabled };
        item.Click += (sender, e) => action();
        menu.Items.Add(item);
    }

    void Show(string text) {
        status = text;
        icon.Text = text.Length > 63 ? text.Substring(0, 60) + "..." : text;
    }

    void Notify(string text, bool problem) {
        icon.ShowBalloonTip(8000, "Claude Transplant", text, problem ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    void Refresh() {
        Run(new[] { "accounts", "--json" }, null, (code, output, error) => {
            var parsed = code == 0 ? Account.Parse(output) : null;
            accounts = parsed;
            if (!running) Show(parsed == null ? "Accounts could not be read" : "Ready");
        });
    }

    void ChooseMove() {
        using (var form = new MoveForm(accounts)) {
            if (form.ShowDialog() != DialogResult.OK) return;
            var args = form.Sources.SelectMany(source => new[] { "--from", source.Selector }).Concat(new[] { "--to", form.Destination.Selector, "--cloud", "--json" });
            Start(args.ToArray());
        }
    }

    void ConfirmUndo() {
        if (MessageBox.Show("Undo the last move? Sessions return to the account they came from.", "Claude Transplant", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
            Start(new[] { "undo", "--json" });
    }

    void Start(string[] args) {
        operationArgs = args;
        approvalAttempted = false;
        Launch(args);
    }

    void Launch(string[] args) {
        running = true;
        plan = null;
        done = null;
        failure = null;
        Show(args[0] == "undo" ? "Undoing" : args[0] == "finish" ? "Finishing" : args[0] == "keep-local" ? "Keeping local" : "Moving");
        Run(args, Handle, (code, output, error) => Finished(code, error));
    }

    void Handle(string line) {
        var row = Json.Parse(line) as IDictionary<string, object>;
        if (row == null) return;
        if (Json.Flag(row, "plan")) plan = row;
        else if (Json.Flag(row, "done")) done = row;
        else if (Json.Text(row, "stage") != null && Json.Flag(row, "live")) {
            var completed = Json.Number(row, "completed");
            var total = Json.Number(row, "total");
            var text = Json.Text(row, "text") ?? Json.Text(row, "stage");
            Show(completed.HasValue && total.HasValue && total.Value > 0 ? text + " " + (100 * completed.Value / total.Value) + "%" : text);
        }
        else if (Json.Text(row, "error") != null || Json.Text(row, "reason") != null) failure = row;
    }

    void Finished(int code, string error) {
        running = false;
        if (plan != null && code == 0) {
            if (approvalAttempted) {
                Show("Ready");
                Notify("Open Claude sessions changed before the restart. Try again.", true);
            }
            else Confirm(plan);
            Refresh();
            return;
        }
        Show("Ready");
        Notify(Outcome(code, error), code != 0 || done != null && done.ContainsKey("ok") && !Json.Flag(done, "ok"));
        Refresh();
    }

    string Outcome(int code, string error) {
        var note = Json.Text(done, "note") ?? Json.Text(done, "reason");
        if (note != null) return note;
        if (code != 0) {
            var detail = Json.Text(failure, "error") ?? Json.Text(failure, "reason");
            if (detail != null) return detail;
            var lines = (error ?? "").Split('\n').Select(text => text.Trim()).Where(text => text.Length > 0 && !text.Contains("ExperimentalWarning") && !text.Contains("--trace-warnings")).ToArray();
            return lines.Length > 0 ? lines.Last() : "The operation failed";
        }
        if (Json.Flag(done, "nothing")) return "Nothing to move";
        var moved = Json.Number(Json.Row(done, "summary"), "moved") ?? Json.Number(done, "moved");
        if (moved.HasValue) return moved.Value + (moved.Value == 1 ? " session moved" : " sessions moved");
        return operationArgs != null && operationArgs[0] == "undo" ? "Undo finished" : "Finished";
    }

    void Confirm(IDictionary<string, object> restart) {
        var token = Json.Text(restart, "token");
        if (token == null) { Notify("The restart plan is missing its approval token.", true); return; }
        var kind = Json.Text(restart, "kind");
        var undo = kind == "undo" || Json.Flag(restart, "undoRecovery");
        var held = Json.Number(restart, "heldCount");
        var title = undo ? "Restart Claude to finish Undo?" : held.HasValue && held.Value > 0 ? "Restart Claude to move " + held.Value + (held.Value == 1 ? " session?" : " sessions?") : "Restart Claude to finish the move?";
        var names = Json.List(restart, "affected").OfType<IDictionary<string, object>>().Select(row => Json.Text(row, "title") ?? "An open Claude chat").Distinct().ToArray();
        var body = "Claude will close, including Chat, Cowork and background commands, and stay closed while Claude Transplant " + (undo ? "finishes Undo" : "moves the open sessions") + ". Then it reopens Claude.\r\n\r\nThese open chats will stop:\r\n" + (names.Length == 0 ? "No Code chats are open." : String.Join("\r\n", names));
        var choices = kind == "move" ? new[] { "Restart and move", "Move the others only", "Cancel" } : new[] { undo ? "Restart and undo" : "Restart and finish", "Cancel" };
        var choice = PromptForm.Ask(title, body, choices);
        if (choice == 0) {
            approvalAttempted = true;
            Launch(operationArgs.Concat(new[] { "--restart-approved", token }).ToArray());
        }
        else if (kind == "move" && choice == 1) Launch(operationArgs.Concat(new[] { "--move-only" }).ToArray());
        else Show("Restart cancelled. Remaining work is still waiting.");
    }

    void Quit() {
        if (running && MessageBox.Show("An operation is running. Quit anyway? It continues to completion and can be finished later.", "Claude Transplant", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        icon.Visible = false;
        ExitThread();
    }

    void Run(string[] args, Action<string> line, Action<int, string, string> exited) {
        var start = new ProcessStartInfo(config.Node, String.Join(" ", config.NodeArgs.Concat(new[] { config.Script }).Concat(args).Select(Quote))) {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        var output = new StringBuilder();
        var error = new StringBuilder();
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (sender, e) => {
            if (e.Data == null) return;
            lock (output) output.AppendLine(e.Data);
            if (line != null) ui.BeginInvoke(line, e.Data);
        };
        process.ErrorDataReceived += (sender, e) => { if (e.Data != null) lock (error) error.AppendLine(e.Data); };
        process.Exited += (sender, e) => {
            process.WaitForExit();
            ui.BeginInvoke(exited, process.ExitCode, output.ToString(), error.ToString());
        };
        try {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception exception) {
            running = false;
            ui.BeginInvoke(exited, -1, "", exception.Message);
        }
    }

    static string Quote(string arg) {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg) {
            if (c == '\\') { slashes++; continue; }
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            quoted.Append(c);
            slashes = 0;
        }
        quoted.Append('\\', slashes * 2);
        quoted.Append('"');
        return quoted.ToString();
    }
}

class MoveForm : Form {
    readonly List<Account> accounts;
    readonly CheckedListBox sources = new CheckedListBox { CheckOnClick = true, Dock = DockStyle.Fill, IntegralHeight = false };
    readonly ComboBox destination = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly Button move = new Button { Text = "Move", DialogResult = DialogResult.OK, AutoSize = true };
    public IEnumerable<Account> Sources { get { return sources.CheckedIndices.Cast<int>().Select(index => Others()[index]); } }
    public Account Destination { get { return accounts[destination.SelectedIndex]; } }

    public MoveForm(List<Account> accounts) {
        this.accounts = accounts;
        Text = "Move Claude sessions";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 300);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Move to", AutoSize = true });
        layout.Controls.Add(destination);
        layout.Controls.Add(new Label { Text = "Move sessions from", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        layout.Controls.Add(sources);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(move);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = move;
        CancelButton = cancel;
        foreach (var account in accounts) destination.Items.Add(account.Summary);
        destination.SelectedIndexChanged += (sender, e) => Fill();
        sources.ItemCheck += (sender, e) => BeginInvoke(new Action(Enable));
        var active = accounts.FindIndex(account => account.Active);
        destination.SelectedIndex = active >= 0 ? active : 0;
    }

    List<Account> Others() {
        return accounts.Where((account, index) => index != destination.SelectedIndex).ToList();
    }

    void Fill() {
        sources.Items.Clear();
        foreach (var account in Others()) sources.Items.Add(account.Summary);
        Enable();
    }

    void Enable() {
        move.Enabled = sources.CheckedItems.Count > 0;
    }
}

class PromptForm : Form {
    int choice = -1;

    PromptForm(string title, string body, string[] choices) {
        Text = "Claude Transplant";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(500, 320);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new Label { Text = title, AutoSize = true, Padding = new Padding(0, 0, 0, 8) };
        heading.Font = new Font(heading.Font.FontFamily, heading.Font.Size + 2, FontStyle.Bold);
        layout.Controls.Add(heading);
        layout.Controls.Add(new TextBox { Text = body, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control });
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        for (var index = choices.Length - 1; index >= 0; index--) {
            var picked = index;
            var button = new Button { Text = choices[index], AutoSize = true };
            button.Click += (sender, e) => { choice = picked; Close(); };
            buttons.Controls.Add(button);
            if (index == choices.Length - 1) CancelButton = button;
        }
        layout.Controls.Add(buttons);
        Controls.Add(layout);
    }

    public static int Ask(string title, string body, string[] choices) {
        using (var form = new PromptForm(title, body, choices)) {
            form.ShowDialog();
            return form.choice;
        }
    }
}
