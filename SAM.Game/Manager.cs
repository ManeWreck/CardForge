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
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;
using static SAM.Game.InvariantShorthand;
using APITypes = SAM.API.Types;

namespace SAM.Game
{
    internal partial class Manager : Form
    {
        private readonly long _GameId;
        private readonly API.Client _SteamClient;

        private readonly WebClient _IconDownloader = new();

        private readonly List<Stats.AchievementInfo> _IconQueue = new();
        private readonly List<Stats.StatDefinition> _StatDefinitions = new();

        private readonly List<Stats.AchievementDefinition> _AchievementDefinitions = new();

        private readonly BindingList<Stats.StatInfo> _Statistics = new();

        private readonly API.Callbacks.UserStatsReceived _UserStatsReceivedCallback;

        //private API.Callback<APITypes.UserStatsStored> UserStatsStoredCallback;
        private Panel _TitleBar;
        private Label _TitleLabel;
        private Button _MinimizeButton;
        private Button _MaximizeButton;
        private Button _CloseButton;
        private Panel _AchievementHeaderCornerCover;
        private Panel _AchievementLeftEdgeCover;
        private Panel _AchievementRightEdgeCover;
        private Panel _AchievementBottomEdgeCover;
        private Panel _TabLeftBorderCover;
        private Panel _TabRightBorderCover;
        private Panel _TabBottomBorderCover;
        private ToolStripButton _AchievementsViewButton;
        private ToolStripButton _StatisticsViewButton;

        public Manager(long gameId, API.Client client)
        {
            this.InitializeComponent();
            this.ConfigureModernGameUi();
            this.CreateModernTitleBar();
            this.CreateTabBorderCovers();

            this._MainTabControl.SelectedTab = this._AchievementsTabPage;
            //this.statisticsList.Enabled = this.checkBox1.Checked;

            this._AchievementImageList.Images.Add("Blank", new Bitmap(64, 64));

            this._StatisticsDataGridView.AutoGenerateColumns = false;

            this._StatisticsDataGridView.Columns.Add("name", "Name");
            this._StatisticsDataGridView.Columns[0].ReadOnly = true;
            this._StatisticsDataGridView.Columns[0].Width = 200;
            this._StatisticsDataGridView.Columns[0].DataPropertyName = "DisplayName";

            this._StatisticsDataGridView.Columns.Add("value", "Value");
            this._StatisticsDataGridView.Columns[1].ReadOnly = this._EnableStatsEditingCheckBox.Checked == false;
            this._StatisticsDataGridView.Columns[1].Width = 90;
            this._StatisticsDataGridView.Columns[1].DataPropertyName = "Value";

            this._StatisticsDataGridView.Columns.Add("extra", "Extra");
            this._StatisticsDataGridView.Columns[2].ReadOnly = true;
            this._StatisticsDataGridView.Columns[2].Width = 200;
            this._StatisticsDataGridView.Columns[2].DataPropertyName = "Extra";

            this._StatisticsDataGridView.DataSource = new BindingSource()
            {
                DataSource = this._Statistics,
            };

            this._GameId = gameId;
            this._SteamClient = client;

            this._IconDownloader.DownloadDataCompleted += this.OnIconDownload;

            string name = this._SteamClient.SteamApps001.GetAppData((uint)this._GameId, "name");
            if (name != null)
            {
                base.Text += " | " + name;
            }
            else
            {
                base.Text += " | " + this._GameId.ToString(CultureInfo.InvariantCulture);
            }
            this._TitleLabel.Text = base.Text;

            this._UserStatsReceivedCallback = client.CreateAndRegisterCallback<API.Callbacks.UserStatsReceived>();
            this._UserStatsReceivedCallback.OnRun += this.OnUserStatsReceived;

            //this.UserStatsStoredCallback = new API.Callback(1102, new API.Callback.CallbackFunction(this.OnUserStatsStored));

            this.RefreshStats();
        }

        private void AddAchievementIcon(Stats.AchievementInfo info, Image icon)
        {
            if (icon == null)
            {
                info.ImageIndex = 0;
            }
            else
            {
                info.ImageIndex = this._AchievementImageList.Images.Count;
                this._AchievementImageList.Images.Add(info.IsAchieved == true ? info.IconNormal : info.IconLocked, icon);
            }
        }

        private void OnIconDownload(object sender, DownloadDataCompletedEventArgs e)
        {
            if (e.Error == null && e.Cancelled == false)
            {
                var info = (Stats.AchievementInfo)e.UserState;

                Bitmap bitmap;
                try
                {
                    using (MemoryStream stream = new())
                    {
                        stream.Write(e.Result, 0, e.Result.Length);
                        bitmap = new(stream);
                    }
                }
                catch (Exception)
                {
                    bitmap = null;
                }

                this.AddAchievementIcon(info, bitmap);
                this._AchievementListView.Update();
            }

            this.DownloadNextIcon();
        }

        private void DownloadNextIcon()
        {
            if (this._IconQueue.Count == 0)
            {
                this._DownloadStatusLabel.Visible = false;
                return;
            }

            if (this._IconDownloader.IsBusy == true)
            {
                return;
            }

            this._DownloadStatusLabel.Text = $"Downloading {this._IconQueue.Count} icons...";
            this._DownloadStatusLabel.Visible = true;

            var info = this._IconQueue[0];
            this._IconQueue.RemoveAt(0);


            this._IconDownloader.DownloadDataAsync(
                new Uri(_($"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{this._GameId}/{(info.IsAchieved == true ? info.IconNormal : info.IconLocked)}")),
                info);
        }

        private static string TranslateError(int id) => id switch
        {
            2 => "generic error -- this usually means you don't own the game",
            _ => _($"{id}"),
        };

