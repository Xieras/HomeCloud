// Установщик HomeCloud Connect.
//
// Один exe-файл: внутри (как ресурс "app.exe") лежит сама программа. Установщик
//   * копирует её в %LocalAppData%\Programs\HomeCloud Connect (права администратора не нужны),
//   * создаёт ярлык в меню «Пуск» (и по желанию на рабочем столе),
//   * регистрирует программу в «Параметры → Приложения», откуда её можно удалить.
//
// Тот же файл, скопированный в папку установки под именем Uninstall.exe и запущенный с ключом
// /uninstall, работает как деинсталлятор. Ключ /silent — тихая установка без окон.
// Код написан на C# 5, чтобы собираться встроенным в Windows компилятором csc.exe.

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("HomeCloud Connect — установка")]
[assembly: AssemblyProduct("HomeCloud Connect")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace HomeCloudSetup
{
    static class Info
    {
        public const string AppName = "HomeCloud Connect";
        public const string Version = "1.0.0";
        public const string ExeName = "HomeCloudConnect.exe";
        public const string UninstallName = "Uninstall.exe";
        public const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HomeCloudConnect";

        public static string DefaultDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    Path.Combine("Programs", AppName));
            }
        }

        public static string StartMenuLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); }
        }

        public static string DesktopLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); }
        }
    }

    static class Setup
    {
        public static string Install(string dir, bool desktopShortcut)
        {
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, Info.ExeName);
            string uninstall = Path.Combine(dir, Info.UninstallName);

            using (Stream res = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.exe"))
            {
                if (res == null) throw new InvalidOperationException("В установщик не встроена программа (ресурс app.exe).");
                try
                {
                    using (FileStream f = new FileStream(exe, FileMode.Create, FileAccess.Write, FileShare.None))
                        res.CopyTo(f);
                }
                catch (IOException)
                {
                    throw new InvalidOperationException("Не удалось записать " + Info.ExeName
                        + ". Закройте HomeCloud Connect, если он запущен, и повторите установку.");
                }
            }

            string self = Assembly.GetExecutingAssembly().Location;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninstall), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, uninstall, true);

            MakeShortcut(Info.StartMenuLink, exe, dir);
            if (desktopShortcut) MakeShortcut(Info.DesktopLink, exe, dir);
            else TryDelete(Info.DesktopLink);

            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Info.RegKey))
            {
                k.SetValue("DisplayName", Info.AppName);
                k.SetValue("DisplayVersion", Info.Version);
                k.SetValue("Publisher", "HomeCloud");
                k.SetValue("InstallLocation", dir);
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("UninstallString", "\"" + uninstall + "\" /uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            }
            return exe;
        }

        // Ярлык через WScript.Shell (есть в любой Windows, подключать ничего не нужно)
        static void MakeShortcut(string lnk, string target, string workDir)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            object shell = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type st = sc.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { Info.AppName });
            st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        // Удаление. Стираются только файлы, созданные установщиком; папка — только если она пуста.
        public static int Uninstall()
        {
            string self = Assembly.GetExecutingAssembly().Location;
            string dir = Path.GetDirectoryName(self);
            if (!string.Equals(Path.GetFileName(self), Info.UninstallName, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Удалить программу можно через «Параметры → Приложения» или файлом Uninstall.exe из папки установки.",
                    Info.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }

            if (MessageBox.Show("Удалить " + Info.AppName + " с этого компьютера?", Info.AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return 0;

            bool wipe = MessageBox.Show("Удалить также сохранённые адреса хранилищ?\r\n\r\n"
                    + "«Нет» — оставить их на случай переустановки.", Info.AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

            string exe = Path.Combine(dir, Info.ExeName);
            try
            {
                if (File.Exists(exe)) File.Delete(exe);
            }
            catch (Exception)
            {
                MessageBox.Show("Не удалось удалить " + Info.ExeName + ". Закройте HomeCloud Connect и повторите.",
                    Info.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            TryDelete(Info.StartMenuLink);
            TryDelete(Info.DesktopLink);
            try { Registry.CurrentUser.DeleteSubKey(Info.RegKey, false); } catch (Exception) { }
            if (wipe)
            {
                try
                {
                    string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HomeCloud");
                    if (Directory.Exists(data)) Directory.Delete(data, true);
                }
                catch (Exception) { }
            }

            // Uninstall.exe не может удалить сам себя, пока работает, — поручаем это отдельной команде
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + self + "\" & rmdir \"" + dir + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch (Exception) { }

            MessageBox.Show(Info.AppName + " удалён.", Info.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
    }

    sealed class SetupForm : Form
    {
        TextBox tbDir;
        CheckBox cbDesktop, cbRun;
        Button bInstall, bCancel, bBrowse;

        public SetupForm()
        {
            SuspendLayout();
            Text = Info.AppName + " — установка";
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 310);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            Panel head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 72;
            head.BackColor = Color.FromArgb(0x1c, 0x22, 0x26);
            Panel line = new Panel();
            line.Dock = DockStyle.Bottom;
            line.Height = 3;
            line.BackColor = Color.FromArgb(0xf2, 0xa0, 0x07);
            Label t1 = new Label();
            t1.Text = Info.AppName;
            t1.ForeColor = Color.White;
            t1.Font = new Font("Segoe UI Semibold", 15F);
            t1.AutoSize = true;
            t1.Location = new Point(20, 10);
            Label t2 = new Label();
            t2.Text = "Адреса ваших домашних хранилищ в одном окне";
            t2.ForeColor = Color.FromArgb(0xaa, 0xb4, 0xba);
            t2.AutoSize = true;
            t2.Location = new Point(22, 42);
            head.Controls.Add(t1);
            head.Controls.Add(t2);
            head.Controls.Add(line);

            Label l1 = new Label();
            l1.Text = "Папка установки:";
            l1.AutoSize = true;
            l1.Location = new Point(20, 92);

            tbDir = new TextBox();
            tbDir.Text = Info.DefaultDir;
            tbDir.Location = new Point(20, 114);
            tbDir.Size = new Size(380, 23);

            bBrowse = new Button();
            bBrowse.Text = "Обзор…";
            bBrowse.Location = new Point(410, 112);
            bBrowse.Size = new Size(90, 27);
            bBrowse.Click += delegate { Browse(); };

            cbDesktop = new CheckBox();
            cbDesktop.Text = "Создать ярлык на рабочем столе";
            cbDesktop.Checked = true;
            cbDesktop.AutoSize = true;
            cbDesktop.Location = new Point(20, 152);

            cbRun = new CheckBox();
            cbRun.Text = "Запустить после установки";
            cbRun.Checked = true;
            cbRun.AutoSize = true;
            cbRun.Location = new Point(20, 178);

            Label note = new Label();
            note.Text = "Права администратора не нужны: программа ставится только для вашей учётной записи. "
                      + "Сохранённые адреса хранятся в %APPDATA%\\HomeCloud.";
            note.ForeColor = Color.FromArgb(0x5f, 0x6b, 0x73);
            note.AutoSize = false;
            note.Location = new Point(20, 212);
            note.Size = new Size(480, 40);

            bInstall = new Button();
            bInstall.Text = "Установить";
            bInstall.Location = new Point(300, 266);
            bInstall.Size = new Size(100, 30);
            bInstall.Click += delegate { DoInstall(); };

            bCancel = new Button();
            bCancel.Text = "Отмена";
            bCancel.Location = new Point(410, 266);
            bCancel.Size = new Size(90, 30);
            bCancel.Click += delegate { Close(); };

            Controls.Add(head);
            Controls.Add(l1);
            Controls.Add(tbDir);
            Controls.Add(bBrowse);
            Controls.Add(cbDesktop);
            Controls.Add(cbRun);
            Controls.Add(note);
            Controls.Add(bInstall);
            Controls.Add(bCancel);
            AcceptButton = bInstall;
            CancelButton = bCancel;

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(false);
            PerformLayout();
        }

        void Browse()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Выберите папку для установки";
                if (d.ShowDialog(this) == DialogResult.OK)
                    tbDir.Text = Path.Combine(d.SelectedPath, Info.AppName);
            }
        }

        void DoInstall()
        {
            string dir;
            try
            {
                dir = Path.GetFullPath(tbDir.Text.Trim());
            }
            catch (Exception)
            {
                MessageBox.Show(this, "Путь указан неверно.", Info.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bInstall.Enabled = false;
            bCancel.Enabled = false;
            bBrowse.Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                string exe = Setup.Install(dir, cbDesktop.Checked);
                Cursor = Cursors.Default;
                MessageBox.Show(this, "Установка завершена.", Info.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (cbRun.Checked)
                {
                    ProcessStartInfo psi = new ProcessStartInfo(exe);
                    psi.WorkingDirectory = dir;
                    Process.Start(psi);
                }
                Close();
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                MessageBox.Show(this, "Установка не удалась:\r\n" + ex.Message, Info.AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                bInstall.Enabled = true;
                bCancel.Enabled = true;
                bBrowse.Enabled = true;
            }
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string a = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (a == "/uninstall") return Setup.Uninstall();

            if (a == "/silent")
            {
                try
                {
                    Setup.Install(Info.DefaultDir, true);
                    return 0;
                }
                catch (Exception)
                {
                    return 1;
                }
            }

            Application.Run(new SetupForm());
            return 0;
        }
    }
}
