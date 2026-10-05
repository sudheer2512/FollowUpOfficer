using FollowUp.Integrations.Graph;

namespace FollowUp.Tests.Integrations;

public sealed class HtmlTextTests
{
    [Fact]
    public void Outlook_html_reply_keeps_only_the_new_text()
    {
        const string html = "<html><body><div>Done, the vendor confirmed delivery.</div><div>Invoice attached.</div>"
                            + "<hr style=\"display:inline-block;width:98%\"><div id=\"divRplyFwdMsg\"><b>From:</b> Anita Rao<br><b>Sent:</b> Monday"
                            + "<br><b>Subject:</b> Reminder</div><div>Please update the status of FT-12.</div></body></html>";

        var text = HtmlText.MailBodyToText(html, "html");

        Assert.Equal("Done, the vendor confirmed delivery.\nInvoice attached.", text);
    }

    [Theory]
    [InlineData("Will finish by Friday.\n\nOn Mon, 5 Oct 2026 at 10:00, Anita Rao <ceo@contoso.com> wrote:\n> Please update", "Will finish by Friday.")]
    [InlineData("Will finish by Friday.\n-----Original Message-----\nFrom: Anita\nSent: Monday", "Will finish by Friday.")]
    [InlineData("Will finish by Friday.\n\nFrom: Anita Rao\nSent: Monday, October 5, 2026\nTo: Ravi\nSubject: FT-12", "Will finish by Friday.")]
    [InlineData("Will finish by Friday.\n________________________________\nFrom: Anita Rao\nSent: Monday", "Will finish by Friday.")]
    [InlineData("From: the vendor side we are blocked.\nWaiting for PO.", "From: the vendor side we are blocked.\nWaiting for PO.")]
    public void Plain_text_quote_history_is_cut(string body, string expected) =>
        Assert.Equal(expected, HtmlText.MailBodyToText(body, "text"));

    [Fact]
    public void Body_that_is_only_a_quote_is_kept()
    {
        const string body = "-----Original Message-----\nFrom: Anita\nPlease update";
        Assert.Contains("Please update", HtmlText.MailBodyToText(body, "text"));
    }

    [Fact]
    public void Teams_html_turns_mentions_into_at_names_and_drops_reply_quotes()
    {
        const string html = "<blockquote itemtype=\"http://schema.skype.com/Reply\">old message</blockquote>"
                            + "<p><at id=\"0\">Ravi Kumar</at> please share the update &amp; the ETA</p><attachment id=\"a1\"></attachment>";

        Assert.Equal("@Ravi Kumar please share the update & the ETA", HtmlText.TeamsBodyToText(html, "html"));
    }

    [Fact]
    public void Block_elements_become_lines()
    {
        Assert.Equal("Line one\nLine two\n\n- item", HtmlText.ToPlainText("<p>Line   one</p><div>Line two</div><ul><li>item</li></ul>"));
    }
}
