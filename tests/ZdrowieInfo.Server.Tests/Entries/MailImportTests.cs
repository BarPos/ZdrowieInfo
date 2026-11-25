using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ZdrowieInfo.Server.Entries;
using ZdrowieInfo.Server.Tests.Infrastructure;

namespace ZdrowieInfo.Server.Tests.Entries;

/// <summary>
/// Import maili .eml do wpisów własnych. Maile są syntetyczne (fikcyjne osoby i adresy), ale z budową prawdziwych:
/// przekazanie z Thunderbirda, HTML z Worda z tabelą, obrazy w multipart/related, zawinięty temat RFC 2047, stopka z RODO.
/// </summary>
public class MailImportTests(PostgresFixture postgres)
{
    private const string ThunderbirdForward = """
        Return-Path: <sekretariat@przychodnia.example>
        Content-Type: multipart/alternative;
         boundary="------------ALT"
        Message-ID: <fwd-1@przychodnia.example>
        Date: Thu, 26 Nov 2026 09:00:00 +0100
        MIME-Version: 1.0
        Subject: =?UTF-8?Q?Fwd=3A_Ostrze=C5=BCenie_o_podatno=C5=9Bciach_w_Przyk?=
         =?UTF-8?Q?=C5=82ad_Router?=
        To: Jan Testowy <jan@przychodnia.example>
        From: Anna Przekazuj=?UTF-8?Q?=C4=85ca?= <sekretariat@przychodnia.example>
        X-Forwarded-Message-Id: <ORYGINAL-123@mon.gov.pl>

        This is a multi-part message in MIME format.
        --------------ALT
        Content-Type: text/plain; charset=UTF-8; format=flowed
        Content-Transfer-Encoding: 8bit

        --- Treść przekazanej wiadomości ---
        Temat: Ostrzeżenie o podatnościach w Przykład Router
        Data: Wed, 25 Nov 2026 23:30:00 +0000
        Nadawca: Podatności CSIRT-MON <podatnosci.csirtmon@mon.gov.pl>

        Szanowni Państwo, przesyłamy informacje.

        --------------ALT
        Content-Type: multipart/related;
         boundary="------------REL"

        --------------REL
        Content-Type: text/html; charset=UTF-8
        Content-Transfer-Encoding: 8bit

        <html><head><style>p.MsoNormal{margin:0}</style></head><body>
        <p>Proszę o weryfikację – Anna</p>
        <div class="moz-forward-container"><br><br>
          --- Treść przekazanej wiadomości ---
          <table class="moz-email-headers-table">
            <tr><th>Temat: </th><td>Ostrzeżenie o podatnościach w
              Przykład Router</td></tr>
            <tr><th>Data: </th><td>Wed, 25 Nov 2026 23:30:00 +0000</td></tr>
            <tr><th>Nadawca: </th><td>Podatności CSIRT-MON <a href="mailto:podatnosci.csirtmon@mon.gov.pl">&lt;podatnosci.csirtmon@mon.gov.pl&gt;</a></td></tr>
          </table><br><br>
          <div class="WordSection1">
            <p class="MsoNormal">Szanowni Państwo,</p>
            <p class="MsoNormal">przesyłamy informacje na temat podatności w<span> </span><b>Przykład Router.</b></p>
            <table class="MsoTableGrid" border="1"><tr><td><p><b>Produkt</b></p></td><td><p><b>CVE</b></p></td></tr>
              <tr><td><p>Przykład Router</p></td><td><p>CVE-2026-55555</p></td></tr></table>
            <p class="MsoNormal">Opis: <a href="https://producent.example/advisory">https://producent.example/advisory</a></p>
            <p class="MsoNormal"><img src="cid:logo@x" alt="logo"></p>
            <p class="MsoNormal"><b><i>Z wyrazami szacunku</i></b></p>
            <p class="MsoNormal">Zarządzanie Podatnościami, ul. Przykładowa 1, 00-000 Miasto</p>
            <p class="MsoNormal">Administratorem Pani/Pana danych osobowych jest … RODO … kontakt: iod@mon.gov.pl</p>
          </div>
        </div></body></html>

        --------------REL
        Content-Type: image/png; name="logo.png"
        Content-Disposition: inline; filename="logo.png"
        Content-ID: <logo@x>
        Content-Transfer-Encoding: base64

        iVBORw0KGgo=
        --------------REL--

        --------------ALT--
        """;

    private const string PlainLatin2 = """
        Message-ID: <plain-7@cert.example>
        Date: Mon, 23 Nov 2026 10:00:00 +0100
        MIME-Version: 1.0
        Subject: =?ISO-8859-2?Q?Krytyczna_podatno=B6=E6_w_VPN?=
        From: =?ISO-8859-2?Q?Zesp=F3=B3_Bezpiecze=F1stwa?= <zespol@cert.example>
        Content-Type: text/plain; charset=ISO-8859-2
        Content-Transfer-Encoding: quoted-printable

        Wykryto podatno=B6=E6 w bramie VPN.
        Zalecana aktualizacja do wersji 7.4.3.

        Pozdrawiam
        Jan Kowalski, tel. 123 456 789
        """;