        private static string GetLocalizedString(KeyValue kv, string language, string defaultValue)
        {
            var name = kv[language].AsString("");
            if (string.IsNullOrEmpty(name) == false)
            {
                return name;
            }

            if (language != "english")
            {
                name = kv["english"].AsString("");
                if (string.IsNullOrEmpty(name) == false)
                {
                    return name;
                }
            }

            name = kv.AsString("");
            if (string.IsNullOrEmpty(name) == false)
            {
                return name;
            }

            return defaultValue;
        }

        private bool LoadUserGameStatsSchema()
        {
            string path;
            try
            {
                string fileName = _($"UserGameStatsSchema_{this._GameId}.bin");
                path = API.Steam.GetInstallPath();
                path = Path.Combine(path, "appcache", "stats", fileName);
                if (File.Exists(path) == false)
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }

            var kv = KeyValue.LoadAsBinary(path);
            if (kv == null)
            {
                return false;
            }

            var currentLanguage = this._SteamClient.SteamApps008.GetCurrentGameLanguage();

            this._AchievementDefinitions.Clear();
            this._StatDefinitions.Clear();

            var stats = kv[this._GameId.ToString(CultureInfo.InvariantCulture)]["stats"];
            if (stats.Valid == false || stats.Children == null)
            {
                return false;
            }

            foreach (var stat in stats.Children)
            {
                if (stat.Valid == false)
                {
                    continue;
                }

                APITypes.UserStatType type;

                // schema in the new format?
                var typeNode = stat["type"];
                if (typeNode.Valid == true && typeNode.Type == KeyValueType.String)
                {
                    if (Enum.TryParse((string)typeNode.Value, true, out type) == false)
                    {
                        type = APITypes.UserStatType.Invalid;
                    }
                }
                else
                {
                    type = APITypes.UserStatType.Invalid;
                }

                // schema in the old format?
                if (type == APITypes.UserStatType.Invalid)
                {
                    var typeIntNode = stat["type_int"];
                    var rawType = typeIntNode.Valid == true
                        ? typeIntNode.AsInteger(0)
                        : typeNode.AsInteger(0);
                    type = (APITypes.UserStatType)rawType;
                }

                switch (type)
                {
                    case APITypes.UserStatType.Invalid:
                    {
                        break;
                    }

                    case APITypes.UserStatType.Integer:
                    {
                        var id = stat["name"].AsString("");
                        string name = GetLocalizedString(stat["display"]["name"], currentLanguage, id);

                        this._StatDefinitions.Add(new Stats.IntegerStatDefinition()
                        {
                            Id = stat["name"].AsString(""),
                            DisplayName = name,
                            MinValue = stat["min"].AsInteger(int.MinValue),
                            MaxValue = stat["max"].AsInteger(int.MaxValue),
                            MaxChange = stat["maxchange"].AsInteger(0),
                            IncrementOnly = stat["incrementonly"].AsBoolean(false),
                            SetByTrustedGameServer = stat["bSetByTrustedGS"].AsBoolean(false),
                            DefaultValue = stat["default"].AsInteger(0),
                            Permission = stat["permission"].AsInteger(0),
                        });
                        break;
                    }

                    case APITypes.UserStatType.Float:
                    case APITypes.UserStatType.AverageRate:
                    {
                        var id = stat["name"].AsString("");
                        string name = GetLocalizedString(stat["display"]["name"], currentLanguage, id);

                        this._StatDefinitions.Add(new Stats.FloatStatDefinition()
                        {
                            Id = stat["name"].AsString(""),
                            DisplayName = name,
                            MinValue = stat["min"].AsFloat(float.MinValue),
                            MaxValue = stat["max"].AsFloat(float.MaxValue),
                            MaxChange = stat["maxchange"].AsFloat(0.0f),
                            IncrementOnly = stat["incrementonly"].AsBoolean(false),
                            DefaultValue = stat["default"].AsFloat(0.0f),
                            Permission = stat["permission"].AsInteger(0),
                        });
                        break;
                    }

                    case APITypes.UserStatType.Achievements:
                    case APITypes.UserStatType.GroupAchievements:
                    {
                        if (stat.Children != null)
                        {
                            foreach (var bits in stat.Children.Where(
                                b => string.Compare(b.Name, "bits", StringComparison.InvariantCultureIgnoreCase) == 0))
                            {
                                if (bits.Valid == false || bits.Children == null)
                                {
                                    continue;
                                }

                                foreach (var bit in bits.Children)
                                {
                                    string id = bit["name"].AsString("");
                                    string name = GetLocalizedString(bit["display"]["name"], currentLanguage, id);
                                    string desc = GetLocalizedString(bit["display"]["desc"], currentLanguage, "");

                                    this._AchievementDefinitions.Add(new()
                                    {
                                        Id = id,
                                        Name = name,
                                        Description = desc,
                                        IconNormal = bit["display"]["icon"].AsString(""),
                                        IconLocked = bit["display"]["icon_gray"].AsString(""),
                                        IsHidden = bit["display"]["hidden"].AsBoolean(false),
                                        Permission = bit["permission"].AsInteger(0),
                                    });
                                }
                            }
                        }

                        break;
                    }

                    default:
                    {
                        throw new InvalidOperationException("invalid stat type");
                    }
                }
            }

            return true;
        }

