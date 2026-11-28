using Microsoft.Data.SqlClient;

namespace FMSC_SMS.Core;

/// <summary>One authenticated check per opening/month. SQL owns eligibility, locks and posting.</summary>
internal static class AutomaticMonthlyBilling
{
    private static bool running;
    private static DateTime? checkedMonth;
    private static int checkedActor;
    internal static event Action<string>? StatusChanged;

    internal static Task<DataSet> ReviewAsync() => ExecuteAsync(true, false, false, null);

    private static Task<DataSet> ExecuteAsync(bool preview, bool includeMissed, bool reviewMissed, string? snapshot)
    {
        int actor = CurrentUser.UserId;
        return DatabaseHelper.QueryUserSetAsync("RunAutomaticMonthlyBilling",
            DatabaseHelper.Bit("@Preview", preview), DatabaseHelper.Bit("@IncludeMissed", includeMissed),
            DatabaseHelper.Bit("@ReviewMissed", reviewMissed), DatabaseHelper.P("@ExpectedSnapshot", snapshot),
            DatabaseHelper.Int("@UserId", actor));
    }

    internal static async Task CheckAsync(IWin32Window owner, bool force = false)
    {
        DateTime month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
        if (running || (!force && checkedMonth == month && checkedActor == CurrentUser.UserId) || CurrentUser.UserId <= 0 ||
            !CurrentUser.HasPermission("Fees.Collect")) return;
        running = true;
        int actor = CurrentUser.UserId;
        try
        {
            using DataSet review = await ReviewAsync();
            if (CurrentUser.UserId != actor || owner is Control { IsDisposed: true }) return;
            DataRow summary = review.Tables[0].Rows[0];
            bool missed = Convert.ToInt32(summary["MissedCharges"]) > 0;
            bool administrator = CurrentUser.HasPermission("Students.Administer");
            bool includeMissed = false;
            bool reviewed = false;
            int actionable = review.Tables[2].AsEnumerable().Count(row => !Convert.ToBoolean(row["IsLegacyReference"]));
            int references = review.Tables[2].Rows.Count - actionable;
            if (missed && administrator)
            {
                using var choice = new BillingRecoveryDialog(review.Tables[1]);
                choice.ShowDialog(owner);
                includeMissed = choice.IncludeMissed;
                reviewed = choice.Reviewed;
            }
            if (CurrentUser.UserId != actor || owner is Control { IsDisposed: true }) return;
            if (Convert.ToInt32(summary["CurrentCharges"]) > 0 ||
                Convert.ToInt32(summary["PendingCreditApplications"]) > 0 || includeMissed || reviewed)
            {
                using DataSet posted = await ExecuteAsync(false, includeMissed, reviewed,
                    Convert.ToString(summary["Snapshot"]));
                int created = Convert.ToInt32(posted.Tables[0].Rows[0]["ChargesCreated"]);
                StatusChanged?.Invoke($"Monthly billing: {created:N0} charges created. " +
                    $"{actionable:N0} students need review; {references:N0} legacy references excluded.");
            }
            else StatusChanged?.Invoke($"Monthly billing is up to date. {actionable:N0} students need review; {references:N0} legacy references excluded.");
            checkedMonth = month;
            checkedActor = CurrentUser.UserId;
        }
        catch (Exception ex)
        {
            // No success marker on failure. Reopening the workspace or reconnecting permits a fresh review.
            StatusChanged?.Invoke("Monthly billing needs attention. Open Billing Status to review and retry.");
            ModernMessageDialog.ShowError(owner, DbMessage.UserMessage(ex), "Monthly Billing");
        }
        finally { running = false; }
    }

    internal static void Reset() => checkedMonth = null;
}

internal sealed class BillingRecoveryDialog : ApplicationWindowForm
{
    internal bool IncludeMissed { get; private set; }
    internal bool Reviewed { get; private set; }

