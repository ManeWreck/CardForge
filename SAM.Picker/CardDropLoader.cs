/* Copyright (c) 2024 Rick (rick 'at' gibbed 'dot' us)
 *
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 *
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 *
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would
 *    be appreciated but is not required.
 *
 * 2. Altered source versions must be plainly marked as such, and must not
 *    be misrepresented as being the original software.
 *
 * 3. This notice may not be removed or altered from any source
 *    distribution.
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SAM.Picker
{
    internal sealed class CardDropLoader : Form
    {
        private readonly ulong _SteamId;
        private readonly Action<Dictionary<uint, int>> _ApplyDrops;
        private readonly WebView2 _WebView;
        private readonly ToolStripStatusLabel _StatusLabel;
        private readonly ToolStripButton _OpenBadgesButton;
        private readonly ToolStripButton _ScanCurrentButton;
        private readonly ToolStripButton _ScanDropsButton;
        private readonly Dictionary<uint, int> _Drops;
        private readonly bool _AutoScan;
        private readonly bool _CloseAfterScan;

        public CardDropLoader(ulong steamId, Action<Dictionary<uint, int>> applyDrops)
            : this(steamId, applyDrops, false, false)
        {
        }

        public CardDropLoader(ulong steamId, Action<Dictionary<uint, int>> applyDrops, bool autoScan, bool closeAfterScan)
        {
            this._SteamId = steamId;
            this._ApplyDrops = applyDrops;
            this._Drops = new();
            this._AutoScan = autoScan;
            this._CloseAfterScan = closeAfterScan;

            this.Text = "Steam Card Drop Loader";
            this.MinimumSize = new Size(1000, 700);
            this.Size = new Size(1180, 780);
            this.BackColor = Color.FromArgb(18, 21, 27);

            ToolStrip toolStrip = new()
            {
                BackColor = Color.FromArgb(28, 33, 42),
                ForeColor = Color.FromArgb(232, 238, 247),
                GripStyle = ToolStripGripStyle.Hidden,
            };

            this._OpenBadgesButton = new("Open Badges");
            this._OpenBadgesButton.Click += (sender, e) => this.OpenBadgesPage();

            this._ScanCurrentButton = new("Scan Current Page");
            this._ScanCurrentButton.Click += async (sender, e) => await this.ScanCurrentPageAsync();

            this._ScanDropsButton = new("Scan Drops");
            this._ScanDropsButton.Click += async (sender, e) => await this.ScanDropsAsync();

            toolStrip.Items.Add(this._OpenBadgesButton);
            toolStrip.Items.Add(this._ScanCurrentButton);
            toolStrip.Items.Add(this._ScanDropsButton);

            StatusStrip statusStrip = new()
            {
                BackColor = Color.FromArgb(28, 33, 42),
                ForeColor = Color.FromArgb(170, 187, 204),
                SizingGrip = false,
            };
            this._StatusLabel = new ToolStripStatusLabel("Open badges, sign in if Steam asks, then scan drops.")
            {
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            statusStrip.Items.Add(this._StatusLabel);

            this._WebView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(18, 21, 27),
            };

            this.Controls.Add(this._WebView);
            this.Controls.Add(statusStrip);
            this.Controls.Add(toolStrip);
            toolStrip.Dock = DockStyle.Top;
            statusStrip.Dock = DockStyle.Bottom;

            this.Load += async (sender, e) =>
            {
                var environment = await CoreWebView2Environment.CreateAsync(null, this.GetWebViewUserDataFolder());
                await this._WebView.EnsureCoreWebView2Async(environment);
                this.OpenBadgesPage();
                if (this._AutoScan == true)
                {
                    await this.ScanDropsAsync();
                    if (this._CloseAfterScan == true)
                    {
                        this.Close();
                    }
                }
            };
        }

        private void OpenBadgesPage()
        {
            this._WebView.CoreWebView2.Navigate(this.GetBadgesUrl(1));
            this._StatusLabel.Text = "Badges page opened. Sign in here if Steam asks.";
        }

        private async Task ScanCurrentPageAsync()
        {
            var drops = await this.GetCardDropsFromCurrentPageAsync();
            this._ApplyDrops(drops);
            this._StatusLabel.Text =
                $"Applied current page drops for {drops.Count.ToString(CultureInfo.InvariantCulture)} games.";
        }

        private async Task ScanDropsAsync()
        {
            this.SetButtonsEnabled(false);
            try
            {
                this._Drops.Clear();
                for (int page = 1; page <= 25; page++)
                {
                    this._StatusLabel.Text =
                        $"Scanning badge page {page.ToString(CultureInfo.InvariantCulture)}...";
                    await this.NavigateAndWaitAsync(this.GetBadgesUrl(page));

                    var pageDrops = await this.GetCardDropsFromCurrentPageAsync();
                    foreach (var kv in pageDrops)
                    {
                        this._Drops[kv.Key] = kv.Value;
                    }

                    if (await this.HasNextBadgesPageAsync(page) == false)
                    {
                        break;
                    }
                }

                this._ApplyDrops(new Dictionary<uint, int>(this._Drops));
                this._StatusLabel.Text =
                    $"Applied remaining card drops for {this._Drops.Count.ToString(CultureInfo.InvariantCulture)} games.";
            }
            finally
            {
                this.SetButtonsEnabled(true);
            }
        }

        private async Task<Dictionary<uint, int>> GetCardDropsFromCurrentPageAsync()
        {
            var script =
                "Array.from(document.querySelectorAll('.badge_row, a[href*=\"/gamecards/\"]')).map(el => {" +
                "  const link = el.matches && el.matches('a[href*=\"/gamecards/\"]') ? el : el.querySelector && el.querySelector('a[href*=\"/gamecards/\"]');" +
                "  if (!link) return '';" +
                "  const match = link.href.match(/gamecards\\/(\\d+)/);" +
                "  if (!match) return '';" +
                "  const row = link.closest('.badge_row') || el;" +
                "  return match[1] + '\\t' + (row.innerText || el.innerText || '').replace(/\\s+/g, ' ').trim();" +
                "}).filter(Boolean).filter((v,i,a)=>a.indexOf(v)===i).join('\\n')";
            var raw = await this._WebView.ExecuteScriptAsync(script);
            var text = DecodeJsonString(raw);
            Dictionary<uint, int> drops = new();
            foreach (var line in text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(new[] { '\t' }, 2);
                if (parts.Length != 2 ||
                    uint.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) == false)
                {
                    continue;
                }

                var count = ParseDropsFromText(parts[1]);
                if (count.HasValue == true)
                {
                    drops[appId] = count.Value;
                }
            }

            return drops;
        }

        private async Task NavigateAndWaitAsync(string url)
        {
            var completion = new TaskCompletionSource<bool>();
            void Handler(object sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                this._WebView.NavigationCompleted -= Handler;
                completion.TrySetResult(true);
            }

            this._WebView.NavigationCompleted += Handler;
            this._WebView.CoreWebView2.Navigate(url);
            await completion.Task;
            await Task.Delay(250);
        }

        private async Task<bool> HasNextBadgesPageAsync(int page)
        {
            var result = await this._WebView.ExecuteScriptAsync(
                "Array.from(document.querySelectorAll('a.pagelink')).some(a => a.textContent.trim() === String(" +
                (page + 1).ToString(CultureInfo.InvariantCulture) +
                "))");
            return string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
        }

        private string GetBadgesUrl(int page)
        {
            return "https://steamcommunity.com/profiles/" +
                   this._SteamId.ToString(CultureInfo.InvariantCulture) +
                   "/badges/?p=" +
                   page.ToString(CultureInfo.InvariantCulture);
        }

        private void SetButtonsEnabled(bool enabled)
        {
            this._OpenBadgesButton.Enabled = enabled;
            this._ScanCurrentButton.Enabled = enabled;
            this._ScanDropsButton.Enabled = enabled;
        }

        private string GetWebViewUserDataFolder()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SAM Modern Library",
                "WebView2",
                this._SteamId.ToString(CultureInfo.InvariantCulture));
        }

        private static int? ParseDropsFromText(string text)
        {
            var match = Regex.Match(
                text,
                "(?:\\u0415\\u0449\\u0451|\\u0415\\u0449\\u0435)\\s+\\u0432\\u044b\\u043f\\u0430\\u0434\\u0435\\u0442\\s+\\u043a\\u0430\\u0440\\u0442\\u043e\\u0447\\u0435\\u043a:\\s*(?<count>\\d+)|(?<count>\\d+)\\s+card\\s+drops?\\s+remaining|card\\s+drops?\\s+remaining:\\s*(?<count>\\d+)|(?<count>\\d+)\\s+\\u043a\\u0430\\u0440\\u0442\\u043e\\u0447(?:\\u043a\\u0430|\\u043a\\u0438|\\u0435\\u043a)\\s+\\u0435\\u0449[\\u0435\\u0451]\\s+\\u0432\\u044b\\u043f\\u0430\\u0434",
                RegexOptions.IgnoreCase);
            if (match.Success == true &&
                int.TryParse(match.Groups["count"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) == true)
            {
                return count;
            }

            if (Regex.IsMatch(
                    text,
                    "\\u0431\\u043e\\u043b\\u044c\\u0448\\u0435\\s+\\u043d\\u0435\\s+\\u0432\\u044b\\u043f\\u0430\\u0434\\u0443\\u0442|no\\s+card\\s+drops?\\s+remaining",
                    RegexOptions.IgnoreCase) == true)
            {
                return 0;
            }

            return null;
        }

        private static string DecodeJsonString(string value)
        {
            if (string.IsNullOrEmpty(value) == true || value == "null")
            {
                return "";
            }

            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                value = value.Substring(1, value.Length - 2);
            }

            return Regex.Unescape(value)
                .Replace("\\\"", "\"")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t");
        }
    }
}
