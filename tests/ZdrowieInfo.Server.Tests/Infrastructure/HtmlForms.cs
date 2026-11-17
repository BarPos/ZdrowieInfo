using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace ZdrowieInfo.Server.Tests.Infrastructure;

/// <summary>Wypełnia i wysyła formularze stron static SSR tak jak przeglądarka (z antiforgery i _handler).</summary>
public static class HtmlForms
{
    public static async Task<IHtmlDocument> GetDocumentAsync(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync());
    }

    public static async Task<HttpResponseMessage> SubmitAsync(this HttpClient client, string pageUrl, string formName,
        IDictionary<string, string> values)
    {
        var document = await client.GetDocumentAsync(pageUrl);
        return await client.SubmitAsync(document, pageUrl, formName, values);
    }

    public static async Task<HttpResponseMessage> SubmitAsync(this HttpClient client, IHtmlDocument document, string pageUrl,
        string formName, IDictionary<string, string> values)
    {
        var form = document.Forms.SingleOrDefault(f =>
                       f.QuerySelector($"input[name='_handler'][value='{formName}']") is not null)
                   ?? throw new InvalidOperationException($"Brak formularza {formName} na {pageUrl}.");

        var fields = new Dictionary<string, string>();
        foreach (var input in form.QuerySelectorAll<IHtmlInputElement>("input[name]"))
        {
            if (input.Type is "checkbox" or "radio" && !input.IsChecked) continue;
            if (input.Type is "submit" or "button") continue;
            fields[input.Name!] = input.Value;
        }
        foreach (var (name, value) in values) fields[name] = value;

        var action = string.IsNullOrEmpty(form.GetAttribute("action")) ? pageUrl : form.GetAttribute("action")!;
        return await client.PostAsync(action, new FormUrlEncodedContent(fields));
    }

    /// <summary>Zwykły formularz POST (bez @formname) wskazany atrybutem action – pola, w tym token antiforgery, ze strony.</summary>
    public static async Task<HttpResponseMessage> SubmitActionAsync(this HttpClient client, string pageUrl, string action)
    {
        var document = await client.GetDocumentAsync(pageUrl);
        var form = document.Forms.SingleOrDefault(f => f.GetAttribute("action") == action)
                   ?? throw new InvalidOperationException($"Brak formularza {action} na {pageUrl}.");
        var fields = form.QuerySelectorAll<IHtmlInputElement>("input[name]").ToDictionary(i => i.Name!, i => i.Value);
        return await client.PostAsync("/" + action, new FormUrlEncodedContent(fields));
    }

    public static string? RedirectPath(this HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location is null) return null;
        if (location.IsAbsoluteUri) return location.PathAndQuery;
        return location.OriginalString.StartsWith('/') ? location.OriginalString : "/" + location.OriginalString;
    }
}
