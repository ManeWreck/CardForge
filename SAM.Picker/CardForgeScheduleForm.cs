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
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SAM.Picker
{
    internal sealed class CardForgeScheduleForm : Form
    {
        private readonly Func<IReadOnlyList<GameInfo>> _GetGames;
        private readonly Func<IReadOnlyList<GameInfo>> _GetFavorites;
        private readonly Action<GameInfo> _OpenGame;
        private readonly Action<GameInfo> _CloseGame;
        private readonly Func<Task<Dictionary<uint, int>>> _RefreshCardDrops;
        private readonly Timer _Timer;
        private readonly List<GameInfo> _RunQueue;
        private readonly List<GameInfo> _RunningGames;
        private readonly Dictionary<uint, int?> _LastCardDrops;

        private ListView _GameListView;
        private ListBox _QueueListBox;
        private NumericUpDown _DurationMinutesBox;
        private NumericUpDown _MaxConcurrentBox;
        private CheckBox _RepeatCheckBox;
        private CheckBox _RotateQueueCheckBox;
        private CheckBox _CardAwareCheckBox;
        private CheckBox _RefreshDropsCheckBox;
        private Button _StartButton;
        private Button _StopButton;
        private Label _StatusLabel;
        private TextBox _LogTextBox;

        private int _QueueIndex;
        private DateTime? _NextCycleAt;
        private bool _ScheduleRunning;
        private bool _CycleBusy;
        private bool _StopAfterCurrentBatch;

        public CardForgeScheduleForm(
            Func<IReadOnlyList<GameInfo>> getGames,
            Func<IReadOnlyList<GameInfo>> getFavorites,
            Action<GameInfo> openGame,
            Action<GameInfo> closeGame,
            Func<Task<Dictionary<uint, int>>> refreshCardDrops)
        {
            this._GetGames = getGames;
            this._GetFavorites = getFavorites;
            this._OpenGame = openGame;
            this._CloseGame = closeGame;
            this._RefreshCardDrops = refreshCardDrops;
            this._RunQueue = new();
            this._RunningGames = new();
            this._LastCardDrops = new();
            this._Timer = new Timer
            {
                Interval = 1000,
            };
            this._Timer.Tick += this.OnTimerTick;

            this.InitializeUi();
            this.PopulateGames();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            this._Timer.Stop();
            base.OnFormClosing(e);
        }

        private void InitializeUi()
        {
            this.Text = "CardForge Scheduler";
            this.MinimumSize = new Size(980, 640);
            this.Size = new Size(1180, 760);
            this.BackColor = Color.FromArgb(14, 17, 22);
            this.ForeColor = Color.FromArgb(232, 238, 247);

            TableLayoutPanel root = new()
            {
                BackColor = Color.FromArgb(14, 17, 22),
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                RowCount = 3,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58F));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 162F));

            Label title = new()
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 13F, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(232, 238, 247),
                Text = "Launch scheduler",
                TextAlign = ContentAlignment.MiddleLeft,
            };
            root.Controls.Add(title, 0, 0);
            root.SetColumnSpan(title, 2);

            this._GameListView = new ListView
            {
                BackColor = Color.FromArgb(14, 17, 22),
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(232, 238, 247),
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = true,
                UseCompatibleStateImageBehavior = false,
                View = View.Details,
            };
            this._GameListView.Columns.Add("Game", 330);
            this._GameListView.Columns.Add("AppID", 90);
            this._GameListView.Columns.Add("Hours", 80);
            this._GameListView.Columns.Add("Cards", 70);
            root.Controls.Add(this._GameListView, 0, 1);

            TableLayoutPanel right = new()
            {
                BackColor = Color.FromArgb(14, 17, 22),
                ColumnCount = 1,
                Dock = DockStyle.Fill,
                RowCount = 2,
            };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 132F));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(right, 1, 1);

            FlowLayoutPanel queueButtons = new()
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(8, 0, 0, 8),
                WrapContents = true,
            };
            queueButtons.Controls.Add(this.CreateButton("Add selected", (sender, e) => this.AddSelectedGames()));
            queueButtons.Controls.Add(this.CreateButton("Add favorites", (sender, e) => this.AddGames(this._GetFavorites())));
            queueButtons.Controls.Add(this.CreateButton("Add cards left", (sender, e) => this.AddGames(this.GetGamesWithCardsLeft())));
            queueButtons.Controls.Add(this.CreateButton("Remove", (sender, e) => this.RemoveSelectedQueueItems()));
            queueButtons.Controls.Add(this.CreateButton("Move up", (sender, e) => this.MoveSelectedQueueItem(-1)));
            queueButtons.Controls.Add(this.CreateButton("Move down", (sender, e) => this.MoveSelectedQueueItem(1)));
            queueButtons.Controls.Add(this.CreateButton("Clear", (sender, e) => this.ClearQueue()));
            queueButtons.Controls.Add(this.CreateButton("Refresh list", (sender, e) => this.PopulateGames()));
            right.Controls.Add(queueButtons, 0, 0);

            this._QueueListBox = new ListBox
            {
                BackColor = Color.FromArgb(18, 21, 27),
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(232, 238, 247),
                IntegralHeight = false,
            };
            right.Controls.Add(this._QueueListBox, 0, 1);

            TableLayoutPanel bottom = new()
            {
                BackColor = Color.FromArgb(18, 21, 27),
                ColumnCount = 4,
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                RowCount = 3,
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190F));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210F));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250F));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(bottom, 0, 2);
            root.SetColumnSpan(bottom, 2);

            this._DurationMinutesBox = this.CreateNumberBox(1, 1440, 60);
            this._MaxConcurrentBox = this.CreateNumberBox(1, 50, 3);
            bottom.Controls.Add(this.CreateFieldLabel("Run each batch, minutes"), 0, 0);
            bottom.Controls.Add(this._DurationMinutesBox, 1, 0);
            bottom.Controls.Add(this.CreateFieldLabel("Max simultaneous games"), 0, 1);
            bottom.Controls.Add(this._MaxConcurrentBox, 1, 1);

            FlowLayoutPanel options = new()
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
            };
            this._RepeatCheckBox = this.CreateCheckBox("Repeat", true);
            this._RotateQueueCheckBox = this.CreateCheckBox("Rotate queue", true);
            this._CardAwareCheckBox = this.CreateCheckBox("Card-aware", false);
            this._RefreshDropsCheckBox = this.CreateCheckBox("Refresh drops each cycle", true);
            options.Controls.Add(this._RepeatCheckBox);
            options.Controls.Add(this._RotateQueueCheckBox);
            options.Controls.Add(this._CardAwareCheckBox);
            options.Controls.Add(this._RefreshDropsCheckBox);
            bottom.Controls.Add(options, 2, 0);
            bottom.SetRowSpan(options, 2);

            FlowLayoutPanel actions = new()
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
            };
            this._StartButton = this.CreateButton("Start", async (sender, e) => await this.StartScheduleAsync());
            this._StopButton = this.CreateButton("Stop", (sender, e) => this.StopSchedule());
            actions.Controls.Add(this._StartButton);
            actions.Controls.Add(this._StopButton);
            bottom.Controls.Add(actions, 3, 0);
            bottom.SetRowSpan(actions, 2);

            this._StatusLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(170, 187, 204),
                Text = "Build a run list, choose timing, then press Start.",
                TextAlign = ContentAlignment.MiddleLeft,
            };
            bottom.Controls.Add(this._StatusLabel, 0, 2);
            bottom.SetColumnSpan(this._StatusLabel, 2);

            this._LogTextBox = new TextBox
            {
                BackColor = Color.FromArgb(14, 17, 22),
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(170, 187, 204),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
            };
            bottom.Controls.Add(this._LogTextBox, 2, 2);
            bottom.SetColumnSpan(this._LogTextBox, 2);

            this.Controls.Add(root);
        }

        private Button CreateButton(string text, EventHandler onClick)
        {
            Button button = new()
            {
                BackColor = Color.FromArgb(28, 33, 42),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(232, 238, 247),
                Height = 30,
                Margin = new Padding(0, 0, 8, 8),
                Text = text,
                UseVisualStyleBackColor = false,
                Width = 118,
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(54, 64, 78);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(54, 64, 78);
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 49, 60);
            button.Click += onClick;
            return button;
        }

        private CheckBox CreateCheckBox(string text, bool isChecked)
        {
            return new CheckBox
            {
                AutoSize = true,
                Checked = isChecked,
                ForeColor = Color.FromArgb(232, 238, 247),
                Margin = new Padding(0, 8, 18, 0),
                Text = text,
                UseVisualStyleBackColor = false,
            };
        }

        private Label CreateFieldLabel(string text)
        {
            return new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(170, 187, 204),
                Text = text,
                TextAlign = ContentAlignment.MiddleLeft,
            };
        }

        private NumericUpDown CreateNumberBox(int min, int max, int value)
        {
            return new NumericUpDown
            {
                BackColor = Color.FromArgb(22, 26, 34),
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Left,
                ForeColor = Color.FromArgb(232, 238, 247),
                Maximum = max,
                Minimum = min,
                Value = value,
                Width = 82,
            };
        }

        private void PopulateGames()
        {
            this._GameListView.BeginUpdate();
            this._GameListView.Items.Clear();
            foreach (var game in this._GetGames())
            {
                ListViewItem item = new(game.Name)
                {
                    Tag = game,
                };
                item.SubItems.Add(game.Id.ToString(CultureInfo.InvariantCulture));
                item.SubItems.Add(FormatPlaytime(game));
                item.SubItems.Add(FormatCards(game));
                this._GameListView.Items.Add(item);
            }

            this._GameListView.EndUpdate();
        }

        private void AddSelectedGames()
        {
            this.AddGames(this._GameListView.SelectedItems
                .Cast<ListViewItem>()
                .Select(item => item.Tag)
                .OfType<GameInfo>());
        }

        private void AddGames(IEnumerable<GameInfo> games)
        {
            foreach (var game in games)
            {
                if (this._RunQueue.Any(existing => existing.Id == game.Id) == true)
                {
                    continue;
                }

                this._RunQueue.Add(game);
            }

            this.RefreshQueueList();
        }

        private IReadOnlyList<GameInfo> GetGamesWithCardsLeft()
        {
            return this._GetGames()
                .Where(game => game.CardDropsRemaining.HasValue == true && game.CardDropsRemaining.Value > 0)
                .OrderBy(game => game.Name)
                .ToList();
        }

        private void RemoveSelectedQueueItems()
        {
            var indexes = this._QueueListBox.SelectedIndices
                .Cast<int>()
                .OrderByDescending(index => index)
                .ToList();
            foreach (var index in indexes)
            {
                this._RunQueue.RemoveAt(index);
            }

            this.RefreshQueueList();
        }

        private void MoveSelectedQueueItem(int direction)
        {
            if (this._QueueListBox.SelectedIndex < 0)
            {
                return;
            }

            var index = this._QueueListBox.SelectedIndex;
            var target = index + direction;
            if (target < 0 || target >= this._RunQueue.Count)
            {
                return;
            }

            var game = this._RunQueue[index];
            this._RunQueue.RemoveAt(index);
            this._RunQueue.Insert(target, game);
            this.RefreshQueueList();
            this._QueueListBox.SelectedIndex = target;
        }

        private void ClearQueue()
        {
            this._RunQueue.Clear();
            this.RefreshQueueList();
        }

        private void RefreshQueueList()
        {
            this._QueueListBox.Items.Clear();
            foreach (var game in this._RunQueue)
            {
                this._QueueListBox.Items.Add(FormatQueueGame(game));
            }
        }

        private async Task StartScheduleAsync()
        {
            if (this._RunQueue.Count == 0)
            {
                MessageBox.Show(this, "Add at least one game to the run list.", "CardForge Scheduler", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (this._ScheduleRunning == true)
            {
                return;
            }

            this._ScheduleRunning = true;
            this._StopAfterCurrentBatch = false;
            this._QueueIndex = 0;
            this._RunningGames.Clear();
            this._LastCardDrops.Clear();
            foreach (var game in this._RunQueue)
            {
                this._LastCardDrops[game.Id] = game.CardDropsRemaining;
            }

            this._Timer.Start();
            this.Log("Scheduler started.");
            await this.RunCycleAsync(true);
        }

        private void StopSchedule()
        {
            this._ScheduleRunning = false;
            this._StopAfterCurrentBatch = false;
            this._Timer.Stop();
            this._NextCycleAt = null;
            this.CloseRunningGames();
            this._RunningGames.Clear();
            this._StatusLabel.Text = "Scheduler stopped.";
            this.Log("Scheduler stopped.");
        }

        private async void OnTimerTick(object sender, EventArgs e)
        {
            if (this._ScheduleRunning == false || this._CycleBusy == true || this._NextCycleAt.HasValue == false)
            {
                return;
            }

            var remaining = this._NextCycleAt.Value - DateTime.Now;
            if (remaining <= TimeSpan.Zero)
            {
                await this.RunCycleAsync(false);
                return;
            }

            this._StatusLabel.Text =
                $"Next cycle in {Math.Ceiling(remaining.TotalSeconds).ToString(CultureInfo.InvariantCulture)} sec. Running: {string.Join(", ", this._RunningGames.Select(game => game.Name))}";
        }

        private async Task RunCycleAsync(bool initial)
        {
            if (this._CycleBusy == true)
            {
                return;
            }

            this._CycleBusy = true;
            try
            {
                if (initial == false)
                {
                    this.CloseRunningGames();
                    if (this._StopAfterCurrentBatch == true)
                    {
                        this._ScheduleRunning = false;
                        this._StopAfterCurrentBatch = false;
                        this._Timer.Stop();
                        this._NextCycleAt = null;
                        this._RunningGames.Clear();
                        this._StatusLabel.Text = "Single cycle finished.";
                        this.Log("Single cycle finished.");
                        return;
                    }

                    if (this._RefreshDropsCheckBox.Checked == true)
                    {
                        this.Log("Refreshing card drops before next cycle...");
                        await this._RefreshCardDrops();
                    }
                }
                else if (this._CardAwareCheckBox.Checked == true && this._RefreshDropsCheckBox.Checked == true)
                {
                    this.Log("Refreshing card drops before first cycle...");
                    await this._RefreshCardDrops();
                }

                var nextBatch = this.SelectNextBatch(initial);
                if (nextBatch.Count == 0)
                {
                    this._ScheduleRunning = false;
                    this._Timer.Stop();
                    this._StatusLabel.Text = "Scheduler stopped: no eligible games left.";
                    this.Log("No eligible games left.");
                    return;
                }

                this._RunningGames.Clear();
                foreach (var game in nextBatch)
                {
                    this._OpenGame(game);
                    this._RunningGames.Add(game);
                    this.Log("Launched " + game.Name + ".");
                }

                foreach (var game in this._RunQueue)
                {
                    this._LastCardDrops[game.Id] = game.CardDropsRemaining;
                }

                var minutes = (double)this._DurationMinutesBox.Value;
                this._NextCycleAt = DateTime.Now.AddMinutes(minutes);
                this._StatusLabel.Text =
                    $"Running {nextBatch.Count.ToString(CultureInfo.InvariantCulture)} game(s) for {minutes.ToString("0", CultureInfo.InvariantCulture)} minute(s).";

                if (this._RepeatCheckBox.Checked == false)
                {
                    this._StopAfterCurrentBatch = true;
                    this.Log("Single cycle started. Repeat is off, so scheduler will stop after this batch.");
                }
            }
            finally
            {
                this._CycleBusy = false;
            }
        }

        private List<GameInfo> SelectNextBatch(bool initial)
        {
            var max = (int)this._MaxConcurrentBox.Value;
            var eligible = this._RunQueue
                .Where(this.IsEligibleForCardMode)
                .ToList();

            if (this._CardAwareCheckBox.Checked == true && initial == false)
            {
                var keepRunning = this._RunningGames
                    .Where(game => eligible.Any(candidate => candidate.Id == game.Id))
                    .Where(game => this.HadNoNewDrop(game) == true)
                    .Take(max)
                    .ToList();

                if (keepRunning.Count >= max)
                {
                    return keepRunning;
                }

                var fill = this.SelectFromQueue(eligible, max - keepRunning.Count)
                    .Where(game => keepRunning.Any(existing => existing.Id == game.Id) == false)
                    .ToList();
                keepRunning.AddRange(fill);
                return keepRunning;
            }

            if (this._RotateQueueCheckBox.Checked == false)
            {
                return eligible.Take(max).ToList();
            }

            return this.SelectFromQueue(eligible, max);
        }

        private List<GameInfo> SelectFromQueue(IReadOnlyList<GameInfo> eligible, int max)
        {
            List<GameInfo> result = new();
            if (max <= 0 || eligible.Count == 0 || this._RunQueue.Count == 0)
            {
                return result;
            }

            int scanned = 0;
            while (result.Count < max && scanned < this._RunQueue.Count)
            {
                if (this._QueueIndex >= this._RunQueue.Count)
                {
                    this._QueueIndex = 0;
                }

                var game = this._RunQueue[this._QueueIndex];
                this._QueueIndex++;
                scanned++;

                if (eligible.Any(candidate => candidate.Id == game.Id) == false ||
                    result.Any(existing => existing.Id == game.Id) == true)
                {
                    continue;
                }

                result.Add(game);
            }

            return result;
        }

        private bool IsEligibleForCardMode(GameInfo game)
        {
            if (this._CardAwareCheckBox.Checked == false)
            {
                return true;
            }

            return game.CardDropsRemaining.HasValue == false || game.CardDropsRemaining.Value > 0;
        }

        private bool HadNoNewDrop(GameInfo game)
        {
            if (this._CardAwareCheckBox.Checked == false)
            {
                return true;
            }

            if (this._LastCardDrops.TryGetValue(game.Id, out var before) == false ||
                before.HasValue == false ||
                game.CardDropsRemaining.HasValue == false)
            {
                return true;
            }

            return game.CardDropsRemaining.Value >= before.Value;
        }

        private void CloseRunningGames()
        {
            foreach (var game in this._RunningGames.ToArray())
            {
                this._CloseGame(game);
                this.Log("Closed " + game.Name + ".");
            }
        }

        private void Log(string message)
        {
            this._LogTextBox.AppendText(
                DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture) +
                "  " +
                message +
                Environment.NewLine);
        }

        private static string FormatPlaytime(GameInfo game)
        {
            return game.PlaytimeMinutes.HasValue == true
                ? (game.PlaytimeMinutes.Value / 60.0).ToString("0.0", CultureInfo.InvariantCulture) + " h"
                : "-";
        }

        private static string FormatCards(GameInfo game)
        {
            return game.CardDropsRemaining.HasValue == true
                ? game.CardDropsRemaining.Value.ToString(CultureInfo.InvariantCulture)
                : "-";
        }

        private static string FormatQueueGame(GameInfo game)
        {
            return game.Name +
                   "  |  AppID " +
                   game.Id.ToString(CultureInfo.InvariantCulture) +
                   "  |  Cards left: " +
                   FormatCards(game);
        }
    }
}