    private const string OutlookForward = """
        Message-ID: <out-1@przychodnia.example>
        Date: Tue, 24 Nov 2026 08:00:00 +0100
        MIME-Version: 1.0
        Subject: PD: Komunikat
        From: Ktos <ktos@przychodnia.example>
        Content-Type: text/html; charset=UTF-8

        <html><body><p>Do wiadomości.</p>
        <div style="border-top:solid #E1E1E1 1.0pt"><p><b>Od:</b> CERT Polska &lt;cert@cert.pl&gt;<br><b>Wysłane:</b> wtorek, 24 listopada 2026 07:00<br><b>Do:</b> Ktos<br><b>Temat:</b> Ostrzeżenie: podatność w serwerze poczty</p></div>
        <p>Treść ostrzeżenia o podatności w serwerze poczty.</p></body></html>
        """;

    private static string AsAttachment(string inner) => $"""
        Message-ID: <wrap-1@przychodnia.example>
        Date: Tue, 24 Nov 2026 12:00:00 +0100
        MIME-Version: 1.0
        Subject: Fwd: w załączniku
        From: Ktos <ktos@przychodnia.example>
        Content-Type: multipart/mixed; boundary="MIX"

        --MIX
        Content-Type: text/plain; charset=UTF-8

        --MIX
        Content-Type: message/rfc822

        {inner}
        --MIX--
        """;

    private static MailParseResult Parse(string eml, string name = "mail.eml") =>
        MailImport.Parse(Encoding.UTF8.GetBytes(eml.ReplaceLineEndings("\r\n")), name);

    [Fact]
    public void Thunderbird_forward_keeps_only_the_advisory()
    {
        var result = Parse(ThunderbirdForward);

        var entry = Assert.IsType<MailEntry>(result.Entry);
        Assert.Equal("Ostrzeżenie o podatnościach w Przykład Router", entry.Title);
        Assert.Equal(new DateOnly(2026, 11, 26), entry.PublishedOn); // 23:30 UTC to już 26.11 w Warszawie
        Assert.Equal("CSIRT MON", entry.Origin);
        Assert.Equal("mail:oryginal-123@mon.gov.pl", entry.Key);
        Assert.Equal("""
            Szanowni Państwo,
            przesyłamy informacje na temat podatności w Przykład Router.
            Produkt | CVE
            Przykład Router | CVE-2026-55555
            Opis: https://producent.example/advisory
            """.ReplaceLineEndings("\n"), entry.Text);
        foreach (var gone in new[] { "@", "Anna", "Proszę o weryfikację", "Treść przekazanej", "Z wyrazami", "RODO", "Przykładowa 1", "<img", "MsoNormal", "style" })
            Assert.DoesNotContain(gone, entry.Text + entry.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<td><p><b>Produkt</b></p></td>", entry.Html.Replace(" ", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_text_mail_in_latin2_without_forward_is_decoded_and_signature_dropped()
    {
        var entry = Assert.IsType<MailEntry>(Parse(PlainLatin2).Entry);

        Assert.Equal("Krytyczna podatność w VPN", entry.Title);
        Assert.Equal("Zespół Bezpieczeństwa", entry.Origin);
        Assert.Equal(new DateOnly(2026, 11, 23), entry.PublishedOn);
        Assert.Equal("Wykryto podatność w bramie VPN.\nZalecana aktualizacja do wersji 7.4.3.", entry.Text);
        Assert.Equal("mail:plain-7@cert.example", entry.Key);
    }

    [Fact]
    public void Outlook_forward_and_mail_attached_as_message_are_read()
    {
        var outlook = Assert.IsType<MailEntry>(Parse(OutlookForward).Entry);
        Assert.Equal("Ostrzeżenie: podatność w serwerze poczty", outlook.Title);
        Assert.Equal("CERT Polska", outlook.Origin);
        Assert.Equal("Treść ostrzeżenia o podatności w serwerze poczty.", outlook.Text);

        var attached = Assert.IsType<MailEntry>(Parse(AsAttachment(PlainLatin2.Replace("ISO-8859-2", "UTF-8").Replace("=B6=E6", "=C5=9B=C4=87").Replace("=F3=B3", "=C3=B3=C5=82").Replace("=F1", "=C5=84"))).Entry);
        Assert.Equal("Krytyczna podatność w VPN", attached.Title);
        Assert.Equal("mail:plain-7@cert.example", attached.Key); // identyfikator wiadomości w załączniku
    }

    [Fact]
    public void Garbage_and_empty_files_give_a_message()
    {
        Assert.NotNull(MailImport.Parse(Encoding.UTF8.GetBytes("to nie jest mail"), "notatka.eml").Error);
        Assert.NotNull(MailImport.Parse([], "pusty.eml").Error);
        Assert.NotNull(Parse("Subject: tylko temat\r\nFrom: a@b.example\r\nContent-Type: text/plain\r\n\r\n   \r\n").Error);
    }

    [Fact]
    public async Task Imported_mail_is_stored_once()
    {
        await using var app = await AppFactory.CreateAsync(postgres);
        var entries = app.Services.GetRequiredService<UserEntryService>();
        var mail = Parse(ThunderbirdForward).Entry!;
        var input = new UserEntryInput(mail.Origin, mail.Title, mail.Html, mail.PublishedOn);

        Assert.Empty(await entries.ExistingKeysAsync([mail.Key]));
        Assert.True((await entries.CreateAsync(input, "admin", key: mail.Key)).Ok);
        Assert.Equal([mail.Key], await entries.ExistingKeysAsync([mail.Key, "mail:inny@x"]));
        var again = await entries.CreateAsync(input, "admin", key: mail.Key);

        Assert.False(again.Ok);
        Assert.Contains("już zaimportowany", again.Error);
    }
}