    internal BillingRecoveryDialog(DataTable months)
    {
        Text = "Missed Monthly Fees";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(AppScale.Scale(900), AppScale.Scale(560));
        MinimumSize = new Size(AppScale.Scale(680), AppScale.Scale(430));
        BackColor = AppTheme.PageBackground;
        Font = AppTheme.NormalFont;

        var heading = new Label
        {
            Text = "Missed Monthly Fees",
            Dock = DockStyle.Fill,
            Font = AppTheme.PageTitleFont,
            ForeColor = AppTheme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        };
        var explanation = new Label
        {
            Text = "Some earlier months have not been billed. Review the months and amounts below.\r\n\r\n" +
                   "Bill missed months too: creates the listed earlier charges as well as this month's fees.\r\n" +
                   "Bill this month only: leaves earlier gaps unbilled and available for later administrative recovery.\r\n" +
                   "Previously reversed fees and periods before approved billing commencement are excluded.",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = AppTheme.NormalFont,
            ForeColor = AppTheme.MutedText,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
            Padding = new Padding(0, AppMetrics.SpacingXs, 0, AppMetrics.SpacingSm)
        };

        var grid = HumanUi.Grid(false);
        grid.AutoGenerateColumns = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "BillingMonth",
            DataPropertyName = "BillingMonth",
            HeaderText = "Billing month",
            FillWeight = 160,
            MinimumWidth = AppScale.Scale(180)
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Students",
            DataPropertyName = "Students",
            HeaderText = "Students",
            FillWeight = 75,
            MinimumWidth = AppScale.Scale(90)
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Charges",
            DataPropertyName = "Charges",
            HeaderText = "Charges",
            FillWeight = 75,
            MinimumWidth = AppScale.Scale(90)
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Amount",
            DataPropertyName = "Amount",
            HeaderText = "Amount",
            FillWeight = 100,
            MinimumWidth = AppScale.Scale(120)
        });
        grid.DataSource = months.Copy();
        grid.Columns["BillingMonth"].DefaultCellStyle.Format = "dd-MMM-yyyy";
        grid.Columns["Students"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        grid.Columns["Charges"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        grid.Columns["Amount"].DefaultCellStyle.Format = "N0";
        grid.Columns["Amount"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        grid.ClearSelection();
        grid.CurrentCell = null;
        grid.Margin = Padding.Empty;

        ModernButton Choice(string caption, bool all, string themeRole)
        {
            var button = new ModernButton
            {
                Text = caption,
                ThemeRole = themeRole,
                Height = AppMetrics.ButtonHeight,
                Width = AppMetrics.TextWidth(caption, AppTheme.ButtonFont, AppScale.Scale(40)) + AppScale.Scale(32),
                DialogResult = DialogResult.None,
                Margin = new Padding(AppScale.Scale(4), 0, AppScale.Scale(4), 0)
            };
            button.Click += (_, _) =>
            {
                IncludeMissed = all;
                Reviewed = true;
                DialogResult = DialogResult.OK;
                Close();
            };
            return button;
        }

        var includeMissedButton = Choice("Bill the missed months too", true, "Primary");
        var currentMonthButton = Choice("Bill this month only", false, "Secondary");
        var laterButton = new ModernButton
        {
            Text = "Decide later",
            ThemeRole = "Secondary",
            Height = AppMetrics.ButtonHeight,
            Width = AppMetrics.TextWidth("Decide later", AppTheme.ButtonFont, AppScale.Scale(40)) + AppScale.Scale(32),
            DialogResult = DialogResult.Cancel,
            Margin = new Padding(AppScale.Scale(4), 0, AppScale.Scale(4), 0)
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        buttons.Controls.Add(includeMissedButton);
        buttons.Controls.Add(currentMonthButton);
        buttons.Controls.Add(laterButton);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(AppMetrics.SpacingLg),
            Margin = Padding.Empty,
            BackColor = AppTheme.PageBackground
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,
            Math.Max(AppMetrics.TextHeight(AppTheme.PageTitleFont) + AppMetrics.SpacingSm, AppScale.Scale(38))));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,
            9 * AppMetrics.TextHeight(AppTheme.NormalFont) + AppMetrics.SpacingLg));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, AppMetrics.ButtonHeight + AppMetrics.SpacingLg));

        root.Controls.Add(heading, 0, 0);
        root.Controls.Add(explanation, 0, 1);
        root.Controls.Add(grid, 0, 2);
        root.Controls.Add(buttons, 0, 3);
        Controls.Add(root);

        CancelButton = laterButton;
        AcceptButton = null;
    }
}
