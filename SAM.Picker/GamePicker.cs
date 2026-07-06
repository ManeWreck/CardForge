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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.XPath;
using static SAM.Picker.InvariantShorthand;
using APITypes = SAM.API.Types;

namespace SAM.Picker
{
    internal partial class GamePicker : Form
    {
        private readonly API.Client _SteamClient;

        private readonly Dictionary<uint, GameInfo> _Games;
        private readonly List<GameInfo> _FilteredGames;

        private readonly object _LogoLock;
        private readonly HashSet<string> _LogosAttempting;
        private readonly HashSet<string> _LogosAttempted;
        private readonly ConcurrentQueue<GameInfo> _LogoQueue;

        private readonly API.Callbacks.AppDataChanged _AppDataChangedCallback;

        private readonly ContextMenuStrip _LibraryContextMenu;
        private Panel _TitleBar;
        private Label _TitleLabel;
        private Button _MinimizeButton;
        private Button _MaximizeButton;
        private Button _CloseButton;
        private Panel _GameHubPanel;
        private Label _GameHubTitleLabel;
        private Label _GameHubMetaLabel;
        private Button _GameHubOpenButton;
        private Button _GameHubStoreButton;
        private Button _GameHubCardsButton;
        private Button _GameHubAchievementsButton;
        private Button _GameHubCopyButton;
        private TableLayoutPanel _RootLayoutPanel;
        private TableLayoutPanel _LibraryLayoutPanel;
        private Panel _ListAreaPanel;
        private Panel _TableHostPanel;
        private DataGridView _TableGridView;
        private ToolStripDropDownButton _ViewModeDropDownButton;
        private ToolStripMenuItem _TileViewMenuItem;
        private ToolStripMenuItem _ListViewMenuItem;
        private ToolStripMenuItem _TableViewMenuItem;
        private ToolStripButton _LoadCardDataButton;
        private ToolStripButton _CardsRemainingFilterButton;
        private ToolStripButton _LaunchCardGamesButton;
        private ToolStripButton _FavoritesFilterButton;
        private ToolStripButton _ToggleFavoriteButton;
        private ToolStripButton _SchedulerButton;
        private Dictionary<uint, int> _PlaytimeByAppId;
        private readonly Dictionary<uint, int> _CardDropsByAppId;
        private readonly List<Process> _OpenedGameProcesses;
        private readonly Dictionary<uint, Process> _OpenedGameProcessesByAppId;
        private readonly Dictionary<int, IntPtr> _OpenedGameWindows;
        private readonly HashSet<uint> _CommunityStatsRequested;
        private readonly HashSet<uint> _FavoriteAppIds;
        private View _CurrentLibraryView;
        private NotifyIcon _TrayIcon;
        private ContextMenuStrip _TrayMenu;
        private ToolStripMenuItem _OpenGameWindowsMenuItem;
        private bool _RefreshingCardDrops;
        private bool _AllowExit;
        private int _SortColumn;
        private bool _SortAscending;

        public GamePicker(API.Client client)
        {
            this._Games = new();
            this._FilteredGames = new();
            this._LogoLock = new();
            this._LogosAttempting = new();
            this._LogosAttempted = new();
            this._LogoQueue = new();
            this._PlaytimeByAppId = LoadLocalPlaytimeByAppId(client);
            this._CardDropsByAppId = new();
            this._OpenedGameProcesses = new();
            this._OpenedGameProcessesByAppId = new();
            this._OpenedGameWindows = new();
            this._CommunityStatsRequested = new();
            this._FavoriteAppIds = LoadFavoriteAppIds();
            this._CurrentLibraryView = View.LargeIcon;
            this._SortColumn = 0;
            this._SortAscending = true;

            this.InitializeComponent();
            this._LibraryContextMenu = this.CreateLibraryContextMenu();
            this.ConfigureModernLibraryUi();
            this.CreateModernTitleBar();
            this.CreateGameHubPanel();
            this.CreateTrayIcon();

            Bitmap blank = new(this._LogoImageList.ImageSize.Width, this._LogoImageList.ImageSize.Height);
            using (var g = Graphics.FromImage(blank))
            {
                g.Clear(Color.FromArgb(35, 39, 47));
            }

            this._LogoImageList.Images.Add("Blank", blank);

            this._SteamClient = client;

            this._AppDataChangedCallback = client.CreateAndRegisterCallback<API.Callbacks.AppDataChanged>();
            this._AppDataChangedCallback.OnRun += this.OnAppDataChanged;

            this.AddGames();
        }

        private void OnAppDataChanged(APITypes.AppDataChanged param)
        {
            if (param.Result == false)
            {
                return;
            }

            if (this._Games.TryGetValue(param.Id, out var game) == false)
            {
                return;
            }

            game.Name = this._SteamClient.SteamApps001.GetAppData(game.Id, "name");

            this.AddGameToLogoQueue(game);
            this.DownloadNextLogo();
        }

        private void DoDownloadList(object sender, DoWorkEventArgs e)
        {
            this._PickerStatusLabel.Text = "Downloading game list...";

            byte[] bytes;
            using (WebClient downloader = new())
            {
                bytes = downloader.DownloadData(new Uri("https://gib.me/sam/games.xml"));
            }

            List<KeyValuePair<uint, string>> pairs = new();
            using (MemoryStream stream = new(bytes, false))
            {
                XPathDocument document = new(stream);
                var navigator = document.CreateNavigator();
                var nodes = navigator.Select("/games/game");
                while (nodes.MoveNext() == true)
                {
                    string type = nodes.Current.GetAttribute("type", "");
                    if (string.IsNullOrEmpty(type) == true)
                    {
                        type = "normal";
                    }
                    pairs.Add(new((uint)nodes.Current.ValueAsLong, type));
                }
            }

            this._PickerStatusLabel.Text = "Checking game ownership...";
            foreach (var kv in pairs)
            {
                this.AddGame(kv.Key, kv.Value);
            }
        }

        private void OnDownloadList(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error != null || e.Cancelled == true)
            {
                this.AddDefaultGames();
                MessageBox.Show(e.Error.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            this.RefreshGames();
            this._RefreshGamesButton.Enabled = true;
            this.DownloadNextLogo();
        }

        private void RefreshGames()
        {
            var nameSearch = this._SearchGameTextBox.Text.Length > 0
                ? this._SearchGameTextBox.Text.Trim()
                : null;

            var wantNormals = this._FilterGamesMenuItem.Checked == true;
            var wantDemos = this._FilterDemosMenuItem.Checked == true;
            var wantMods = this._FilterModsMenuItem.Checked == true;
            var wantJunk = this._FilterJunkMenuItem.Checked == true;
            var wantCardsRemaining = this._CardsRemainingFilterButton?.Checked == true;
            var wantFavorites = this._FavoritesFilterButton?.Checked == true;

            this._FilteredGames.Clear();
            foreach (var info in this._Games.Values.OrderBy(gi => gi.Name))
            {
                if (nameSearch != null && MatchesSearch(info, nameSearch) == false)
                {
                    continue;
                }

                if (wantCardsRemaining == true && (info.CardDropsRemaining.HasValue == false || info.CardDropsRemaining.Value <= 0))
                {
                    continue;
                }

                if (wantFavorites == true && this._FavoriteAppIds.Contains(info.Id) == false)
                {
                    continue;
                }

                bool wanted = info.Type switch
                {
                    "normal" => wantNormals,
                    "demo" => wantDemos,
                    "mod" => wantMods,
                    "junk" => wantJunk,
                    _ => true,
                };
                if (wanted == false)
                {
                    continue;
                }

                this._FilteredGames.Add(info);
            }

            this.SortFilteredGames();
            this._GameListView.VirtualListSize = this._FilteredGames.Count;
            if (this._CurrentLibraryView == View.Details)
            {
                this.PopulateTableGrid();
            }
            this._PickerStatusLabel.Text =
                $"Displaying {this._FilteredGames.Count} games. Total {this._Games.Count} owned apps.";

            if (this._GameListView.Items.Count > 0)
            {
                this._GameListView.Items[0].Selected = true;
                this._GameListView.Select();
            }
        }

        private void OnGameListViewRetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            var info = this._FilteredGames[e.ItemIndex];
            e.Item = info.Item = new()
            {
                Text = this.GetLibraryItemText(info),
                ImageIndex = this._CurrentLibraryView == View.LargeIcon ? info.ImageIndex : this._CurrentLibraryView == View.Details ? 0 : -1,
                ToolTipText = $"{info.Name}\nAppID: {info.Id}\nType: {GetDisplayType(info.Type)}\nFavorite: {(this.IsFavorite(info) == true ? "yes" : "no")}\nCard drops left: {FormatCardDrops(info)}",
            };
            e.Item.SubItems.Add(info.Id.ToString(CultureInfo.InvariantCulture));
            e.Item.SubItems.Add(FormatPlaytime(info.PlaytimeMinutes));
            e.Item.SubItems.Add(FormatAchievements(info));
            e.Item.SubItems.Add(FormatCardDrops(info));
        }