        private void OnUserStatsReceived(APITypes.UserStatsReceived param)
        {
            if (param.Result != 1)
            {
                this._GameStatusLabel.Text = $"Error while retrieving stats: {TranslateError(param.Result)}";
                this.EnableInput();
                return;
            }

            if (this.LoadUserGameStatsSchema() == false)
            {
                this._GameStatusLabel.Text = "Failed to load schema.";
                this.EnableInput();
                return;
            }

            try
            {
                this.GetAchievements();
            }
            catch (Exception e)
            {
                this._GameStatusLabel.Text = "Error when handling achievements retrieval.";
                this.EnableInput();
                MessageBox.Show(
                    "Error when handling achievements retrieval:\n" + e,
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            try
            {
                this.GetStatistics();
            }
            catch (Exception e)
            {
                this._GameStatusLabel.Text = "Error when handling stats retrieval.";
                this.EnableInput();
                MessageBox.Show(
                    "Error when handling stats retrieval:\n" + e,
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            this._GameStatusLabel.Text = $"Retrieved {this._AchievementListView.Items.Count} achievements and {this._StatisticsDataGridView.Rows.Count} statistics.";
            this.EnableInput();
        }

        private void RefreshStats()
        {
            this._AchievementListView.Items.Clear();
            this._StatisticsDataGridView.Rows.Clear();

            var steamId = this._SteamClient.SteamUser.GetSteamId();

            // This still triggers the UserStatsReceived callback, in addition to the callresult.
            // No need to implement callresults for the time being.
            var callHandle = this._SteamClient.SteamUserStats.RequestUserStats(steamId);
            if (callHandle == API.CallHandle.Invalid)
            {
                MessageBox.Show(this, "Failed.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            this._GameStatusLabel.Text = "Retrieving stat information...";
            this.DisableInput();
        }

        private bool _IsUpdatingAchievementList;

        private void GetAchievements()
        {
            var textSearch = this._MatchingStringTextBox.Text.Length > 0
                ? this._MatchingStringTextBox.Text
                : null;

            this._IsUpdatingAchievementList = true;

            this._AchievementListView.Items.Clear();
            this._AchievementListView.BeginUpdate();
            //this.Achievements.Clear();

            bool wantLocked = this._DisplayLockedOnlyButton.Checked == true;
            bool wantUnlocked = this._DisplayUnlockedOnlyButton.Checked == true;

            foreach (var def in this._AchievementDefinitions)
            {
                if (string.IsNullOrEmpty(def.Id) == true)
                {
                    continue;
                }

                if (this._SteamClient.SteamUserStats.GetAchievementAndUnlockTime(
                    def.Id,
                    out bool isAchieved,
                    out var unlockTime) == false)
                {
                    continue;
                }

                bool wanted = (wantLocked == false && wantUnlocked == false) || isAchieved switch
                {
                    true => wantUnlocked,
                    false => wantLocked,
                };
                if (wanted == false)
                {
                    continue;
                }

                if (textSearch != null)
                {
                    if (def.Name.IndexOf(textSearch, StringComparison.OrdinalIgnoreCase) < 0 &&
                        def.Description.IndexOf(textSearch, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                }

                Stats.AchievementInfo info = new()
                {
                    Id = def.Id,
                    IsAchieved = isAchieved,
                    UnlockTime = isAchieved == true && unlockTime > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(unlockTime).LocalDateTime
                        : null,
                    IconNormal = string.IsNullOrEmpty(def.IconNormal) ? null : def.IconNormal,
                    IconLocked = string.IsNullOrEmpty(def.IconLocked) ? def.IconNormal : def.IconLocked,
                    Permission = def.Permission,
                    Name = def.Name,
                    Description = def.Description,
                };

                ListViewItem item = new()
                {
                    Checked = isAchieved,
                    Tag = info,
                    Text = info.Name,
                    BackColor = (def.Permission & 3) == 0 ? Color.FromArgb(14, 17, 22) : Color.FromArgb(64, 0, 0),
                    ForeColor = Color.FromArgb(232, 238, 247),
                };

                info.Item = item;

                if (item.Text.StartsWith("#", StringComparison.InvariantCulture) == true)
                {
                    item.Text = info.Id;
                    item.SubItems.Add("");
                }
                else
                {
                    item.SubItems.Add(info.Description);
                }

                item.SubItems.Add(info.UnlockTime.HasValue == true
                    ? info.UnlockTime.Value.ToString()
                    : "");

                info.ImageIndex = 0;

                this.AddAchievementToIconQueue(info, false);
                this._AchievementListView.Items.Add(item);
            }

            this._AchievementListView.EndUpdate();
            this._IsUpdatingAchievementList = false;

            this.DownloadNextIcon();
        }

        private void GetStatistics()
        {
            this._Statistics.Clear();
            foreach (var stat in this._StatDefinitions)
            {
                if (string.IsNullOrEmpty(stat.Id) == true)
                {
                    continue;
                }

                if (stat is Stats.IntegerStatDefinition intStat)
                {
                    if (this._SteamClient.SteamUserStats.GetStatValue(intStat.Id, out int value) == false)
                    {
                        continue;
                    }
                    this._Statistics.Add(new Stats.IntStatInfo()
                    {
                        Id = intStat.Id,
                        DisplayName = intStat.DisplayName,
                        IntValue = value,
                        OriginalValue = value,
                        IsIncrementOnly = intStat.IncrementOnly,
                        Permission = intStat.Permission,
                    });
                }
                else if (stat is Stats.FloatStatDefinition floatStat)
                {
                    if (this._SteamClient.SteamUserStats.GetStatValue(floatStat.Id, out float value) == false)
                    {
                        continue;
                    }
                    this._Statistics.Add(new Stats.FloatStatInfo()
                    {
                        Id = floatStat.Id,
                        DisplayName = floatStat.DisplayName,
                        FloatValue = value,
                        OriginalValue = value,
                        IsIncrementOnly = floatStat.IncrementOnly,
                        Permission = floatStat.Permission,
                    });
                }
            }
        }

        private void AddAchievementToIconQueue(Stats.AchievementInfo info, bool startDownload)
        {
            int imageIndex = this._AchievementImageList.Images.IndexOfKey(
                info.IsAchieved == true ? info.IconNormal : info.IconLocked);

            if (imageIndex >= 0)
            {
                info.ImageIndex = imageIndex;
            }
            else
            {
                this._IconQueue.Add(info);

                if (startDownload == true)
                {
                    this.DownloadNextIcon();
                }
            }
        }

        private int StoreAchievements()
        {
            if (this._AchievementListView.Items.Count == 0)
            {
                return 0;
            }

            List<Stats.AchievementInfo> achievements = new();
            foreach (ListViewItem item in this._AchievementListView.Items)
            {
                if (item.Tag is not Stats.AchievementInfo achievementInfo ||
                    achievementInfo.IsAchieved == item.Checked)
                {
                    continue;
                }

                achievementInfo.IsAchieved = item.Checked;
                achievements.Add(achievementInfo);
            }

            if (achievements.Count == 0)
            {
                return 0;
            }

            foreach (var info in achievements)
            {
                if (this._SteamClient.SteamUserStats.SetAchievement(info.Id, info.IsAchieved) == false)
                {
                    MessageBox.Show(
                        this,
                        $"An error occurred while setting the state for {info.Id}, aborting store.",
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return -1;
                }
            }

            return achievements.Count;
        }

        private int StoreStatistics()
        {
            if (this._Statistics.Count == 0)
            {
                return 0;
            }

            var statistics = this._Statistics.Where(stat => stat.IsModified == true).ToList();
            if (statistics.Count == 0)
            {
                return 0;
            }

            foreach (var stat in statistics)
            {
                if (stat is Stats.IntStatInfo intStat)
                {
                    if (this._SteamClient.SteamUserStats.SetStatValue(
                        intStat.Id,
                        intStat.IntValue) == false)
                    {
                        MessageBox.Show(
                            this,
                            $"An error occurred while setting the value for {stat.Id}, aborting store.",
                            "Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return -1;
                    }
                }
                else if (stat is Stats.FloatStatInfo floatStat)
                {
                    if (this._SteamClient.SteamUserStats.SetStatValue(
                        floatStat.Id,
                        floatStat.FloatValue) == false)
                    {
                        MessageBox.Show(
                            this,
                            $"An error occurred while setting the value for {stat.Id}, aborting store.",
                            "Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return -1;
                    }
                }
                else
                {
                    throw new InvalidOperationException("unsupported stat type");
                }
            }

            return statistics.Count;
        }

        private void DisableInput()
        {
            this._ReloadButton.Enabled = false;
            this._StoreButton.Enabled = false;
        }

        private void EnableInput()
        {
            this._ReloadButton.Enabled = true;
            this._StoreButton.Enabled = true;
        }

        private void OnTimer(object sender, EventArgs e)
        {
            this._CallbackTimer.Enabled = false;
            this._SteamClient.RunCallbacks(false);
            this._CallbackTimer.Enabled = true;
        }

        private void OnRefresh(object sender, EventArgs e)
        {
            this.RefreshStats();
        }

        private void OnLockAll(object sender, EventArgs e)
        {
            foreach (ListViewItem item in this._AchievementListView.Items)
            {
                item.Checked = false;
            }
        }

        private void OnInvertAll(object sender, EventArgs e)
        {
            foreach (ListViewItem item in this._AchievementListView.Items)
            {
                item.Checked = !item.Checked;
            }
        }

        private void OnUnlockAll(object sender, EventArgs e)
        {
            foreach (ListViewItem item in this._AchievementListView.Items)
            {
                item.Checked = true;
            }
        }

        private bool Store()
        {
            if (this._SteamClient.SteamUserStats.StoreStats() == false)
            {
                MessageBox.Show(
                    this,
                    "An error occurred while storing, aborting.",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }

            return true;
        }

        private void OnStore(object sender, EventArgs e)
        {
            int achievements = this.StoreAchievements();
            if (achievements < 0)
            {
                this.RefreshStats();
                return;
            }

            int stats = this.StoreStatistics();
            if (stats < 0)
            {
                this.RefreshStats();
                return;
            }

            if (this.Store() == false)
            {
                this.RefreshStats();
                return;
            }

            MessageBox.Show(
                this,
                $"Stored {achievements} achievements and {stats} statistics.",
                "Information",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            this.RefreshStats();
        }

        private void OnStatDataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            if (e.Context != DataGridViewDataErrorContexts.Commit)
            {
                return;
            }

            var view = (DataGridView)sender;
            if (e.Exception is Stats.StatIsProtectedException)
            {
                e.ThrowException = false;
                e.Cancel = true;
                view.Rows[e.RowIndex].ErrorText = "Stat is protected! -- you can't modify it";
            }
            else
            {
                e.ThrowException = false;
                e.Cancel = true;
                view.Rows[e.RowIndex].ErrorText = "Invalid value";
            }
        }

        private void OnStatAgreementChecked(object sender, EventArgs e)
        {
            this._StatisticsDataGridView.Columns[1].ReadOnly = this._EnableStatsEditingCheckBox.Checked == false;
        }

        private void OnStatCellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            var view = (DataGridView)sender;
            view.Rows[e.RowIndex].ErrorText = "";
        }

        private void OnResetAllStats(object sender, EventArgs e)
        {
            if (MessageBox.Show(
                "Are you absolutely sure you want to reset stats?",
                "Warning",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) == DialogResult.No)
            {
                return;
            }

            bool achievementsToo = DialogResult.Yes == MessageBox.Show(
                "Do you want to reset achievements too?",
                "Question",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (MessageBox.Show(
                "Really really sure?",
                "Warning",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Error) == DialogResult.No)
            {
                return;
            }

            if (this._SteamClient.SteamUserStats.ResetAllStats(achievementsToo) == false)
            {
                MessageBox.Show(this, "Failed.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            this.RefreshStats();
        }

        private void OnCheckAchievement(object sender, ItemCheckEventArgs e)
        {
            if (sender != this._AchievementListView)
            {
                return;
            }

            if (this._IsUpdatingAchievementList == true)
            {
                return;
            }

            if (this._AchievementListView.Items[e.Index].Tag is not Stats.AchievementInfo info)
            {
                return;
            }

            if ((info.Permission & 3) != 0)
            {
                MessageBox.Show(
                    this,
                    "Sorry, but this is a protected achievement and cannot be managed with Steam Achievement Manager.",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                e.NewValue = e.CurrentValue;
            }
        }

        private void OnDisplayUncheckedOnly(object sender, EventArgs e)
        {
            if ((sender as ToolStripButton).Checked == true)
            {
                this._DisplayLockedOnlyButton.Checked = false;
            }

            this.GetAchievements();
        }

        private void OnDisplayCheckedOnly(object sender, EventArgs e)
        {
            if ((sender as ToolStripButton).Checked == true)
            {
                this._DisplayUnlockedOnlyButton.Checked = false;
            }

            this.GetAchievements();
        }

        private void OnFilterUpdate(object sender, KeyEventArgs e)
        {
            this.GetAchievements();
        }

        private void ConfigureModernGameUi()
        {
            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? this.Icon;
            this.BackColor = Color.FromArgb(18, 21, 27);
            this.ForeColor = Color.FromArgb(232, 238, 247);
            this.FormBorderStyle = FormBorderStyle.None;
            this.MinimumSize = new Size(720, 460);

            ConfigureToolStrip(this._MainToolStrip);
            ConfigureToolStrip(this._AchievementsToolStrip);
            this.CreateGameViewButtons();
            this._MainStatusStrip.BackColor = Color.FromArgb(28, 33, 42);
            this._MainStatusStrip.ForeColor = Color.FromArgb(170, 187, 204);
            this._MainStatusStrip.Renderer = new DarkToolStripRenderer();
            this._MainStatusStrip.SizingGrip = false;

            this._MatchingStringTextBox.Size = new Size(190, 24);
            this._MatchingStringTextBox.BackColor = Color.FromArgb(22, 26, 34);
            this._MatchingStringTextBox.ForeColor = Color.FromArgb(232, 238, 247);
            this._MatchingStringTextBox.BorderStyle = BorderStyle.FixedSingle;
            this._MatchingStringTextBox.Control.BackColor = Color.FromArgb(22, 26, 34);
            this._MatchingStringTextBox.Control.ForeColor = Color.FromArgb(232, 238, 247);

            this._MainTabControl.DrawMode = TabDrawMode.OwnerDrawFixed;
            this._MainTabControl.Appearance = TabAppearance.FlatButtons;
            this._MainTabControl.Padding = Point.Empty;
            this._MainTabControl.DrawItem += this.OnMainTabDrawItem;
            this._MainTabControl.BackColor = Color.FromArgb(18, 21, 27);
            Native.ApplyExplorerDarkTheme(this._MainTabControl.Handle);
            this._AchievementsTabPage.BackColor = Color.FromArgb(18, 21, 27);
            this._AchievementsTabPage.ForeColor = Color.FromArgb(232, 238, 247);
            this._AchievementsTabPage.Padding = Padding.Empty;
            this._AchievementsToolStrip.Location = Point.Empty;
            this._AchievementListView.Location = new Point(0, this._AchievementsToolStrip.Height);
            this._StatisticsTabPage.BackColor = Color.FromArgb(18, 21, 27);
            this._StatisticsTabPage.ForeColor = Color.FromArgb(232, 238, 247);
            this._StatisticsTabPage.Padding = Padding.Empty;
            this._EnableStatsEditingCheckBox.BackColor = Color.FromArgb(18, 21, 27);
            this._EnableStatsEditingCheckBox.ForeColor = Color.FromArgb(232, 238, 247);

            this._AchievementListView.BackColor = Color.FromArgb(14, 17, 22);
            this._AchievementListView.BorderStyle = BorderStyle.None;
            this._AchievementListView.ForeColor = Color.FromArgb(232, 238, 247);
            this._AchievementListView.GridLines = false;
            this._AchievementListView.OwnerDraw = true;
            this._AchievementListView.DrawColumnHeader += this.OnAchievementColumnHeaderDraw;
            this._AchievementListView.DrawItem += this.OnAchievementItemDraw;
            this._AchievementListView.DrawSubItem += this.OnAchievementSubItemDraw;
            this._AchievementListView.Resize += (sender, e) => this.AdjustAchievementColumns();
            this._AchievementsTabPage.Resize += (sender, e) => this.PositionAchievementChromeCovers();
            this.CreateAchievementChromeCovers();

            this._StatisticsDataGridView.BackgroundColor = Color.FromArgb(14, 17, 22);
            this._StatisticsDataGridView.BorderStyle = BorderStyle.None;
            this._StatisticsDataGridView.GridColor = Color.FromArgb(54, 64, 78);
            this._StatisticsDataGridView.EnableHeadersVisualStyles = false;
            this._StatisticsDataGridView.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(28, 33, 42);
            this._StatisticsDataGridView.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(232, 238, 247);
            this._StatisticsDataGridView.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(28, 33, 42);
            this._StatisticsDataGridView.DefaultCellStyle.BackColor = Color.FromArgb(14, 17, 22);
            this._StatisticsDataGridView.DefaultCellStyle.ForeColor = Color.FromArgb(232, 238, 247);
            this._StatisticsDataGridView.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 120, 215);
            this._StatisticsDataGridView.DefaultCellStyle.SelectionForeColor = Color.White;
            this._StatisticsDataGridView.RowHeadersDefaultCellStyle.BackColor = Color.FromArgb(28, 33, 42);
            this._StatisticsDataGridView.RowHeadersDefaultCellStyle.ForeColor = Color.FromArgb(232, 238, 247);

            Native.ApplyImmersiveDarkMode(this.Handle);
            Native.ApplyExplorerDarkTheme(this._AchievementListView.Handle);
            Native.ApplyExplorerDarkTheme(this._StatisticsDataGridView.Handle);
            Native.ApplyExplorerDarkTheme(this._MatchingStringTextBox.Control.Handle);
            this.AdjustAchievementColumns();
        }

        private void CreateTabBorderCovers()
        {
            this._TabLeftBorderCover = CreateDarkCover();
            this._TabRightBorderCover = CreateDarkCover();
            this._TabBottomBorderCover = CreateDarkCover();

            this.Controls.Add(this._TabLeftBorderCover);
            this.Controls.Add(this._TabRightBorderCover);
            this.Controls.Add(this._TabBottomBorderCover);
            this.Resize += (sender, e) => this.PositionTabBorderCovers();
            this.PositionTabBorderCovers();
        }

        private void PositionTabBorderCovers()
        {
            if (this._TabLeftBorderCover == null)
            {
                return;
            }

            var bounds = this._MainTabControl.Bounds;
            this._TabLeftBorderCover.Bounds = new Rectangle(bounds.Left, bounds.Top + 22, 5, bounds.Height - 22);
            this._TabRightBorderCover.Bounds = new Rectangle(bounds.Right - 5, bounds.Top + 22, 5, bounds.Height - 22);
            this._TabBottomBorderCover.Bounds = new Rectangle(bounds.Left, bounds.Bottom - 5, bounds.Width, 5);

            this._TabLeftBorderCover.BringToFront();
            this._TabRightBorderCover.BringToFront();
            this._TabBottomBorderCover.BringToFront();
        }

        private void AdjustAchievementColumns()
        {
            int availableWidth = this._AchievementListView.ClientSize.Width - 1;
            if (availableWidth <= 0)
            {
                return;
            }

            const int nameWidth = 200;
            const int unlockTimeWidth = 160;
            int descriptionWidth = Math.Max(240, availableWidth - nameWidth - unlockTimeWidth);

            this._AchievementNameColumnHeader.Width = nameWidth;
            this._AchievementDescriptionColumnHeader.Width = descriptionWidth;
            this._AchievementUnlockTimeColumnHeader.Width = unlockTimeWidth;
            this.PositionAchievementChromeCovers();
        }

        private void CreateAchievementChromeCovers()
        {
            this._AchievementHeaderCornerCover = CreateDarkCover();
            this._AchievementLeftEdgeCover = CreateDarkCover();
            this._AchievementRightEdgeCover = CreateDarkCover();
            this._AchievementBottomEdgeCover = CreateDarkCover();

            this._AchievementsTabPage.Controls.Add(this._AchievementHeaderCornerCover);
            this._AchievementsTabPage.Controls.Add(this._AchievementLeftEdgeCover);
            this._AchievementsTabPage.Controls.Add(this._AchievementRightEdgeCover);
            this._AchievementsTabPage.Controls.Add(this._AchievementBottomEdgeCover);
            this.PositionAchievementChromeCovers();
        }

        private static Panel CreateDarkCover()
        {
            return new Panel
            {
                BackColor = Color.FromArgb(14, 17, 22),
                Enabled = false,
                Visible = true,
            };
        }

        private void PositionAchievementChromeCovers()
        {
            if (this._AchievementHeaderCornerCover == null)
            {
                return;
            }

            var bounds = this._AchievementListView.Bounds;
            int headerHeight = Math.Max(22, this.Font.Height + 8);
            int scrollWidth = SystemInformation.VerticalScrollBarWidth + 8;
            int scrollHeight = SystemInformation.HorizontalScrollBarHeight + 8;

            this._AchievementHeaderCornerCover.Bounds = new Rectangle(
                bounds.Right - scrollWidth,
                bounds.Top,
                scrollWidth,
                headerHeight);
            this._AchievementLeftEdgeCover.Bounds = new Rectangle(bounds.Left, bounds.Top, 5, bounds.Height);
            this._AchievementRightEdgeCover.Bounds = new Rectangle(bounds.Right - scrollWidth, bounds.Top, scrollWidth, bounds.Height);
            this._AchievementBottomEdgeCover.Bounds = new Rectangle(bounds.Left, bounds.Bottom - scrollHeight, bounds.Width, scrollHeight);

            this._AchievementHeaderCornerCover.BringToFront();
            this._AchievementLeftEdgeCover.BringToFront();
            this._AchievementRightEdgeCover.BringToFront();
            this._AchievementBottomEdgeCover.BringToFront();
        }

        private static void ConfigureToolStrip(ToolStrip toolStrip)
        {
            toolStrip.BackColor = Color.FromArgb(28, 33, 42);
            toolStrip.ForeColor = Color.FromArgb(232, 238, 247);
            toolStrip.GripStyle = ToolStripGripStyle.Hidden;
            toolStrip.Renderer = new DarkToolStripRenderer();
        }

        private void CreateGameViewButtons()
        {
            this._AchievementsViewButton = new ToolStripButton("Achievements")
            {
                Checked = true,
                CheckOnClick = false,
                DisplayStyle = ToolStripItemDisplayStyle.Text,
            };
            this._AchievementsViewButton.Click += (sender, e) => this.SelectGameView(this._AchievementsTabPage);

            this._StatisticsViewButton = new ToolStripButton("Statistics")
            {
                CheckOnClick = false,
                DisplayStyle = ToolStripItemDisplayStyle.Text,
            };
            this._StatisticsViewButton.Click += (sender, e) => this.SelectGameView(this._StatisticsTabPage);

            this._MainToolStrip.Items.Insert(0, new ToolStripSeparator());
            this._MainToolStrip.Items.Insert(0, this._StatisticsViewButton);
            this._MainToolStrip.Items.Insert(0, this._AchievementsViewButton);
        }

        private void SelectGameView(TabPage tabPage)
        {
            this._MainTabControl.SelectedTab = tabPage;
            this._AchievementsViewButton.Checked = tabPage == this._AchievementsTabPage;
            this._StatisticsViewButton.Checked = tabPage == this._StatisticsTabPage;
            this.PositionTabBorderCovers();
            this.PositionAchievementChromeCovers();
        }

        private void OnMainTabDrawItem(object sender, DrawItemEventArgs e)
        {
            var selected = e.Index == this._MainTabControl.SelectedIndex;
            var bounds = e.Bounds;
            using SolidBrush background = new(selected ? Color.FromArgb(28, 33, 42) : Color.FromArgb(18, 21, 27));
            using SolidBrush foreground = new(Color.FromArgb(232, 238, 247));
            e.Graphics.FillRectangle(background, bounds);
            TextRenderer.DrawText(
                e.Graphics,
                this._MainTabControl.TabPages[e.Index].Text,
                this.Font,
                bounds,
                foreground.Color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void OnAchievementColumnHeaderDraw(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using SolidBrush background = new(Color.FromArgb(28, 33, 42));
            using Pen border = new(Color.FromArgb(54, 64, 78));
            e.Graphics.FillRectangle(background, e.Bounds);
            e.Graphics.DrawRectangle(border, e.Bounds);
            TextRenderer.DrawText(
                e.Graphics,
                e.Header.Text,
                this.Font,
                new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
                Color.FromArgb(232, 238, 247),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void OnAchievementItemDraw(object sender, DrawListViewItemEventArgs e)
        {
        }

        private void OnAchievementSubItemDraw(object sender, DrawListViewSubItemEventArgs e)
        {
            var selected = e.Item.Selected;
            var protectedItem = e.Item.BackColor.R > 40 && e.Item.BackColor.G == 0;
            Color backgroundColor = selected
                ? Color.FromArgb(0, 120, 215)
                : protectedItem
                    ? Color.FromArgb(64, 0, 0)
                    : Color.FromArgb(14, 17, 22);
            using SolidBrush background = new(backgroundColor);
            e.Graphics.FillRectangle(background, e.Bounds);
            using Pen border = new(Color.FromArgb(42, 49, 60));
            e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            e.Graphics.DrawLine(border, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);

            if (e.ColumnIndex == 0)
            {
                var checkState = e.Item.Checked ? CheckBoxState.CheckedNormal : CheckBoxState.UncheckedNormal;
                var checkBoxBounds = new Rectangle(e.Bounds.X + 6, e.Bounds.Y + (e.Bounds.Height - 14) / 2, 14, 14);
                CheckBoxRenderer.DrawCheckBox(e.Graphics, checkBoxBounds.Location, checkState);

                if (e.Item.ImageIndex >= 0 && e.Item.ImageIndex < this._AchievementImageList.Images.Count)
                {
                    var image = this._AchievementImageList.Images[e.Item.ImageIndex];
                    e.Graphics.DrawImage(image, e.Bounds.X + 28, e.Bounds.Y + 4, 56, 56);
                }

                TextRenderer.DrawText(
                    e.Graphics,
                    e.SubItem.Text,
                    this.Font,
                    new Rectangle(e.Bounds.X + 92, e.Bounds.Y, e.Bounds.Width - 96, e.Bounds.Height),
                    Color.FromArgb(232, 238, 247),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }

            TextRenderer.DrawText(
                e.Graphics,
                e.SubItem.Text,
                this.Font,
                new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height),
                Color.FromArgb(232, 238, 247),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void CreateModernTitleBar()
        {
            this._TitleBar = new Panel
            {
                BackColor = Color.FromArgb(11, 14, 19),
                Dock = DockStyle.Top,
                Height = 34,
            };
            this._TitleBar.MouseDown += this.OnTitleBarMouseDown;

            this._TitleLabel = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(232, 238, 247),
                Padding = new Padding(12, 0, 0, 0),
                Text = this.Text,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
            };
            this._TitleLabel.MouseDown += this.OnTitleBarMouseDown;

            this._CloseButton = CreateWindowButton("X", Color.FromArgb(201, 64, 75));
            this._CloseButton.Dock = DockStyle.Right;
            this._CloseButton.Click += (sender, e) => this.Close();

            this._MaximizeButton = CreateWindowButton("□", Color.FromArgb(68, 78, 92));
            this._MaximizeButton.Dock = DockStyle.Right;
            this._MaximizeButton.Click += (sender, e) => this.ToggleWindowState();

            this._MinimizeButton = CreateWindowButton("-", Color.FromArgb(68, 78, 92));
            this._MinimizeButton.Dock = DockStyle.Right;
            this._MinimizeButton.Click += (sender, e) => this.WindowState = FormWindowState.Minimized;

            this._TitleBar.Controls.Add(this._TitleLabel);
            this._TitleBar.Controls.Add(this._MinimizeButton);
            this._TitleBar.Controls.Add(this._MaximizeButton);
            this._TitleBar.Controls.Add(this._CloseButton);
            this.Controls.Add(this._TitleBar);
        }

        private void ToggleWindowState()
        {
            this.WindowState = this.WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
        }

        private static Button CreateWindowButton(string text, Color hoverColor)
        {
            Button button = new()
            {
                BackColor = Color.FromArgb(11, 14, 19),
                Dock = DockStyle.Right,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(232, 238, 247),
                Size = new Size(46, 34),
                TabStop = false,
                Text = text,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
            };
            button.FlatAppearance.BorderSize = 0;
            button.MouseEnter += (sender, e) => button.BackColor = hoverColor;
            button.MouseLeave += (sender, e) => button.BackColor = Color.FromArgb(11, 14, 19);
            return button;
        }

        private void OnTitleBarMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            Native.ReleaseCapture();
            Native.SendMessage(this.Handle, Native.WmNclButtonDown, Native.HtCaption, 0);
        }

        protected override void WndProc(ref Message m)
        {
            const int resizeBorder = 8;

            if (m.Msg == Native.WmNcHitTest && this.WindowState == FormWindowState.Normal)
            {
                base.WndProc(ref m);
                if ((int)m.Result == Native.HtClient)
                {
                    var point = this.PointToClient(new Point(m.LParam.ToInt32()));
                    bool left = point.X <= resizeBorder;
                    bool right = point.X >= this.ClientSize.Width - resizeBorder;
                    bool top = point.Y <= resizeBorder;
                    bool bottom = point.Y >= this.ClientSize.Height - resizeBorder;

                    if (left && top)
                    {
                        m.Result = (IntPtr)Native.HtTopLeft;
                    }
                    else if (right && top)
                    {
                        m.Result = (IntPtr)Native.HtTopRight;
                    }
                    else if (left && bottom)
                    {
                        m.Result = (IntPtr)Native.HtBottomLeft;
                    }
                    else if (right && bottom)
                    {
                        m.Result = (IntPtr)Native.HtBottomRight;
                    }
                    else if (left)
                    {
                        m.Result = (IntPtr)Native.HtLeft;
                    }
                    else if (right)
                    {
                        m.Result = (IntPtr)Native.HtRight;
                    }
                    else if (top)
                    {
                        m.Result = (IntPtr)Native.HtTop;
                    }
                    else if (bottom)
                    {
                        m.Result = (IntPtr)Native.HtBottom;
                    }
                }

                return;
            }

            base.WndProc(ref m);
        }

        private static class Native
        {
            public const int WmNcHitTest = 0x0084;
            public const int WmNclButtonDown = 0x00A1;
            public const int HtClient = 0x0001;
            public const int HtCaption = 0x0002;
            public const int HtLeft = 0x000A;
            public const int HtRight = 0x000B;
            public const int HtTop = 0x000C;
            public const int HtTopLeft = 0x000D;
            public const int HtTopRight = 0x000E;
            public const int HtBottom = 0x000F;
            public const int HtBottomLeft = 0x0010;
            public const int HtBottomRight = 0x0011;

            [DllImport("user32.dll")]
            public static extern bool ReleaseCapture();

            [DllImport("user32.dll")]
            public static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

            [DllImport("dwmapi.dll")]
            private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

            [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
            private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

            public static void ApplyImmersiveDarkMode(IntPtr handle)
            {
                int enabled = 1;
                if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
                }
            }

            public static void ApplyExplorerDarkTheme(IntPtr handle)
            {
                SetWindowTheme(handle, "DarkMode_Explorer", null);
            }
        }

        private sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
        {
            public DarkToolStripRenderer()
                : base(new DarkColorTable())
            {
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using Pen pen = new(Color.FromArgb(42, 49, 60));
                e.Graphics.DrawLine(pen, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                using Pen pen = new(Color.FromArgb(66, 76, 92));
                int x = e.Item.Width / 2;
                e.Graphics.DrawLine(pen, x, 6, x, e.Item.Height - 6);
            }
        }

        private sealed class DarkColorTable : ProfessionalColorTable
        {
            public override Color ToolStripGradientBegin => Color.FromArgb(28, 33, 42);
            public override Color ToolStripGradientMiddle => Color.FromArgb(28, 33, 42);
            public override Color ToolStripGradientEnd => Color.FromArgb(28, 33, 42);
            public override Color MenuStripGradientBegin => Color.FromArgb(28, 33, 42);
            public override Color MenuStripGradientEnd => Color.FromArgb(28, 33, 42);
            public override Color ImageMarginGradientBegin => Color.FromArgb(28, 33, 42);
            public override Color ImageMarginGradientMiddle => Color.FromArgb(28, 33, 42);
            public override Color ImageMarginGradientEnd => Color.FromArgb(28, 33, 42);
            public override Color ButtonSelectedGradientBegin => Color.FromArgb(42, 49, 60);
            public override Color ButtonSelectedGradientMiddle => Color.FromArgb(42, 49, 60);
            public override Color ButtonSelectedGradientEnd => Color.FromArgb(42, 49, 60);
            public override Color ButtonPressedGradientBegin => Color.FromArgb(54, 64, 78);
            public override Color ButtonPressedGradientMiddle => Color.FromArgb(54, 64, 78);
            public override Color ButtonPressedGradientEnd => Color.FromArgb(54, 64, 78);
            public override Color MenuItemSelected => Color.FromArgb(42, 49, 60);
            public override Color MenuItemSelectedGradientBegin => Color.FromArgb(42, 49, 60);
            public override Color MenuItemSelectedGradientEnd => Color.FromArgb(42, 49, 60);
            public override Color MenuItemBorder => Color.FromArgb(72, 86, 106);
            public override Color ToolStripBorder => Color.FromArgb(42, 49, 60);
        }
    }
}
