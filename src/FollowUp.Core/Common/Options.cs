using FollowUp.Domain;

namespace FollowUp.Core.Common;

/// <summary>Application options bound from the "FollowUp" configuration section.</summary>
public sealed class FollowUpOptions
{
    public const string Section = "FollowUp";

    public string OrganisationName { get; set; } = "Wings Software";
    /// <summary>Public URL of the web UI — used for links in emails and Teams messages.</summary>
    public string PublicAppUrl { get; set; } = "http://localhost:5860";
    /// <summary>Overdue by this many days or more is "severely overdue".</summary>
    public int SeverelyOverdueDays { get; set; } = 7;
    /// <summary>A meaningful update inside this window suppresses a follow-up reminder.</summary>
    public int RecentUpdateWindowHours { get; set; } = 24;
    /// <summary>Anti-spam: at most this many reminder-type notifications per recipient per day.</summary>
    public int MaxRemindersPerRecipientPerDay { get; set; } = 6;
    /// <summary>A follow-up point missed by more than this (downtime) is coalesced into one catch-up reminder.</summary>
    public int FollowUpCatchUpGraceMinutes { get; set; } = 90;
    /// <summary>Unsent reminders older than this are expired rather than delivered late (spec §62).</summary>
    public int ReminderExpiryHours { get; set; } = 20;
    /// <summary>Messages matched with at least this confidence are attached automatically; lower ones go to the review queue.</summary>
    public double AutoAttachConfidence { get; set; } = 0.80;
    /// <summary>Below this confidence a possible match is not even suggested.</summary>
    public double SuggestConfidence { get; set; } = 0.45;
    /// <summary>Tasks at or above this criticality require acknowledgement by the responsible person.</summary>
    public Criticality AcknowledgementFrom { get; set; } = Criticality.Normal;
    /// <summary>Unacknowledged important tasks enter the CEO attention queue after this many hours.</summary>
    public int AcknowledgementAttentionHours { get; set; } = 24;
    /// <summary>Number of unanswered reminders that makes "repeated non-response".</summary>
    public int RepeatedNonResponseReminders { get; set; } = 3;
    public TimeOnly DefaultFollowUpTime { get; set; } = new(10, 0);
    public bool DailyDigestEnabled { get; set; } = true;
    public TimeOnly DailyDigestTime { get; set; } = new(8, 30);
    public bool WeeklyReviewEnabled { get; set; } = true;
    public DayOfWeek WeeklyReviewDay { get; set; } = DayOfWeek.Monday;
    public TimeOnly WeeklyReviewTime { get; set; } = new(9, 0);
    /// <summary>Push important deadlines / reviews / approvals to Outlook calendars (spec §72).</summary>
    public bool CalendarSyncEnabled { get; set; } = true;
    /// <summary>Background communication processing batch size.</summary>
    public int ProcessingBatchSize { get; set; } = 50;
    /// <summary>Retention for real-time UI events and job history.</summary>
    public int UiEventRetentionHours { get; set; } = 48;
    public int JobHistoryRetentionDays { get; set; } = 30;
    /// <summary>Folder for encrypted attachments.</summary>
    public string StoragePath { get; set; } = "data/attachments";
    /// <summary>Seed the demo organisation, tasks and Microsoft 365 emulator content on first start.</summary>
    public bool SeedDemoData { get; set; } = true;
    /// <summary>Addresses / Graph user ids this application sends from — their messages are never re-processed (loop guard).</summary>
    public List<string> SystemSenders { get; set; } = [];
}

/// <summary>Options for the durable job engine ("Jobs" section).</summary>
public sealed class JobEngineOptions
{
    public const string Section = "Jobs";

    /// <summary>Run the background processor in this process (the Worker service always does; the API can too).</summary>
    public bool Enabled { get; set; } = true;
    public int PollSeconds { get; set; } = 5;
    public int MaxConcurrency { get; set; } = 4;
    /// <summary>Extra lease time beyond a job's timeout before another host may take it over.</summary>
    public int LeaseGraceSeconds { get; set; } = 60;
    /// <summary>Overrides per job key: interval seconds.</summary>
    public Dictionary<string, int> Intervals { get; set; } = [];
}