        private void OnGameListViewSearchForVirtualItem(object sender, SearchForVirtualItemEventArgs e)
        {
            if (e.Direction != SearchDirectionHint.Down || e.IsTextSearch == false)
            {
                return;
            }

            var count = this._FilteredGames.Count;
            if (count < 2)
            {
                return;
            }

            var text = e.Text;
            int startIndex = e.StartIndex;

            Predicate<GameInfo> predicate;
            /*if (e.IsPrefixSearch == true)*/
            {
                predicate = gi => gi.Name != null && gi.Name.StartsWith(text, StringComparison.CurrentCultureIgnoreCase);
            }
            /*else
            {
                predicate = gi => gi.Name != null && string.Compare(gi.Name, text, StringComparison.CurrentCultureIgnoreCase) == 0;
            }*/

            int index;
            if (e.StartIndex >= count)
            {
                // starting from the last item in the list
                index = this._FilteredGames.FindIndex(0, startIndex - 1, predicate);
            }
            else if (startIndex <= 0)
            {
                // starting from the first item in the list
                index = this._FilteredGames.FindIndex(0, count, predicate);
            }
            else
            {
                index = this._FilteredGames.FindIndex(startIndex, count - startIndex, predicate);
                if (index < 0)
                {
                    index = this._FilteredGames.FindIndex(0, startIndex - 1, predicate);
                }
            }

            e.Index = index < 0 ? -1 : index;
        }

        private void DoDownloadLogo(object sender, DoWorkEventArgs e)
        {
            var info = (GameInfo)e.Argument;

            this._LogosAttempted.Add(info.ImageUrl);

            var cachedPath = GetLogoCachePath(info.Id);
            if (File.Exists(cachedPath) == true)
            {
                try
                {
                    e.Result = new LogoInfo(info.Id, LoadCachedBitmap(cachedPath));
                    return;
                }
                catch (Exception)
                {
                    TryDeleteFile(cachedPath);
                }
            }

            using (WebClient downloader = new())
            {
                try
                {
                    var data = downloader.DownloadData(new Uri(info.ImageUrl));
                    e.Result = new LogoInfo(info.Id, CreateCachedBitmap(info.Id, data));
                }
                catch (Exception)
                {
                    e.Result = new LogoInfo(info.Id, null);
                }
            }
        }

        private static Bitmap LoadCachedBitmap(string path)
        {
            using (var stream = new MemoryStream(File.ReadAllBytes(path), false))
            using (var bitmap = new Bitmap(stream))
            {
                return new Bitmap(bitmap);
            }
        }

        private static Bitmap CreateCachedBitmap(uint appId, byte[] data)
        {
            using (var stream = new MemoryStream(data, false))
            using (var bitmap = new Bitmap(stream))
            {
                Bitmap cached = new(bitmap);
                SaveLogoCache(appId, cached);
                return cached;
            }
        }

        private static void SaveLogoCache(uint appId, Bitmap bitmap)
        {
            try
            {
                Directory.CreateDirectory(GetLogoCacheDirectory());
                bitmap.Save(GetLogoCachePath(appId), ImageFormat.Bmp);
            }
            catch (Exception)
            {
            }
        }

