using Outlook = Microsoft.Office.Interop.Outlook;

namespace CaravanCMS.Client.Services;

/// <summary>Opens a reviewable draft in the PC's desktop Outlook via COM automation — it only ever
/// calls Display(), never Send(), so nothing leaves the building without a person clicking Send in
/// Outlook themselves. Chosen over SMTP so the app never needs to store mailbox credentials, and so
/// there's always a human review step before an email reaches a customer or an insurer.
/// Requires classic desktop Outlook installed on the PC (same client the Outlook add-in targets).</summary>
public static class OutlookEmailService
{
    public static void OpenDraft(string? to, string subject, string body, IEnumerable<string>? attachmentPaths = null)
    {
        Outlook.Application outlookApp = new();
        Outlook.MailItem mail = (Outlook.MailItem)outlookApp.CreateItem(Outlook.OlItemType.olMailItem);

        if (!string.IsNullOrWhiteSpace(to)) mail.To = to;
        mail.Subject = subject;
        mail.Body = body;

        if (attachmentPaths is not null)
        {
            foreach (string path in attachmentPaths)
                mail.Attachments.Add(path, Outlook.OlAttachmentType.olByValue, Type.Missing, Type.Missing);
        }

        mail.Display(false);
    }
}
