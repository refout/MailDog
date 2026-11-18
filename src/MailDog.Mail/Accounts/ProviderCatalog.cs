using MailDog.Contracts.Dto;

namespace MailDog.Mail.Accounts;

public sealed record ProviderConfig(
    string ImapHost,
    int ImapPort,
    bool ImapStartTls,
    string SmtpHost,
    int SmtpPort,
    bool SmtpStartTls);

public static class ProviderCatalog
{
    public static ProviderConfig Get(MailProvider provider)
        => provider switch
        {
            MailProvider.QQ => new ProviderConfig(
                "imap.qq.com", 993, ImapStartTls: false,
                "smtp.qq.com", 465, SmtpStartTls: false),
            _ => new ProviderConfig(
                "outlook.office365.com", 993, ImapStartTls: false,
                "smtp.office365.com", 587, SmtpStartTls: true)
        };
}