        private static string GetLogoCacheDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SAM Modern Library",
                "LogoCache");
        }

        private static string GetLogoCachePath(uint appId)
        {
            return Path.Combine(GetLogoCacheDirectory(), appId.ToString(CultureInfo.InvariantCulture) + ".bmp");
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
            }
        }

        private void OnDownloadLogo(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error != null || e.Cancelled == true)
            {
                return;
            }

            if (e.Result is LogoInfo logoInfo &&
                logoInfo.Bitmap != null &&
                this._Games.TryGetValue(logoInfo.Id, out var gameInfo) == true)
            {
                this._GameListView.BeginUpdate();
                var imageIndex = this._LogoImageList.Images.Count;
                this._LogoImageList.Images.Add(gameInfo.ImageUrl, logoInfo.Bitmap);
                gameInfo.ImageIndex = imageIndex;
                this._GameListView.EndUpdate();
            }

            this.DownloadNextLogo();
        }

        private void DownloadNextLogo()
        {
            lock (this._LogoLock)
            {

                if (this._LogoWorker.IsBusy == true)
                {
                    return;
                }

                GameInfo info;
                while (true)
                {
                    if (this._LogoQueue.TryDequeue(out info) == false)
                    {
                        this._DownloadStatusLabel.Visible = false;
                        return;
                    }

                    if (info.Item == null)
                    {
                        continue;
                    }

                    if (this._FilteredGames.Contains(info) == false ||
                        info.Item.Bounds.IntersectsWith(this._GameListView.ClientRectangle) == false)
                    {
                        this._LogosAttempting.Remove(info.ImageUrl);
                        continue;
                    }

                    break;
                }

                this._DownloadStatusLabel.Text = $"Downloading {1 + this._LogoQueue.Count} game icons...";
                this._DownloadStatusLabel.Visible = true;

                this._LogoWorker.RunWorkerAsync(info);
            }
        }

        private string GetGameImageUrl(uint id)
        {
            string candidate;

            var currentLanguage = this._SteamClient.SteamApps008.GetCurrentGameLanguage();

            candidate = this._SteamClient.SteamApps001.GetAppData(id, _($"small_capsule/{currentLanguage}"));
            if (string.IsNullOrEmpty(candidate) == false)
            {
                return _($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{id}/{candidate}");
            }

            if (currentLanguage != "english")
            {
                candidate = this._SteamClient.SteamApps001.GetAppData(id, "small_capsule/english");
                if (string.IsNullOrEmpty(candidate) == false)
                {
                    return _($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{id}/{candidate}");
                }
            }

            candidate = this._SteamClient.SteamApps001.GetAppData(id, "logo");
            if (string.IsNullOrEmpty(candidate) == false)
            {
                return _($"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{id}/{candidate}.jpg");
            }

            return null;
        }

        private void AddGameToLogoQueue(GameInfo info)
        {
            if (info.ImageIndex > 0)
            {
                return;
            }

            var imageUrl = GetGameImageUrl(info.Id);
            if (string.IsNullOrEmpty(imageUrl) == true)
            {
                return;
            }

            info.ImageUrl = imageUrl;

            int imageIndex = this._LogoImageList.Images.IndexOfKey(imageUrl);
            if (imageIndex >= 0)
            {
                info.ImageIndex = imageIndex;
            }
            else if (
                this._LogosAttempting.Contains(imageUrl) == false &&
                this._LogosAttempted.Contains(imageUrl) == false)
            {
                this._LogosAttempting.Add(imageUrl);
                this._LogoQueue.Enqueue(info);
            }
        }

        private bool OwnsGame(uint id)
        {
            return this._SteamClient.SteamApps008.IsSubscribedApp(id);
        }

        private static bool MatchesSearch(GameInfo info, string search)
        {
            var tokens = search.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                return true;
            }

            var appId = info.Id.ToString(CultureInfo.InvariantCulture);
            foreach (var token in tokens)
            {
                if (info.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    appId.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    info.Type.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        private static string GetDisplayType(string type)
        {
            return type switch
            {
                "normal" => "Game",
                "demo" => "Demo",
                "mod" => "Mod",
                "junk" => "Other",
                _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(type ?? "App"),
            };
        }

        private static bool TryParseAppId(string text, out uint id)
        {
            id = default;
            if (string.IsNullOrWhiteSpace(text) == true)
            {
                return false;
            }

            text = text.Trim();
            if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) == true)
            {
                return true;
            }

            var match = Regex.Match(text, @"(?:store\.steampowered\.com/app/|steam://run/)(?<id>\d+)", RegexOptions.IgnoreCase);
            return match.Success == true &&
                   uint.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) == true;
        }

        private static string FormatPlaytime(int? minutes)
        {
            if (minutes.HasValue == false)
            {
                return "-";
            }

            return (minutes.Value / 60.0).ToString("0.0", CultureInfo.InvariantCulture) + " h";
        }

        private static string FormatAchievements(GameInfo info)
        {
            if (info.AchievementTotal.HasValue == false)
            {
                return "—";
            }

            return (info.AchievementUnlocked.HasValue == true
                ? info.AchievementUnlocked.Value.ToString(CultureInfo.InvariantCulture)
                : "-") +
                "/" +
                info.AchievementTotal.Value.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatCardDrops(GameInfo info)
        {
            return info.CardDropsRemaining.HasValue == true
                ? info.CardDropsRemaining.Value.ToString(CultureInfo.InvariantCulture)
                : "-";
        }

        private bool IsFavorite(GameInfo info)
        {
            return info != null && this._FavoriteAppIds.Contains(info.Id) == true;
        }

        private string FormatFavoritePrefix(GameInfo info)
        {
            return this.IsFavorite(info) == true ? "[Fav] " : "";
        }

        private void ToggleFocusedFavorite()
        {
            var info = this.GetFocusedGame();
            if (info == null)
            {
                return;
            }

            if (this._FavoriteAppIds.Contains(info.Id) == true)
            {
                this._FavoriteAppIds.Remove(info.Id);
                this._PickerStatusLabel.Text = $"Removed {info.Name} from favorites.";
            }
            else
            {
                this._FavoriteAppIds.Add(info.Id);
                this._PickerStatusLabel.Text = $"Added {info.Name} to favorites.";
            }

            this.SaveFavoriteAppIds();
            this.RefreshGames();
            this.UpdateGameHub();
        }

        private static HashSet<uint> LoadFavoriteAppIds()
        {
            HashSet<uint> result = new();
            var path = GetFavoritesPath();
            if (File.Exists(path) == false)
            {
                return result;
            }

            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (uint.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) == true)
                    {
                        result.Add(appId);
                    }
                }
            }
            catch (Exception)
            {
            }

            return result;
        }

        private void SaveFavoriteAppIds()
        {
            try
            {
                Directory.CreateDirectory(GetCardForgeDataDirectory());
                File.WriteAllLines(
                    GetFavoritesPath(),
                    this._FavoriteAppIds
                        .OrderBy(appId => appId)
                        .Select(appId => appId.ToString(CultureInfo.InvariantCulture)));
            }
            catch (Exception)
            {
            }
        }

        private static string GetCardForgeDataDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CardForge");
        }

        private static string GetFavoritesPath()
        {
            return Path.Combine(GetCardForgeDataDirectory(), "favorites.txt");
        }

        private static Dictionary<uint, int> LoadLocalPlaytimeByAppId(API.Client client)
        {
            Dictionary<uint, int> result = new();

            try
            {
                var steamPath = API.Steam.GetInstallPath();
                var accountId = (client.SteamUser.GetSteamId() & 0xFFFFFFFF).ToString(CultureInfo.InvariantCulture);
                var localConfigPath = Path.Combine(steamPath, "userdata", accountId, "config", "localconfig.vdf");
                if (File.Exists(localConfigPath) == false)
                {
                    return result;
                }

                var text = File.ReadAllText(localConfigPath);
                var matches = Regex.Matches(
                    text,
                    "\"(?<appid>\\d+)\"\\s*\\{(?<body>.*?)\\}",
                    RegexOptions.Singleline);
                foreach (Match match in matches)
                {
                    if (uint.TryParse(match.Groups["appid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) == false)
                    {
                        continue;
                    }

                    var playtimeMatch = Regex.Match(match.Groups["body"].Value, "\"Playtime\"\\s*\"(?<minutes>\\d+)\"");
                    if (playtimeMatch.Success == false)
                    {
                        continue;
                    }

                    if (int.TryParse(playtimeMatch.Groups["minutes"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) == true)
                    {
                        result[appId] = minutes;
                    }
                }
            }
            catch (Exception)
            {
            }

            return result;
        }

        private static int? CountLocalAchievements(uint appId)
        {
            try
            {
                var schemaPath = Path.Combine(
                    API.Steam.GetInstallPath(),
                    "appcache",
                    "stats",
                    "UserGameStatsSchema_" + appId.ToString(CultureInfo.InvariantCulture) + ".bin");
                var kv = PickerKeyValue.LoadAsBinary(schemaPath);
                if (kv == null)
                {
                    return null;
                }

                var stats = kv[appId.ToString(CultureInfo.InvariantCulture)]["stats"];
                if (stats.Valid == false || stats.Children == null)
                {
                    return null;
                }

                int total = 0;
                foreach (var stat in stats.Children)
                {
                    var type = stat["type"].AsString("");
                    var typeInt = stat["type_int"].AsInteger(stat["type"].AsInteger(0));
                    bool isAchievementGroup =
                        string.Equals(type, "ACHIEVEMENTS", StringComparison.OrdinalIgnoreCase) == true ||
                        string.Equals(type, "GROUPACHIEVEMENTS", StringComparison.OrdinalIgnoreCase) == true ||
                        typeInt == 4 ||
                        typeInt == 5;
                    if (isAchievementGroup == false || stat.Children == null)
                    {
                        continue;
                    }

                    foreach (var bits in stat.Children.Where(child => string.Equals(child.Name, "bits", StringComparison.OrdinalIgnoreCase) == true))
                    {
                        if (bits.Children != null)
                        {
                            total += bits.Children.Count;
                        }
                    }
                }

                return total > 0 ? total : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void AddGame(uint id, string type)
        {
            if (this._Games.ContainsKey(id) == true)
            {
                return;
            }

            if (this.OwnsGame(id) == false)
            {
                return;
            }

            GameInfo info = new(id, type);
            info.Name = this._SteamClient.SteamApps001.GetAppData(info.Id, "name");
            if (this._PlaytimeByAppId.TryGetValue(id, out var playtime) == true)
            {
                info.PlaytimeMinutes = playtime;
            }
            if (this._CardDropsByAppId.TryGetValue(id, out var cardDrops) == true)
            {
                info.CardDropsRemaining = cardDrops;
            }
            info.AchievementTotal = CountLocalAchievements(id);
            this._Games.Add(id, info);
        }

        private void AddGames()
        {
            this._Games.Clear();
            this._RefreshGamesButton.Enabled = false;
            this._ListWorker.RunWorkerAsync();
        }

        private void AddDefaultGames()
        {
            this.AddGame(480, "normal"); // Spacewar
        }

        private void OnTimer(object sender, EventArgs e)
        {
            this._CallbackTimer.Enabled = false;
            this._SteamClient.RunCallbacks(false);
            this._CallbackTimer.Enabled = true;
        }

        private void OnActivateGame(object sender, EventArgs e)
        {
            var info = this.GetFocusedGame();
            if (info == null)
            {
                return;
            }

            this.OpenSelectedGame(info);
        }

        private void OpenSelectedGame(GameInfo info)
        {
            this.OpenSelectedGame(info, false);
        }

        private void OpenSelectedGame(GameInfo info, bool hideWhenReady)
        {
            try
            {
                var gamePath = Path.Combine(Application.StartupPath, "SAM.Game.exe");
                var process = Process.Start(gamePath, info.Id.ToString(CultureInfo.InvariantCulture));
                if (process != null)
                {
                    this.TrackOpenedGameProcess(info.Id, process, hideWhenReady);
                }
            }
            catch (Win32Exception)
            {
                MessageBox.Show(
                    this,
                    "Failed to start SAM.Game.exe.",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void OnRefresh(object sender, EventArgs e)
        {
            this._SearchGameTextBox.Text = "";
            this._PlaytimeByAppId = LoadLocalPlaytimeByAppId(this._SteamClient);
            this.AddGames();
            this.RefreshCardDropsInBackground();
        }

        private void OnAddGame(object sender, EventArgs e)
        {
            uint id;

            if (TryParseAppId(this._SearchGameTextBox.Text, out id) == false)
            {
                MessageBox.Show(
                    this,
                    "Please enter a valid AppID, Steam store URL, or steam://run URL.",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            if (this.OwnsGame(id) == false)
            {
                MessageBox.Show(this, "You don't own that game.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            while (this._LogoQueue.TryDequeue(out var logo) == true)
            {
                // clear the download queue because we will be showing only one app
                this._LogosAttempted.Remove(logo.ImageUrl);
            }

            this._SearchGameTextBox.Text = "";
            this._Games.Clear();
            this.AddGame(id, "normal");
            this._FilterGamesMenuItem.Checked = true;
            this.RefreshGames();
            this.DownloadNextLogo();
        }

        private void OnFilterUpdate(object sender, EventArgs e)
        {
            this.RefreshGames();

            // Compatibility with _GameListView SearchForVirtualItemEventHandler (otherwise _SearchGameTextBox loose focus on KeyUp)
            this._SearchGameTextBox.Focus();
        }

        private void OnSearchGameKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter)
            {
                return;
            }

            if (TryParseAppId(this._SearchGameTextBox.Text, out var id) == true)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                this.OnAddGame(sender, e);
            }
        }

        private void ConfigureModernLibraryUi()
        {
            this.Text = "CardForge | Steam Library";
            this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? this.Icon;
            this.BackColor = Color.FromArgb(18, 21, 27);
            this.ForeColor = Color.FromArgb(232, 238, 247);
            this.FormBorderStyle = FormBorderStyle.None;
            this.MinimumSize = new Size(980, 620);
            this.Size = new Size(1120, 720);

            this._PickerToolStrip.BackColor = Color.FromArgb(28, 33, 42);
            this._PickerToolStrip.ForeColor = Color.FromArgb(232, 238, 247);
            this._PickerToolStrip.GripStyle = ToolStripGripStyle.Hidden;
            this._PickerToolStrip.Padding = new Padding(8, 3, 8, 3);
            this._PickerToolStrip.AutoSize = false;
            this._PickerToolStrip.Height = 34;
            this._PickerToolStrip.Renderer = new DarkToolStripRenderer();

            this._RefreshGamesButton.Text = "Refresh";
            this._RefreshGamesButton.ToolTipText = "Reload your Steam library";
            this._FindGamesLabel.Text = "Search";
            this._SearchGameTextBox.Size = new Size(360, 24);
            this._SearchGameTextBox.ToolTipText = "Search by name, AppID, or type. Press Enter on an AppID or Steam URL to open it.";
            this._SearchGameTextBox.KeyDown += this.OnSearchGameKeyDown;
            this._SearchGameTextBox.BackColor = Color.FromArgb(22, 26, 34);
            this._SearchGameTextBox.ForeColor = Color.FromArgb(232, 238, 247);
            this._SearchGameTextBox.BorderStyle = BorderStyle.FixedSingle;
            this._SearchGameTextBox.Control.BackColor = Color.FromArgb(22, 26, 34);
            this._SearchGameTextBox.Control.ForeColor = Color.FromArgb(232, 238, 247);
            this._FilterDropDownButton.Text = "Types";
            this._FilterDropDownButton.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            this._FilterDropDownButton.DropDown.BackColor = Color.FromArgb(28, 33, 42);
            this._FilterDropDownButton.DropDown.ForeColor = Color.FromArgb(232, 238, 247);
            this._FilterDropDownButton.DropDown.Renderer = new DarkToolStripRenderer();
            this.CreateViewModeMenu();

            this._GameListView.ContextMenuStrip = this._LibraryContextMenu;
            this._GameListView.BackColor = Color.FromArgb(14, 17, 22);
            this._GameListView.BorderStyle = BorderStyle.None;
            this._GameListView.ForeColor = Color.FromArgb(232, 238, 247);
            this._GameListView.TileSize = new Size(260, 92);
            this._GameListView.ShowItemToolTips = true;
            this._GameListView.SelectedIndexChanged += (sender, e) => this.UpdateGameHub();
            this._GameListView.Columns.Add("Name", 260);
            this._GameListView.Columns.Add("AppID", 90);
            this._GameListView.Columns.Add("Hours", 90);
            this._GameListView.Columns.Add("Achievements", 120);
            this._GameListView.Columns.Add("Cards left", 110);
            this._GameListView.HeaderStyle = ColumnHeaderStyle.None;
            this._GameListView.Resize += (sender, e) =>
            {
                if (this._CurrentLibraryView == View.Details)
                {
                    this.ApplyTableColumnWidths();
                }
            };

            this.CreateTableGridView();
            this.CreateLibraryLayout();

            this._PickerStatusStrip.BackColor = Color.FromArgb(28, 33, 42);
            this._PickerStatusStrip.ForeColor = Color.FromArgb(170, 187, 204);
            this._PickerStatusStrip.Renderer = new DarkToolStripRenderer();
            this._PickerStatusStrip.AutoSize = false;
            this._PickerStatusStrip.Height = 22;
            this._PickerStatusStrip.SizingGrip = false;

            Native.ApplyImmersiveDarkMode(this.Handle);
            Native.ApplyExplorerDarkTheme(this._GameListView.Handle);
            Native.ApplyExplorerDarkTheme(this._SearchGameTextBox.Control.Handle);

            this.Resize += this.OnMainWindowResize;
        }

        private void CreateViewModeMenu()
        {
            this._TileViewMenuItem = new ToolStripMenuItem("Tiles", null, (sender, e) => this.SetLibraryView(View.LargeIcon));
            this._ListViewMenuItem = new ToolStripMenuItem("List", null, (sender, e) => this.SetLibraryView(View.List));
            this._TableViewMenuItem = new ToolStripMenuItem("Table", null, (sender, e) => this.SetLibraryView(View.Details));
            this._TileViewMenuItem.Checked = true;

            this._ViewModeDropDownButton = new ToolStripDropDownButton("View");
            this._ViewModeDropDownButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            this._ViewModeDropDownButton.DropDownItems.AddRange(new ToolStripItem[]
            {
                this._TileViewMenuItem,
                this._ListViewMenuItem,
                this._TableViewMenuItem,
            });
            this._ViewModeDropDownButton.DropDown.BackColor = Color.FromArgb(28, 33, 42);
            this._ViewModeDropDownButton.DropDown.ForeColor = Color.FromArgb(232, 238, 247);
            this._ViewModeDropDownButton.DropDown.Renderer = new DarkToolStripRenderer();
            this._PickerToolStrip.Items.Add(this._ViewModeDropDownButton);

            this._LoadCardDataButton = new ToolStripButton("Load Card Drops");
            this._LoadCardDataButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            this._LoadCardDataButton.ToolTipText = "Load remaining card drops for the current Steam account.";
            this._LoadCardDataButton.Click += this.OnLoadCardDrops;
            this._PickerToolStrip.Items.Add(this._LoadCardDataButton);

            this._CardsRemainingFilterButton = new ToolStripButton("Cards Only");
            this._CardsRemainingFilterButton.CheckOnClick = true;
            this._CardsRemainingFilterButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            this._CardsRemainingFilterButton.ToolTipText = "Show only games with remaining card drops.";
            this._CardsRemainingFilterButton.CheckedChanged += (sender, e) => this.RefreshGames();
            this._PickerToolStrip.Items.Add(this._CardsRemainingFilterButton);

            this._LaunchCardGamesButton = new ToolStripButton("Launch Cards");
            this._LaunchCardGamesButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            this._LaunchCardGamesButton.ToolTipText = "Open every game with card drops remaining.";
            this._LaunchCardGamesButton.Click += (sender, e) => this.OpenCardRemainingGames();
            this._PickerToolStrip.Items.Add(this._LaunchCardGamesButton);

            this._FavoritesFilterButton = new ToolStripButton("Favorites");
            this._FavoritesFilterButton.CheckOnClick = true;
            this._FavoritesFilterButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            this._FavoritesFilterButton.ToolTipText = "Show only favorite games.";
            this._FavoritesFilterButton.CheckedChanged += (sender, e) => this.RefreshGames();
            this._PickerToolStrip.Items.Add(this._FavoritesFilterButton);

            this._ToggleFavoriteButton = new ToolStripButton("Fav +/-");
            this._ToggleFavoriteButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            this._ToggleFavoriteButton.ToolTipText = "Add or remove the selected game from favorites.";
            this._ToggleFavoriteButton.Click += (sender, e) => this.ToggleFocusedFavorite();
            this._PickerToolStrip.Items.Add(this._ToggleFavoriteButton);

            this._SchedulerButton = new ToolStripButton("Scheduler");
            this._SchedulerButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            this._SchedulerButton.ToolTipText = "Open the timed launch/restart scheduler.";
            this._SchedulerButton.Click += (sender, e) => this.OpenScheduler();
            this._PickerToolStrip.Items.Add(this._SchedulerButton);
        }

        private void SetLibraryView(View view)
        {
            this._CurrentLibraryView = view;
            if (view == View.Details)
            {
                this._GameListView.Visible = false;
                this._GameListView.SendToBack();
                this._TableHostPanel.Visible = true;
                this._TableGridView.Visible = true;
                this._TableGridView.ColumnHeadersVisible = true;
                this._TableGridView.ColumnHeadersHeight = 28;
                this.PopulateTableGrid();
                this._TableHostPanel.BringToFront();
                this._TableGridView.BringToFront();
                this._TableGridView.Focus();
                this.ApplyTableColumnWidths();
                this._TileViewMenuItem.Checked = false;
                this._ListViewMenuItem.Checked = false;
                this._TableViewMenuItem.Checked = true;
                return;
            }

            this._TableHostPanel.Visible = false;
            this._TableGridView.Visible = false;
            this._TableHostPanel.SendToBack();
            this._GameListView.Visible = true;
            this._GameListView.BringToFront();
            this._GameListView.BeginUpdate();
            this._GameListView.VirtualListSize = 0;
            this._GameListView.SelectedIndices.Clear();
            this._GameListView.OwnerDraw = false;
            this._GameListView.LargeImageList = null;
            this._GameListView.SmallImageList = null;

            this._GameListView.View = view;
            if (view == View.LargeIcon)
            {
                this._GameListView.LargeImageList = this._LogoImageList;
                this._GameListView.SmallImageList = this._LogoImageList;
                this._GameListView.OwnerDraw = true;
            }

            this._TileViewMenuItem.Checked = view == View.LargeIcon;
            this._ListViewMenuItem.Checked = view == View.List;
            this._TableViewMenuItem.Checked = false;
            this._GameListView.VirtualListSize = this._FilteredGames.Count;
            this._GameListView.EndUpdate();
            this._GameListView.Invalidate();
            Native.RedrawWindow(
                this._GameListView.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                Native.RdwInvalidate | Native.RdwErase | Native.RdwAllChildren | Native.RdwUpdateNow);
        }

        private string GetLibraryItemText(GameInfo info)
        {
            if (this._CurrentLibraryView == View.LargeIcon)
            {
                var cards = info.CardDropsRemaining.HasValue == true && info.CardDropsRemaining.Value > 0
                    ? $" - Cards {info.CardDropsRemaining.Value.ToString(CultureInfo.InvariantCulture)}"
                    : "";
                return $"{this.FormatFavoritePrefix(info)}{info.Name}\nAppID {info.Id} - {GetDisplayType(info.Type)}{cards}";
            }

            if (this._CurrentLibraryView == View.List)
            {
                return $"{this.FormatFavoritePrefix(info)}{info.Name}  |  AppID {info.Id}  |  {FormatPlaytime(info.PlaytimeMinutes)}  |  {FormatAchievements(info)}  |  Cards left: {FormatCardDrops(info)}";
            }

            return this.FormatFavoritePrefix(info) + info.Name;
        }

        private void ApplyTableColumnWidths()
        {
            if (this._TableGridView == null)
            {
                return;
            }

            var width = Math.Max(600, this._TableGridView.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8);
            this._TableGridView.Columns[0].Width = Math.Max(280, width - 440);
            this._TableGridView.Columns[1].Width = 90;
            this._TableGridView.Columns[2].Width = 90;
            this._TableGridView.Columns[3].Width = 140;
            this._TableGridView.Columns[4].Width = 120;
        }

        private void CreateTableGridView()
        {
            this._TableGridView = new DataGridView
            {
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                BackgroundColor = Color.FromArgb(14, 17, 22),
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleVertical,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
                ColumnHeadersHeight = 28,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersVisible = true,
                Dock = DockStyle.Fill,
                EnableHeadersVisualStyles = false,
                GridColor = Color.FromArgb(36, 43, 53),
                MultiSelect = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                Visible = false,
            };
            this._TableGridView.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(28, 33, 42);
            this._TableGridView.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(232, 238, 247);
            this._TableGridView.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(28, 33, 42);
            this._TableGridView.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.FromArgb(232, 238, 247);
            this._TableGridView.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            this._TableGridView.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
            this._TableGridView.DefaultCellStyle.BackColor = Color.FromArgb(14, 17, 22);
            this._TableGridView.DefaultCellStyle.ForeColor = Color.FromArgb(232, 238, 247);
            this._TableGridView.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 94, 160);
            this._TableGridView.DefaultCellStyle.SelectionForeColor = Color.White;
            this._TableGridView.RowTemplate.Height = 22;
            this._TableGridView.Columns.Add(CreateTableColumn("Name", "Name"));
            this._TableGridView.Columns.Add(CreateTableColumn("AppID", "AppID"));
            this._TableGridView.Columns.Add(CreateTableColumn("Hours", "Hours"));
            this._TableGridView.Columns.Add(CreateTableColumn("Achievements", "Achievements"));
            this._TableGridView.Columns.Add(CreateTableColumn("Cards left", "Cards left"));
            this._TableGridView.ColumnHeaderMouseClick += (sender, e) => this.SortByColumn(e.ColumnIndex);
            this._TableGridView.SelectionChanged += (sender, e) => this.UpdateGameHub();
            this._TableGridView.CellDoubleClick += (sender, e) =>
            {
                var info = this.GetFocusedGame();
                if (info != null)
                {
                    this.OpenSelectedGame(info);
                }
            };
        }

        private static DataGridViewTextBoxColumn CreateTableColumn(string headerText, string name)
        {
            return new DataGridViewTextBoxColumn
            {
                HeaderText = headerText,
                Name = name,
                SortMode = DataGridViewColumnSortMode.Programmatic,
            };
        }

        private void PopulateTableGrid()
        {
            if (this._TableGridView == null)
            {
                return;
            }

            this._TableGridView.ColumnHeadersVisible = true;
            this._TableGridView.ColumnHeadersHeight = 28;
            this._TableGridView.Rows.Clear();
            foreach (var info in this._FilteredGames)
            {
                var index = this._TableGridView.Rows.Add(
                    this.FormatFavoritePrefix(info) + info.Name,
                    info.Id.ToString(CultureInfo.InvariantCulture),
                    FormatPlaytime(info.PlaytimeMinutes),
                    FormatAchievements(info),
                    FormatCardDrops(info));
                this._TableGridView.Rows[index].Tag = info;
            }

            this.ApplyTableColumnWidths();
        }

        private void CreateLibraryLayout()
        {
            this.Controls.Remove(this._GameListView);
            this._LibraryLayoutPanel = new TableLayoutPanel
            {
                BackColor = Color.FromArgb(14, 17, 22),
                ColumnCount = 1,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                RowCount = 2,
            };
            this._LibraryLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            this._LibraryLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            this._LibraryLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 0F));

            this._ListAreaPanel = new Panel
            {
                BackColor = Color.FromArgb(14, 17, 22),
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
            };

            this._TableHostPanel = new Panel
            {
                BackColor = Color.FromArgb(14, 17, 22),
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Visible = false,
            };
            this._TableGridView.Dock = DockStyle.Fill;
            this._TableHostPanel.Controls.Add(this._TableGridView);
            this._TableGridView.BringToFront();

            this._GameListView.Dock = DockStyle.Fill;
            this._ListAreaPanel.Controls.Add(this._GameListView);
            this._ListAreaPanel.Controls.Add(this._TableHostPanel);
            this._GameListView.BringToFront();
            this._LibraryLayoutPanel.Controls.Add(this._ListAreaPanel, 0, 0);
        }

        private void SortByColumn(int column)
        {
            if (this._SortColumn == column)
            {
                this._SortAscending = !this._SortAscending;
            }
            else
            {
                this._SortColumn = column;
                this._SortAscending = true;
            }

            this.RefreshGames();
            if (this._CurrentLibraryView == View.Details)
            {
                this.PopulateTableGrid();
            }
        }

        private void SortFilteredGames()
        {
            Comparison<GameInfo> comparison = this._SortColumn switch
            {
                1 => (left, right) => left.Id.CompareTo(right.Id),
                2 => (left, right) => Nullable.Compare(left.PlaytimeMinutes, right.PlaytimeMinutes),
                3 => CompareAchievements,
                4 => (left, right) => Nullable.Compare(left.CardDropsRemaining, right.CardDropsRemaining),
                _ => (left, right) => string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase),
            };

            this._FilteredGames.Sort((left, right) =>
            {
                var result = comparison(left, right);
                if (result == 0)
                {
                    result = string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
                }

                return this._SortAscending == true ? result : -result;
            });
        }

        private static int CompareAchievements(GameInfo left, GameInfo right)
        {
            var leftPercent = GetAchievementSortValue(left);
            var rightPercent = GetAchievementSortValue(right);
            return leftPercent.CompareTo(rightPercent);
        }

        private static double GetAchievementSortValue(GameInfo info)
        {
            if (info.AchievementTotal.HasValue == false || info.AchievementTotal.Value <= 0)
            {
                return -1;
            }

            return (info.AchievementUnlocked ?? 0) / (double)info.AchievementTotal.Value;
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
                Text = "CardForge",
                TextAlign = ContentAlignment.MiddleLeft,
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
            this.BuildRootLayout();
        }

        private void ApplyMainDockOrder()
        {
            this.BuildRootLayout();
        }

        private void BuildRootLayout()
        {
            if (this._TitleBar == null || this._LibraryLayoutPanel == null)
            {
                return;
            }

            if (this._RootLayoutPanel == null)
            {
                this._RootLayoutPanel = new TableLayoutPanel
                {
                    BackColor = Color.FromArgb(14, 17, 22),
                    ColumnCount = 1,
                    Dock = DockStyle.Fill,
                    GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
                    Margin = Padding.Empty,
                    Padding = Padding.Empty,
                    RowCount = 4,
                };
                this._RootLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                this._RootLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
                this._RootLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
                this._RootLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                this._RootLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
            }

            this.SuspendLayout();
            this._RootLayoutPanel.SuspendLayout();

            var rootControls = new Control[]
            {
                this._TitleBar,
                this._PickerToolStrip,
                this._LibraryLayoutPanel,
                this._PickerStatusStrip,
            };

            foreach (var control in rootControls)
            {
                if (control.Parent != null && control.Parent != this._RootLayoutPanel)
                {
                    control.Parent.Controls.Remove(control);
                }
            }

            if (this._RootLayoutPanel.Parent != null)
            {
                this._RootLayoutPanel.Parent.Controls.Remove(this._RootLayoutPanel);
            }

            this._RootLayoutPanel.Controls.Clear();

            this._TitleBar.Dock = DockStyle.Fill;
            this._PickerToolStrip.Dock = DockStyle.Fill;
            this._LibraryLayoutPanel.Dock = DockStyle.Fill;
            this._PickerStatusStrip.Dock = DockStyle.Fill;

            this._RootLayoutPanel.Controls.Add(this._TitleBar, 0, 0);
            this._RootLayoutPanel.Controls.Add(this._PickerToolStrip, 0, 1);
            this._RootLayoutPanel.Controls.Add(this._LibraryLayoutPanel, 0, 2);
            this._RootLayoutPanel.Controls.Add(this._PickerStatusStrip, 0, 3);
            this.Controls.Add(this._RootLayoutPanel);
            this.Controls.SetChildIndex(this._RootLayoutPanel, 0);

            this._RootLayoutPanel.ResumeLayout(true);
            this.ResumeLayout(true);
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
                TextAlign = ContentAlignment.MiddleCenter,
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

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (this._AllowExit == false && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.HideMainToTray();
                return;
            }

            this._TrayIcon?.Dispose();
            base.OnFormClosing(e);
        }

        private void CreateTrayIcon()
        {
            this._TrayMenu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(28, 33, 42),
                ForeColor = Color.FromArgb(232, 238, 247),
                Renderer = new DarkToolStripRenderer(),
            };
            this._TrayMenu.Items.Add("Show CardForge", null, (sender, e) => this.ShowMainFromTray());
            this._TrayMenu.Items.Add("Hide CardForge", null, (sender, e) => this.HideMainToTray());
            this._TrayMenu.Items.Add(new ToolStripSeparator());
            this._TrayMenu.Items.Add("Refresh Library", null, this.OnRefresh);
            this._TrayMenu.Items.Add("Refresh Card Drops", null, (sender, e) => this.RefreshCardDropsInBackground());
            this._TrayMenu.Items.Add("Open Scheduler", null, (sender, e) => this.OpenScheduler());
            this._TrayMenu.Items.Add(new ToolStripSeparator());
            this._TrayMenu.Items.Add("Launch Card Drop Games", null, (sender, e) => this.OpenCardRemainingGames());
            this._TrayMenu.Items.Add("Close Card Drop Games", null, (sender, e) => this.CloseCardRemainingGames());
            this._OpenGameWindowsMenuItem = new ToolStripMenuItem("Open Game Windows");
            this._OpenGameWindowsMenuItem.DropDownOpening += (sender, e) => this.PopulateOpenGameWindowsMenu();
            this._TrayMenu.Items.Add(this._OpenGameWindowsMenuItem);
            this._TrayMenu.Items.Add(new ToolStripSeparator());
            this._TrayMenu.Items.Add("Show Open Game Windows", null, (sender, e) => this.ShowOpenedGameWindows());
            this._TrayMenu.Items.Add("Hide Open Game Windows", null, (sender, e) => this.HideOpenedGameWindows());
            this._TrayMenu.Items.Add("Close Open Game Windows", null, (sender, e) => this.CloseOpenedGameWindows());
            this._TrayMenu.Items.Add(new ToolStripSeparator());
            this._TrayMenu.Items.Add("Exit CardForge", null, (sender, e) => this.ExitFromTray());

            this._TrayIcon = new NotifyIcon
            {
                ContextMenuStrip = this._TrayMenu,
                Icon = this.Icon,
                Text = "CardForge",
                Visible = true,
            };
            this._TrayIcon.DoubleClick += (sender, e) => this.ShowMainFromTray();
        }

        private void OnMainWindowResize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                this.HideMainToTray();
            }
        }

        private void HideMainToTray()
        {
            this.ShowInTaskbar = false;
            this.Hide();
            this._TrayIcon.Visible = true;
            this._TrayIcon.Text = "CardForge - running in tray";
        }

        private void ShowMainFromTray()
        {
            this.ShowInTaskbar = true;
            this.Show();
            if (this.WindowState == FormWindowState.Minimized)
            {
                this.WindowState = FormWindowState.Normal;
            }

            this.Activate();
            this._TrayIcon.Text = "CardForge";
        }

        private void ExitFromTray()
        {
            this._AllowExit = true;
            this.Close();
        }

        private void TrackOpenedGameProcess(uint appId, Process process, bool hideWhenReady)
        {
            this._OpenedGameProcesses.Add(process);
            this._OpenedGameProcessesByAppId[appId] = process;
            process.EnableRaisingEvents = true;
            process.Exited += (sender, e) =>
            {
                if (this.IsDisposed == false)
                {
                    this.BeginInvoke((Action)(() =>
                    {
                        this._OpenedGameProcesses.Remove(process);
                        this._OpenedGameProcessesByAppId.Remove(appId);
                        this._OpenedGameWindows.Remove(process.Id);
                    }));
                }
            };

            Task.Run(() =>
            {
                var handle = WaitForMainWindowHandle(process);
                if (handle != IntPtr.Zero && this.IsDisposed == false)
                {
                    this.BeginInvoke((Action)(() =>
                    {
                        this._OpenedGameWindows[process.Id] = handle;
                        if (hideWhenReady == true)
                        {
                            Native.ShowWindow(handle, Native.SwHide);
                        }
                    }));
                }
            });
        }

        private void RefreshOpenedGameProcesses()
        {
            this._OpenedGameProcesses.RemoveAll(process =>
            {
                try
                {
                    if (process.HasExited == true)
                    {
                        this._OpenedGameWindows.Remove(process.Id);
                        foreach (var kv in this._OpenedGameProcessesByAppId.Where(kv => kv.Value.Id == process.Id).ToArray())
                        {
                            this._OpenedGameProcessesByAppId.Remove(kv.Key);
                        }
                        return true;
                    }

                    if (this._OpenedGameWindows.ContainsKey(process.Id) == false)
                    {
                        var handle = process.MainWindowHandle;
                        if (handle != IntPtr.Zero)
                        {
                            this._OpenedGameWindows[process.Id] = handle;
                        }
                    }

                    return false;
                }
                catch (Exception)
                {
                    foreach (var kv in this._OpenedGameProcessesByAppId.Where(kv => kv.Value.Id == process.Id).ToArray())
                    {
                        this._OpenedGameProcessesByAppId.Remove(kv.Key);
                    }
                    return true;
                }
            });
        }

        private void OpenCardRemainingGames()
        {
            this.RefreshOpenedGameProcesses();
            var games = this._Games.Values
                .Where(info => info.CardDropsRemaining.HasValue == true && info.CardDropsRemaining.Value > 0)
                .OrderBy(info => info.Name)
                .ToList();

            int launched = 0;
            int skipped = 0;
            foreach (var info in games)
            {
                if (this._OpenedGameProcessesByAppId.TryGetValue(info.Id, out var existing) == true &&
                    IsProcessAlive(existing) == true)
                {
                    skipped++;
                    continue;
                }

                this.OpenSelectedGame(info, true);
                launched++;
            }

            this._PickerStatusLabel.Text =
                $"Launched {launched.ToString(CultureInfo.InvariantCulture)} card drop games. {skipped.ToString(CultureInfo.InvariantCulture)} already open.";
        }

        private void CloseCardRemainingGames()
        {
            this.RefreshOpenedGameProcesses();
            foreach (var kv in this._OpenedGameProcessesByAppId.ToArray())
            {
                var info = this._Games.TryGetValue(kv.Key, out var game) == true ? game : null;
                if (info == null || info.CardDropsRemaining.HasValue == false || info.CardDropsRemaining.Value <= 0)
                {
                    continue;
                }

                try
                {
                    if (kv.Value.HasExited == false)
                    {
                        kv.Value.CloseMainWindow();
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private void PopulateOpenGameWindowsMenu()
        {
            this.RefreshOpenedGameProcesses();
            this._OpenGameWindowsMenuItem.DropDownItems.Clear();

            foreach (var kv in this._OpenedGameProcessesByAppId.OrderBy(kv =>
                         this._Games.TryGetValue(kv.Key, out var info) == true ? info.Name : kv.Key.ToString(CultureInfo.InvariantCulture)))
            {
                if (IsProcessAlive(kv.Value) == false)
                {
                    continue;
                }

                var title = this._Games.TryGetValue(kv.Key, out var info) == true
                    ? info.Name
                    : "AppID " + kv.Key.ToString(CultureInfo.InvariantCulture);
                this._OpenGameWindowsMenuItem.DropDownItems.Add(title, null, (sender, e) => this.ShowOpenedGameWindow(kv.Key));
            }

            if (this._OpenGameWindowsMenuItem.DropDownItems.Count == 0)
            {
                this._OpenGameWindowsMenuItem.DropDownItems.Add("(no open game windows)").Enabled = false;
            }
        }

        private void ShowOpenedGameWindow(uint appId)
        {
            this.RefreshOpenedGameProcesses();
            if (this._OpenedGameProcessesByAppId.TryGetValue(appId, out var process) == false ||
                IsProcessAlive(process) == false)
            {
                return;
            }

            if (this._OpenedGameWindows.TryGetValue(process.Id, out var handle) == false || handle == IntPtr.Zero)
            {
                handle = process.MainWindowHandle;
            }

            if (handle == IntPtr.Zero)
            {
                return;
            }

            this._OpenedGameWindows[process.Id] = handle;
            Native.ShowWindow(handle, Native.SwShow);
            Native.SetForegroundWindow(handle);
        }

        private static bool IsProcessAlive(Process process)
        {
            try
            {
                return process.HasExited == false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void ShowOpenedGameWindows()
        {
            this.RefreshOpenedGameProcesses();
            foreach (var handle in this._OpenedGameWindows.Values)
            {
                Native.ShowWindow(handle, Native.SwShow);
            }
        }

        private void HideOpenedGameWindows()
        {
            this.RefreshOpenedGameProcesses();
            foreach (var handle in this._OpenedGameWindows.Values)
            {
                Native.ShowWindow(handle, Native.SwHide);
            }
        }

        private void CloseOpenedGameWindows()
        {
            this.RefreshOpenedGameProcesses();
            foreach (var process in this._OpenedGameProcesses.ToArray())
            {
                try
                {
                    if (process.HasExited == false)
                    {
                        process.CloseMainWindow();
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private void RefreshCardDropsInBackground()
        {
            this.RefreshCardDropsHiddenAsync();
        }

        private Task<Dictionary<uint, int>> RefreshCardDropsHiddenAsync()
        {
            if (this._RefreshingCardDrops == true)
            {
                return Task.FromResult(this.GetCurrentCardDrops());
            }

            TaskCompletionSource<Dictionary<uint, int>> completion = new();
            this._RefreshingCardDrops = true;
            this._PickerStatusLabel.Text = "Refreshing card drops...";
            var steamId = this._SteamClient.SteamUser.GetSteamId();
            bool completed = false;
            CardDropLoader loader = new(steamId, drops =>
            {
                if (this.IsDisposed == true)
                {
                    completion.TrySetResult(drops);
                    return;
                }

                this.BeginInvoke((Action)(() =>
                {
                    this.ApplyCardDrops(drops);
                    completed = true;
                    this._RefreshingCardDrops = false;
                    completion.TrySetResult(drops);
                }));
            }, true, true)
            {
                Opacity = 0,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
                Size = new Size(1, 1),
            };
            loader.FormClosed += (sender, e) =>
            {
                if (this.IsDisposed == false)
                {
                    this._RefreshingCardDrops = false;
                }

                if (completed == false)
                {
                    completion.TrySetResult(this.GetCurrentCardDrops());
                }
            };
            loader.Show(this);
            return completion.Task;
        }

        private Dictionary<uint, int> GetCurrentCardDrops()
        {
            return this._Games.Values
                .Where(info => info.CardDropsRemaining.HasValue == true)
                .ToDictionary(info => info.Id, info => info.CardDropsRemaining.Value);
        }

        private void OpenScheduler()
        {
            if (this.Visible == false)
            {
                this.ShowMainFromTray();
            }

            CardForgeScheduleForm form = new(
                () => this._Games.Values.OrderBy(info => info.Name).ToList(),
                () => this._Games.Values.Where(this.IsFavorite).OrderBy(info => info.Name).ToList(),
                info => this.OpenSelectedGame(info, true),
                this.CloseScheduledGame,
                this.RefreshCardDropsHiddenAsync);
            form.Show(this);
        }

        private void CloseScheduledGame(GameInfo info)
        {
            if (info == null)
            {
                return;
            }

            this.RefreshOpenedGameProcesses();
            if (this._OpenedGameProcessesByAppId.TryGetValue(info.Id, out var process) == false ||
                IsProcessAlive(process) == false)
            {
                return;
            }

            try
            {
                process.CloseMainWindow();
            }
            catch (Exception)
            {
            }
        }

        private static IntPtr WaitForMainWindowHandle(Process process)
        {
            for (int i = 0; i < 50; i++)
            {
                try
                {
                    if (process.HasExited == true)
                    {
                        return IntPtr.Zero;
                    }

                    process.Refresh();
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        return process.MainWindowHandle;
                    }
                }
                catch (Exception)
                {
                    return IntPtr.Zero;
                }

                System.Threading.Thread.Sleep(100);
            }

            return IntPtr.Zero;
        }

        private ContextMenuStrip CreateLibraryContextMenu()
        {
            ContextMenuStrip menu = new();
            menu.Items.Add("Open in SAM", null, (sender, e) =>
            {
                var info = this.GetFocusedGame();
                if (info != null)
                {
                    this.OpenSelectedGame(info);
                }
            });
            menu.Items.Add("Open Steam store page", null, (sender, e) => this.OpenSelectedGameUrl("https://store.steampowered.com/app/{0}/"));
            menu.Items.Add("Open Steam cards page", null, (sender, e) => this.OpenSelectedGameUrl("https://steamcommunity.com/my/gamecards/{0}/"));
            menu.Items.Add("Open Steam achievements page", null, (sender, e) => this.OpenSelectedGameUrl("https://steamcommunity.com/stats/{0}/achievements"));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Add / remove favorite", null, (sender, e) => this.ToggleFocusedFavorite());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Copy AppID", null, (sender, e) =>
            {
                var info = this.GetFocusedGame();
                if (info != null)
                {
                    Clipboard.SetText(info.Id.ToString(CultureInfo.InvariantCulture));
                }
            });
            return menu;
        }

        private void CreateGameHubPanel()
        {
            this._GameHubPanel = new Panel
            {
                BackColor = Color.FromArgb(18, 21, 27),
                Dock = DockStyle.Bottom,
                Height = 92,
                Padding = new Padding(14, 10, 14, 10),
            };

            this._GameHubTitleLabel = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Left,
                Font = new Font("Segoe UI", 12F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(232, 238, 247),
                Width = 260,
                Text = "Select a game",
                TextAlign = ContentAlignment.MiddleLeft,
            };

            this._GameHubMetaLabel = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(170, 187, 204),
                Text = "Game Hub actions will appear here.",
                TextAlign = ContentAlignment.MiddleLeft,
            };

            this._GameHubOpenButton = CreateGameHubButton("Open in SAM", (sender, e) =>
            {
                var info = this.GetFocusedGame();
                if (info != null)
                {
                    this.OpenSelectedGame(info);
                }
            });
            this._GameHubStoreButton = CreateGameHubButton("Store Page", (sender, e) => this.OpenSelectedGameUrl("https://store.steampowered.com/app/{0}/"));
            this._GameHubCardsButton = CreateGameHubButton("Card Drops", (sender, e) => this.OpenSelectedGameUrl("https://steamcommunity.com/my/gamecards/{0}/"));
            this._GameHubAchievementsButton = CreateGameHubButton("Steam Achievements", (sender, e) => this.OpenSelectedGameUrl("https://steamcommunity.com/stats/{0}/achievements"));
            this._GameHubCopyButton = CreateGameHubButton("Copy AppID", (sender, e) =>
            {
                var info = this.GetFocusedGame();
                if (info != null)
                {
                    Clipboard.SetText(info.Id.ToString(CultureInfo.InvariantCulture));
                }
            });

            var actionsPanel = new FlowLayoutPanel
            {
                AutoSize = false,
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 14, 0, 0),
                Width = 520,
                WrapContents = false,
            };
            actionsPanel.Controls.Add(this._GameHubOpenButton);
            actionsPanel.Controls.Add(this._GameHubStoreButton);
            actionsPanel.Controls.Add(this._GameHubCardsButton);
            actionsPanel.Controls.Add(this._GameHubAchievementsButton);
            actionsPanel.Controls.Add(this._GameHubCopyButton);
            this._GameHubPanel.Controls.Add(actionsPanel);
            this._GameHubPanel.Controls.Add(this._GameHubMetaLabel);
            this._GameHubPanel.Controls.Add(this._GameHubTitleLabel);
            if (this._LibraryLayoutPanel != null)
            {
                this._GameHubPanel.Dock = DockStyle.Fill;
                this._LibraryLayoutPanel.RowStyles[1].Height = 92F;
                this._LibraryLayoutPanel.Controls.Add(this._GameHubPanel, 0, 1);
            }
            else
            {
                this.Controls.Add(this._GameHubPanel);
                this._GameHubPanel.BringToFront();
            }
            this.SetGameHubEnabled(false);
            this.ApplyMainDockOrder();
        }

        private static Button CreateGameHubButton(string text, EventHandler onClick)
        {
            Button button = new()
            {
                BackColor = Color.FromArgb(28, 33, 42),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(232, 238, 247),
                Height = 34,
                Margin = new Padding(0, 0, 8, 0),
                Width = 96,
                Padding = Padding.Empty,
                Text = text,
                TextAlign = ContentAlignment.MiddleCenter,
                UseCompatibleTextRendering = false,
                UseVisualStyleBackColor = false,
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(54, 64, 78);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 49, 60);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(54, 64, 78);
            button.Click += onClick;
            return button;
        }

        private void UpdateGameHub()
        {
            var info = this.GetFocusedGame();
            if (info == null)
            {
                this._GameHubTitleLabel.Text = "Select a game";
                this._GameHubMetaLabel.Text = "Game Hub actions will appear here.";
                this.SetGameHubEnabled(false);
                return;
            }

            this._GameHubTitleLabel.Text = info.Name;
            this._GameHubMetaLabel.Text =
                $"AppID {info.Id.ToString(CultureInfo.InvariantCulture)}  |  {GetDisplayType(info.Type)}  |  Favorite: {(this.IsFavorite(info) == true ? "yes" : "no")}\n" +
                $"Played: {FormatPlaytime(info.PlaytimeMinutes)}  |  Achievements: {FormatAchievements(info)}  |  Card drops left: {FormatCardDrops(info)}";
            this.SetGameHubEnabled(true);
            this.FetchCommunityStatsIfNeeded(info);
        }

        private void SetGameHubEnabled(bool enabled)
        {
            this._GameHubOpenButton.Enabled = enabled;
            this._GameHubStoreButton.Enabled = enabled;
            this._GameHubCardsButton.Enabled = enabled;
            this._GameHubAchievementsButton.Enabled = enabled;
            this._GameHubCopyButton.Enabled = enabled;
        }

        private void OnLoadCardDrops(object sender, EventArgs e)
        {
            var steamId = this._SteamClient.SteamUser.GetSteamId();
            CardDropLoader loader = new(steamId, drops =>
            {
                if (this.IsDisposed == true)
                {
                    return;
                }

                this.BeginInvoke((Action)(() => this.ApplyCardDrops(drops)));
            });
            loader.Show(this);
        }

        private void ApplyCardDrops(Dictionary<uint, int> drops)
        {
            foreach (var kv in drops)
            {
                this._CardDropsByAppId[kv.Key] = kv.Value;
                if (this._Games.TryGetValue(kv.Key, out var info) == true)
                {
                    info.CardDropsRemaining = kv.Value;
                }
            }

            if (this._CardsRemainingFilterButton?.Checked == true)
            {
                this.RefreshGames();
            }

            this._GameListView.Invalidate();
            this.UpdateGameHub();
            var remaining = drops.Count(kv => kv.Value > 0);
            this._PickerStatusLabel.Text =
                $"Loaded card drop data for {drops.Count.ToString(CultureInfo.InvariantCulture)} games. {remaining.ToString(CultureInfo.InvariantCulture)} with cards remaining.";
        }

        private async void FetchCommunityStatsIfNeeded(GameInfo info)
        {
            if (info == null ||
                info.AchievementUnlocked.HasValue == true ||
                this._CommunityStatsRequested.Add(info.Id) == false)
            {
                return;
            }

            try
            {
                var unlocked = await Task.Run(() => TryFetchUnlockedAchievements(this._SteamClient.SteamUser.GetSteamId(), info.Id));
                if (unlocked.HasValue == false)
                {
                    return;
                }

                info.AchievementUnlocked = unlocked.Value;
                if (this.IsDisposed == false)
                {
                    this.BeginInvoke((Action)(() =>
                    {
                        this.UpdateGameHub();
                        this._GameListView.Invalidate();
                    }));
                }
            }
            catch (Exception)
            {
            }
        }

        private static int? TryFetchUnlockedAchievements(ulong steamId, uint appId)
        {
            var url =
                "https://steamcommunity.com/profiles/" +
                steamId.ToString(CultureInfo.InvariantCulture) +
                "/stats/" +
                appId.ToString(CultureInfo.InvariantCulture) +
                "/achievements?xml=1";

            using WebClient client = new();
            client.Headers[HttpRequestHeader.UserAgent] = "SAM Modern Library";
            var xml = client.DownloadString(url);

            XmlDocument document = new();
            document.LoadXml(xml);
            var achievements = document.SelectNodes("//achievement");
            if (achievements == null || achievements.Count == 0)
            {
                return null;
            }

            int unlocked = 0;
            foreach (XmlNode achievement in achievements)
            {
                var closed = achievement.SelectSingleNode("closed")?.InnerText;
                var unlockTimestamp = achievement.SelectSingleNode("unlockTimestamp")?.InnerText;
                if (closed == "1" || string.IsNullOrEmpty(unlockTimestamp) == false)
                {
                    unlocked++;
                }
            }

            return unlocked;
        }

        private static Dictionary<uint, int> TryFetchCardDrops(ulong steamId)
        {
            Dictionary<uint, int> result = new();

            using WebClient client = new();
            client.Headers[HttpRequestHeader.UserAgent] = "SAM Modern Library";
            for (int page = 1; page <= 25; page++)
            {
                var url =
                    "https://steamcommunity.com/profiles/" +
                    steamId.ToString(CultureInfo.InvariantCulture) +
                    "/badges/?p=" +
                    page.ToString(CultureInfo.InvariantCulture);
                var html = WebUtility.HtmlDecode(client.DownloadString(url));
                var beforeCount = result.Count;
                ParseCardDropsFromBadgesHtml(html, result);

                if (html.IndexOf("pagelink", StringComparison.OrdinalIgnoreCase) < 0 ||
                    result.Count == beforeCount && page > 1)
                {
                    break;
                }
            }

            return result;
        }

        private static void ParseCardDropsFromBadgesHtml(string html, Dictionary<uint, int> result)
        {
            var rowMatches = Regex.Matches(
                html,
                @"<div[^>]+class=""[^""]*badge_row[^""]*""(?<row>.*?)(?=<div[^>]+class=""[^""]*badge_row|\z)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match rowMatch in rowMatches)
            {
                var row = rowMatch.Groups["row"].Value;
                var appMatch = Regex.Match(row, @"gamecards/(?<appid>\d+)", RegexOptions.IgnoreCase);
                if (appMatch.Success == false ||
                    uint.TryParse(appMatch.Groups["appid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) == false)
                {
                    continue;
                }

                var block = StripHtmlTags(WebUtility.HtmlDecode(row));

                var dropMatch = Regex.Match(
                    block,
                    @"(?:Ещё|Еще)\s+выпадет\s+карточек:\s*(?<count>\d+)|(?<count>\d+)\s+card\s+drops?\s+remaining|card\s+drops?\s+remaining:\s*(?<count>\d+)|(?<count>\d+)\s+карточ(?:ка|ки|ек)\s+ещ[её]\s+выпад",
                    RegexOptions.IgnoreCase);
                if (dropMatch.Success == true &&
                    int.TryParse(dropMatch.Groups["count"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) == true)
                {
                    result[appId] = count;
                    continue;
                }

                if (Regex.IsMatch(block, @"больше\s+не\s+выпадут|no\s+card\s+drops?\s+remaining", RegexOptions.IgnoreCase) == true)
                {
                    result[appId] = 0;
                }
            }
        }

        private static string StripHtmlTags(string html)
        {
            return Regex.Replace(html, "<.*?>", " ");
        }

        private GameInfo GetFocusedGame()
        {
            if (this._CurrentLibraryView == View.Details && this._TableGridView != null)
            {
                if (this._TableGridView.CurrentRow?.Tag is GameInfo currentInfo)
                {
                    return currentInfo;
                }

                if (this._TableGridView.SelectedRows.Count > 0 && this._TableGridView.SelectedRows[0].Tag is GameInfo selectedInfo)
                {
                    return selectedInfo;
                }

                return null;
            }

            var index = this._GameListView.SelectedIndices.Count > 0
                ? this._GameListView.SelectedIndices[0]
                : this._GameListView.FocusedItem?.Index ?? -1;
            return index >= 0 && index < this._FilteredGames.Count
                ? this._FilteredGames[index]
                : null;
        }

        private void OpenSelectedGameUrl(string urlFormat)
        {
            var info = this.GetFocusedGame();
            if (info == null)
            {
                return;
            }

            OpenUrl(string.Format(CultureInfo.InvariantCulture, urlFormat, info.Id));
        }

        private static void OpenUrl(string url)
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }

        private void OnGameListViewDrawItem(object sender, DrawListViewItemEventArgs e)
        {
            if (this._CurrentLibraryView != View.LargeIcon)
            {
                e.DrawDefault = true;
                return;
            }

            e.DrawDefault = true;

            if (e.Item.Bounds.IntersectsWith(this._GameListView.ClientRectangle) == false)
            {
                return;
            }

            var info = this._FilteredGames[e.ItemIndex];
            if (info.ImageIndex <= 0)
            {
                this.AddGameToLogoQueue(info);
                this.DownloadNextLogo();
            }
        }

        private enum PickerKeyValueType : byte
        {
            None = 0,
            String = 1,
            Int32 = 2,
            Float32 = 3,
            Pointer = 4,
            WideString = 5,
            Color = 6,
            UInt64 = 7,
            End = 8,
        }

        private sealed class PickerKeyValue
        {
            private static readonly PickerKeyValue Invalid = new();

            public string Name = "<root>";
            public PickerKeyValueType Type;
            public object Value;
            public bool Valid;
            public List<PickerKeyValue> Children;

            public PickerKeyValue this[string key]
            {
                get
                {
                    if (this.Children == null)
                    {
                        return Invalid;
                    }

                    return this.Children.SingleOrDefault(child => string.Equals(child.Name, key, StringComparison.OrdinalIgnoreCase)) ?? Invalid;
                }
            }

            public string AsString(string defaultValue)
            {
                return this.Valid == true && this.Value != null ? this.Value.ToString() : defaultValue;
            }

            public int AsInteger(int defaultValue)
            {
                if (this.Valid == false || this.Value == null)
                {
                    return defaultValue;
                }

                return this.Type switch
                {
                    PickerKeyValueType.String => int.TryParse((string)this.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : defaultValue,
                    PickerKeyValueType.Int32 => (int)this.Value,
                    PickerKeyValueType.Float32 => (int)(float)this.Value,
                    PickerKeyValueType.UInt64 => (int)((ulong)this.Value & 0xFFFFFFFF),
                    _ => defaultValue,
                };
            }

            public static PickerKeyValue LoadAsBinary(string path)
            {
                if (File.Exists(path) == false)
                {
                    return null;
                }

                try
                {
                    using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    PickerKeyValue kv = new();
                    return kv.ReadAsBinary(input) == true ? kv : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            private bool ReadAsBinary(Stream input)
            {
                this.Children = new();
                try
                {
                    while (true)
                    {
                        var type = (PickerKeyValueType)input.ReadByte();
                        if (type == PickerKeyValueType.End)
                        {
                            break;
                        }

                        PickerKeyValue current = new()
                        {
                            Type = type,
                            Name = ReadNullTerminatedUtf8(input),
                        };

                        switch (type)
                        {
                            case PickerKeyValueType.None:
                                current.ReadAsBinary(input);
                                break;

                            case PickerKeyValueType.String:
                                current.Valid = true;
                                current.Value = ReadNullTerminatedUtf8(input);
                                break;

                            case PickerKeyValueType.Int32:
                                current.Valid = true;
                                current.Value = ReadInt32(input);
                                break;

                            case PickerKeyValueType.Float32:
                                current.Valid = true;
                                current.Value = BitConverter.ToSingle(ReadBytes(input, 4), 0);
                                break;

                            case PickerKeyValueType.UInt64:
                                current.Valid = true;
                                current.Value = BitConverter.ToUInt64(ReadBytes(input, 8), 0);
                                break;

                            case PickerKeyValueType.Color:
                            case PickerKeyValueType.Pointer:
                                current.Valid = true;
                                current.Value = BitConverter.ToUInt32(ReadBytes(input, 4), 0);
                                break;

                            default:
                                return false;
                        }

                        this.Children.Add(current);
                    }

                    this.Valid = true;
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            private static int ReadInt32(Stream input)
            {
                return BitConverter.ToInt32(ReadBytes(input, 4), 0);
            }

            private static byte[] ReadBytes(Stream input, int count)
            {
                var data = new byte[count];
                var read = input.Read(data, 0, count);
                if (read != count)
                {
                    throw new EndOfStreamException();
                }

                return data;
            }

            private static string ReadNullTerminatedUtf8(Stream input)
            {
                List<byte> bytes = new();
                while (true)
                {
                    int value = input.ReadByte();
                    if (value < 0)
                    {
                        throw new EndOfStreamException();
                    }

                    if (value == 0)
                    {
                        return Encoding.UTF8.GetString(bytes.ToArray());
                    }

                    bytes.Add((byte)value);
                }
            }
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
            public const int SwHide = 0;
            public const int SwShow = 5;
            public const int RdwInvalidate = 0x0001;
            public const int RdwErase = 0x0004;
            public const int RdwAllChildren = 0x0080;
            public const int RdwUpdateNow = 0x0100;

            [DllImport("user32.dll")]
            public static extern bool ReleaseCapture();

            [DllImport("user32.dll")]
            public static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

            [DllImport("user32.dll")]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, int flags);

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
