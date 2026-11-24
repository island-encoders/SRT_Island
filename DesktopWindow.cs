using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

sealed class DesktopWindow : Form
{
    private readonly Uri panelAddress;
    private readonly string profile;
    private readonly WebView2 panel = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(49, 27, 82) };

    private DesktopWindow(Uri panelAddress, string dataDirectory, bool preview = false)
    {
        this.panelAddress = panelAddress;
        profile = Path.Combine(dataDirectory, "DesktopProfile");
        Text = preview ? "SRT Island · Live preview" : "SRT Island";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(650, 450);
        ClientSize = preview ? new Size(980, 600) : new Size(1230, 810);
        BackColor = Color.FromArgb(49, 27, 82);
        using var icon = typeof(DesktopWindow).Assembly.GetManifestResourceStream("Island.ico");
        if (icon is not null) Icon = new Icon(icon);
        Controls.Add(panel);
    }

    internal static void Run(Uri address, string data, CancellationToken stopping)
    {
        var thread = new Thread(() =>
        {
            using var window = new DesktopWindow(address, data);
            using var registration = stopping.Register(() =>
            {
                try { window.BeginInvoke(window.Close); }
                catch (InvalidOperationException) { }
            });
            System.Windows.Forms.Application.Run(window);
        }) { Name = "SRT Island desktop" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            if (IsDisposed) return;
            await panel.EnsureCoreWebView2Async(environment);
            if (IsDisposed) return;
            var web = panel.CoreWebView2;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.AreBrowserAcceleratorKeysEnabled = false;
            web.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var destination) ||
                    destination.GetLeftPart(UriPartial.Authority) != panelAddress.GetLeftPart(UriPartial.Authority) ||
                    destination.AbsolutePath != "/preview.html") return;
                BeginInvoke(() => new DesktopWindow(destination, Path.GetDirectoryName(profile)!, true).Show(this));
            };
            web.NavigationStarting += (_, args) =>
            {
                args.Cancel = !Uri.TryCreate(args.Uri, UriKind.Absolute, out var destination) ||
                    destination.GetLeftPart(UriPartial.Authority) != panelAddress.GetLeftPart(UriPartial.Authority);
            };
            web.Navigate(panelAddress.AbsoluteUri);
        }
        catch (Exception error) when (error is WebView2RuntimeNotFoundException or InvalidOperationException
            or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            if (IsDisposed) return;
            MessageBox.Show(this, $"SRT Island could not open its desktop controls. Check the Microsoft WebView2 Runtime.\n\n{error.Message}",
                "SRT Island", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }
}
