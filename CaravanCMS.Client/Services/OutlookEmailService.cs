namespace CaravanCMS.Client.Services;

/// <summary>Opens a reviewable draft in the PC's desktop Outlook via late-bound COM automation — it
/// only ever calls Display(), never Send(), so nothing leaves the building without a person clicking
/// Send in Outlook themselves. Chosen over SMTP so the app never needs to store mailbox credentials,
/// and so there's always a human review step before an email reaches a customer or an insurer.
///
/// Deliberately NOT using the Microsoft.Office.Interop.Outlook NuGet package: that package's managed
/// wrapper assembly itself references "office" (the Microsoft.Office.Core PIA), which is only present
/// if Office PIAs were installed alongside Office — not guaranteed on every PC, and not true here,
/// which is exactly what broke "Package and Send" with a FileNotFoundException for `office`. Late
/// binding through plain COM IDispatch (Type.GetTypeFromProgID + dynamic) needs nothing but Outlook
/// itself, so it works on any PC that has it installed, PIAs or not.
/// Requires classic desktop Outlook installed on the PC (same client the Outlook add-in targets).</summary>
public static class OutlookEmailService
{
    private const int OlMailItem = 0;
    private const int OlByValue = 1;

    public static void OpenDraft(string? to, string subject, string body, IEnumerable<string>? attachmentPaths = null)
    {
        Type outlookType = Type.GetTypeFromProgID("Outlook.Application")
            ?? throw new InvalidOperationException("Outlook doesn't appear to be installed on this PC.");
        dynamic outlookApp = Activator.CreateInstance(outlookType)!;
        dynamic mail = outlookApp.CreateItem(OlMailItem);

        if (!string.IsNullOrWhiteSpace(to)) mail.To = to;
        mail.Subject = subject;
        mail.Body = body;

        if (attachmentPaths is not null)
        {
            foreach (string path in attachmentPaths)
                mail.Attachments.Add(path, OlByValue, Type.Missing, Type.Missing);
        }

        mail.Display(false);
    }
}
