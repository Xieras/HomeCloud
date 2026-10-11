// HomeCloud Connect — маленькая программа для Windows: хранит адреса (IP или домен)
// ваших хранилищ HomeCloud, показывает, какие из них сейчас в сети, и открывает нужное в браузере.
//
// Собирается встроенным в Windows компилятором (csc.exe, C# 5) — ничего устанавливать не нужно.
// Поэтому здесь нет современного синтаксиса вроде $"..." и "?." — компилятор Windows их не знает.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("HomeCloud Connect")]
[assembly: AssemblyProduct("HomeCloud Connect")]
[assembly: AssemblyDescription("Сохранённые адреса домашних хранилищ HomeCloud")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace HomeCloudConnect
{
    // ---- данные -------------------------------------------------------------------------

    public class Server
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public bool Main { get; set; }
    }

    public class Settings
    {
        public List<Server> Servers { get; set; }
        public bool AutoOpen { get; set; }
        public Settings() { Servers = new List<Server>(); }
    }

    // Хранение в %APPDATA%\HomeCloud\servers.json
    static class Store
    {
        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HomeCloud"); }
        }

        public static string FilePath { get { return Path.Combine(Dir, "servers.json"); } }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    try
                    {
                        Settings s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath, Encoding.UTF8));
                        if (s != null)
                        {
                            if (s.Servers == null) s.Servers = new List<Server>();
                            s.Servers.RemoveAll(delegate(Server x) { return x == null || string.IsNullOrEmpty(x.Url); });
                            return s;
                        }
                    }
                    catch (Exception)
                    {
                        // файл повреждён — сохраняем копию, чтобы ничего не потерять, и начинаем с чистого листа
                        try { File.Copy(FilePath, FilePath + ".bad", true); } catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            return new Settings();
        }

        public static void Save(Settings s)
        {
            Directory.CreateDirectory(Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(s), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }
    }

    // ---- проверка и приведение адреса ----------------------------------------------------

    static class Addr
    {
        public const string Help = "Адрес указан неверно. Примеры: 192.168.1.50, 192.168.1.50:8080, cloud.example.com";

        // Приводит ввод пользователя к виду схема://хост[:порт][/путь]. Разрешены только http и https.
        public static bool TryNormalize(string input, bool https, out string url, out string error)
        {
            url = null;
            error = null;
            string s = (input ?? "").Trim();
            if (s.Length == 0)
            {
                error = "Введите адрес хранилища.";
                return false;
            }
            if (s.IndexOf("://", StringComparison.Ordinal) < 0)
                s = (https ? "https://" : "http://") + s;

            Uri u;
            if (!Uri.TryCreate(s, UriKind.Absolute, out u)
                || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(u.Host)
                || u.UserInfo.Length > 0)
            {
                error = Help;
                return false;
            }
            string path = u.AbsolutePath.TrimEnd('/');
            url = u.GetLeftPart(UriPartial.Authority) + path;
            return true;
        }
    }

    // Проверка «жив ли сервер»: обычный GET страницы входа (в журнал сайта он не попадает)
    static class Health
    {
        public static string Probe(string baseUrl)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(baseUrl.TrimEnd('/') + "/login");
                req.Method = "GET";
                req.Timeout = 5000;
                req.ReadWriteTimeout = 5000;
                req.AllowAutoRedirect = false;
                req.UserAgent = "HomeCloudConnect/1.0";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    sw.Stop();
                    return "в сети, " + sw.ElapsedMilliseconds + " мс";
                }
            }
            catch (WebException ex)
            {
                sw.Stop();
                if (ex.Response != null)
                {
                    ex.Response.Close();
                    return "в сети, " + sw.ElapsedMilliseconds + " мс";
                }
                if (ex.Status == WebExceptionStatus.TrustFailure || ex.Status == WebExceptionStatus.SecureChannelFailure)
                    return "отвечает, но сертификат не доверен";
                if (ex.Status == WebExceptionStatus.Timeout)
                    return "нет ответа (таймаут)";
                if (ex.Status == WebExceptionStatus.NameResolutionFailure)
                    return "имя не найдено";
                return "недоступно";
            }
            catch (Exception)
            {
                return "недоступно";
            }
        }
    }

    // ---- запуск --------------------------------------------------------------------------

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 768 | 192); } catch (Exception) { }

            Settings cfg = Store.Load();

            // Если включено «открывать при запуске» — сразу открываем основное хранилище.
            // Чтобы увидеть окно, удерживайте Shift при запуске.
            if (cfg.AutoOpen && Control.ModifierKeys != Keys.Shift)
            {
                Server main = cfg.Servers.Find(delegate(Server s) { return s.Main; });
                if (main != null && MainForm.OpenUrl(main.Url)) return;
            }
            Application.Run(new MainForm(cfg));
        }
    }

    // ---- главное окно --------------------------------------------------------------------

    sealed class MainForm : Form
    {
        readonly Settings cfg;
        readonly Dictionary<Server, string> status = new Dictionary<Server, string>();
        readonly System.Windows.Forms.Timer msgTimer = new System.Windows.Forms.Timer();
        Server current;          // запись, которая сейчас в форме справа (null — новая)
        bool loading;

        ListView list;
        TextBox tbName, tbAddr;
        CheckBox cbHttps, cbAuto;
        Button bSave, bNew, bOpen, bCheck, bMain, bDel;
        Label lblForm, lblMsg;

        public MainForm(Settings c)
        {
            cfg = c;
            Build();
            FillList(null);
        }

        // --- построение интерфейса. Размеры указаны для 96 dpi, Windows Forms масштабирует их сам ---
        void Build()
        {
            SuspendLayout();

            Text = "HomeCloud Connect";
            Font = new Font("Segoe UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(800, 470);
            MinimumSize = new Size(720, 440);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            Color dark = Color.FromArgb(0x1c, 0x22, 0x26);
            Color amber = Color.FromArgb(0xf2, 0xa0, 0x07);
            Color gray = Color.FromArgb(0x5f, 0x6b, 0x73);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.Margin = Padding.Empty;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));

            // шапка
            Panel head = new Panel();
            head.Dock = DockStyle.Fill;
            head.BackColor = dark;
            head.Margin = Padding.Empty;
            Panel line = new Panel();
            line.Dock = DockStyle.Bottom;
            line.Height = 3;
            line.BackColor = amber;
            Label t1 = new Label();
            t1.Text = "HomeCloud";
            t1.ForeColor = Color.White;
            t1.Font = new Font("Segoe UI Semibold", 15F);
            t1.AutoSize = true;
            t1.Location = new Point(16, 6);
            Label t2 = new Label();
            t2.Text = "Адреса ваших домашних хранилищ";
            t2.ForeColor = Color.FromArgb(0xaa, 0xb4, 0xba);
            t2.AutoSize = true;
            t2.Location = new Point(18, 38);
            head.Controls.Add(t1);
            head.Controls.Add(t2);
            head.Controls.Add(line);

            // середина: слева список, справа форма
            TableLayoutPanel body = new TableLayoutPanel();
            body.Dock = DockStyle.Fill;
            body.ColumnCount = 2;
            body.RowCount = 1;
            body.Padding = new Padding(12);
            body.Margin = Padding.Empty;
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300F));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            TableLayoutPanel left = new TableLayoutPanel();
            left.Dock = DockStyle.Fill;
            left.ColumnCount = 1;
            left.RowCount = 2;
            left.Margin = Padding.Empty;
            left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = false;
            list.HideSelection = false;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.Columns.Add("Название", 100);
            list.Columns.Add("Адрес", 100);
            list.Columns.Add("Состояние", 100);
            list.SelectedIndexChanged += delegate { OnPick(); };
            list.DoubleClick += delegate { OpenSelected(); };
            list.Resize += delegate { FitColumns(); };
            list.Enter += delegate { AcceptButton = bOpen; };
            list.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete) DeleteSelected();
            };

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.AutoSize = true;
            actions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            actions.Margin = new Padding(0, 8, 0, 0);
            bOpen = MakeButton("Открыть в браузере", delegate { OpenSelected(); });
            bCheck = MakeButton("Проверить", delegate { CheckSelected(); });
            bMain = MakeButton("Сделать основным", delegate { MakeMain(); });
            bDel = MakeButton("Удалить", delegate { DeleteSelected(); });
            actions.Controls.Add(bOpen);
            actions.Controls.Add(bCheck);
            actions.Controls.Add(bMain);
            actions.Controls.Add(bDel);

            left.Controls.Add(list, 0, 0);
            left.Controls.Add(actions, 0, 1);

            TableLayoutPanel edit = new TableLayoutPanel();
            edit.Dock = DockStyle.Fill;
            edit.ColumnCount = 1;
            edit.Padding = new Padding(16, 0, 0, 0);
            edit.Margin = Padding.Empty;
            edit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            lblForm = new Label();
            lblForm.Font = new Font("Segoe UI Semibold", 11F);
            lblForm.AutoSize = true;
            lblForm.Margin = new Padding(0, 0, 0, 4);
            edit.Controls.Add(lblForm);

            edit.Controls.Add(MakeLabel("Название"));
            tbName = new TextBox();
            tbName.Dock = DockStyle.Fill;
            tbName.MaxLength = 60;
            tbName.Margin = new Padding(0);
            tbName.Enter += delegate { AcceptButton = bSave; };
            edit.Controls.Add(tbName);

            edit.Controls.Add(MakeLabel("IP-адрес или домен"));
            tbAddr = new TextBox();
            tbAddr.Dock = DockStyle.Fill;
            tbAddr.MaxLength = 200;
            tbAddr.Margin = new Padding(0);
            tbAddr.Enter += delegate { AcceptButton = bSave; };
            edit.Controls.Add(tbAddr);

            cbHttps = new CheckBox();
            cbHttps.Text = "Подключаться по HTTPS";
            cbHttps.AutoSize = true;
            cbHttps.Margin = new Padding(0, 8, 0, 0);
            edit.Controls.Add(cbHttps);

            Label hint = new Label();
            hint.AutoSize = false;
            hint.Height = 60;
            hint.Dock = DockStyle.Fill;
            hint.ForeColor = gray;
            hint.Margin = new Padding(0, 6, 0, 0);
            hint.Text = "Примеры: 192.168.1.50, 192.168.1.50:8080, cloud.example.com. "
                      + "Если схема не указана, используется http — либо https, когда стоит галочка.";
            edit.Controls.Add(hint);

            FlowLayoutPanel formButtons = new FlowLayoutPanel();
            formButtons.AutoSize = true;
            formButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            formButtons.Margin = new Padding(0, 4, 0, 0);
            bSave = MakeButton("Сохранить", delegate { SaveForm(); });
            bNew = MakeButton("Новое", delegate { NewEntry(); });
            formButtons.Controls.Add(bSave);
            formButtons.Controls.Add(bNew);
            edit.Controls.Add(formButtons);

            body.Controls.Add(left, 0, 0);
            body.Controls.Add(edit, 1, 0);

            // нижняя полоса
            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.BackColor = Color.FromArgb(0xe8, 0xeb, 0xed);
            bar.Padding = new Padding(12, 10, 12, 0);
            bar.Margin = Padding.Empty;
            bar.WrapContents = false;
            cbAuto = new CheckBox();
            cbAuto.Text = "При запуске сразу открывать основное хранилище (Shift — показать окно)";
            cbAuto.AutoSize = true;
            cbAuto.Margin = new Padding(0, 0, 24, 0);
            cbAuto.Checked = cfg.AutoOpen;
            cbAuto.CheckedChanged += delegate
            {
                cfg.AutoOpen = cbAuto.Checked;
                Persist();
            };
            lblMsg = new Label();
            lblMsg.AutoSize = true;
            lblMsg.Margin = new Padding(0, 2, 0, 0);
            bar.Controls.Add(cbAuto);
            bar.Controls.Add(lblMsg);

            root.Controls.Add(head, 0, 0);
            root.Controls.Add(body, 0, 1);
            root.Controls.Add(bar, 0, 2);
            Controls.Add(root);

            msgTimer.Interval = 4500;
            msgTimer.Tick += delegate { msgTimer.Stop(); lblMsg.Text = ""; };

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(false);
            PerformLayout();

            Shown += delegate
            {
                FitColumns();
                CheckAll();
            };
        }

        static Button MakeButton(string text, EventHandler click)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(8, 2, 8, 2);
            b.Margin = new Padding(0, 0, 8, 4);
            b.UseVisualStyleBackColor = true;
            b.Click += click;
            return b;
        }

        static Label MakeLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(0, 10, 0, 3);
            return l;
        }

        void FitColumns()
        {
            int w = list.ClientSize.Width;
            if (w < 60) return;
            list.Columns[0].Width = w * 30 / 100;
            list.Columns[1].Width = w * 38 / 100;
            list.Columns[2].Width = w - list.Columns[0].Width - list.Columns[1].Width;
        }

        // --- работа со списком ---

        Server Selected()
        {
            if (list.SelectedItems.Count == 0) return null;
            return list.SelectedItems[0].Tag as Server;
        }

        ListViewItem FindItem(Server s)
        {
            foreach (ListViewItem it in list.Items)
                if (object.ReferenceEquals(it.Tag, s)) return it;
            return null;
        }

        void FillList(Server select)
        {
            loading = true;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (Server s in cfg.Servers)
            {
                string st;
                if (!status.TryGetValue(s, out st)) st = "";
                ListViewItem it = new ListViewItem(new string[] { (s.Main ? "\u2605 " : "") + s.Name, s.Url, st });
                it.UseItemStyleForSubItems = false;
                it.SubItems[2].ForeColor = StatusColor(st);
                it.Tag = s;
                list.Items.Add(it);
                if (object.ReferenceEquals(s, select)) it.Selected = true;
            }
            list.EndUpdate();
            loading = false;

            if (select != null) LoadForm(select);
            else if (current == null) ResetForm();
            UpdateButtons();
        }

        static Color StatusColor(string st)
        {
            if (st.StartsWith("в сети")) return Color.FromArgb(0x2b, 0x8a, 0x57);
            if (st.Length == 0 || st.StartsWith("проверка")) return Color.FromArgb(0x5f, 0x6b, 0x73);
            return Color.FromArgb(0xc2, 0x3b, 0x3b);
        }

        void OnPick()
        {
            if (loading) return;
            Server s = Selected();
            if (s != null) LoadForm(s);
            UpdateButtons();
        }

        void LoadForm(Server s)
        {
            current = s;
            tbName.Text = s.Name;
            tbAddr.Text = s.Url;
            cbHttps.Checked = s.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            lblForm.Text = "Изменение адреса";
            bSave.Text = "Сохранить изменения";
        }

        void ResetForm()
        {
            current = null;
            tbName.Text = "";
            tbAddr.Text = "";
            cbHttps.Checked = false;
            lblForm.Text = "Новое хранилище";
            bSave.Text = "Добавить";
        }

        void UpdateButtons()
        {
            bool has = Selected() != null;
            bOpen.Enabled = has;
            bCheck.Enabled = has;
            bDel.Enabled = has;
            bMain.Enabled = has && !Selected().Main;
        }

        void NewEntry()
        {
            list.SelectedItems.Clear();
            ResetForm();
            UpdateButtons();
            tbName.Focus();
        }

        // --- действия ---

        void SaveForm()
        {
            string url, err;
            if (!Addr.TryNormalize(tbAddr.Text, cbHttps.Checked, out url, out err))
            {
                Msg(err, true);
                tbAddr.Focus();
                return;
            }
            foreach (Server x in cfg.Servers)
            {
                if (!object.ReferenceEquals(x, current) && string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase))
                {
                    Msg("Такой адрес уже сохранён: " + x.Name, true);
                    return;
                }
            }
            string name = tbName.Text.Trim();
            if (name.Length == 0) name = new Uri(url).Host;
            if (name.Length > 60) name = name.Substring(0, 60);

            bool added = false;
            if (current == null)
            {
                current = new Server();
                current.Main = cfg.Servers.Count == 0;
                cfg.Servers.Add(current);
                added = true;
            }
            current.Name = name;
            current.Url = url;
            status.Remove(current);

            if (!Persist()) return;
            FillList(current);
            Msg(added ? "Адрес сохранён" : "Изменения сохранены", false);
            CheckOne(current);
        }

        void MakeMain()
        {
            Server sel = Selected();
            if (sel == null) return;
            foreach (Server s in cfg.Servers) s.Main = object.ReferenceEquals(s, sel);
            if (Persist()) FillList(sel);
        }

        void DeleteSelected()
        {
            Server sel = Selected();
            if (sel == null) return;
            DialogResult r = MessageBox.Show(this, "Удалить сохранённый адрес «" + sel.Name + "»?", "HomeCloud Connect",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;

            cfg.Servers.Remove(sel);
            status.Remove(sel);
            if (sel.Main && cfg.Servers.Count > 0) cfg.Servers[0].Main = true;
            current = null;
            if (Persist()) FillList(null);
        }

        void OpenSelected()
        {
            Server sel = Selected();
            if (sel != null) OpenUrl(sel.Url);
        }

        public static bool OpenUrl(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
                return false;
            try
            {
                Process.Start(u.AbsoluteUri);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось открыть браузер: " + ex.Message, "HomeCloud Connect",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        bool Persist()
        {
            try
            {
                Store.Save(cfg);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось сохранить список: " + ex.Message, "HomeCloud Connect",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        void Msg(string text, bool error)
        {
            lblMsg.ForeColor = error ? Color.FromArgb(0xc2, 0x3b, 0x3b) : Color.FromArgb(0x2b, 0x8a, 0x57);
            lblMsg.Text = text;
            msgTimer.Stop();
            msgTimer.Start();
        }

        // --- проверка доступности (в фоне, чтобы окно не зависало) ---

        void CheckSelected()
        {
            Server s = Selected();
            if (s != null) CheckOne(s);
        }

        void CheckAll()
        {
            foreach (Server s in new List<Server>(cfg.Servers)) CheckOne(s);
        }

        void CheckOne(Server s)
        {
            SetStatus(s, "проверка…");
            string url = s.Url;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string result = Health.Probe(url);
                try
                {
                    BeginInvoke((MethodInvoker)delegate { SetStatus(s, result); });
                }
                catch (InvalidOperationException) { }   // окно уже закрыто
            });
        }

        void SetStatus(Server s, string text)
        {
            if (!cfg.Servers.Contains(s)) return;   // запись удалили, пока шла проверка
            status[s] = text;
            ListViewItem it = FindItem(s);
            if (it == null) return;
            it.SubItems[2].Text = text;
            it.SubItems[2].ForeColor = StatusColor(text);
        }
    }
}
