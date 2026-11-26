using Pollyanna.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pollyanna.Notifications;

/// <summary>Renders the HTML part of an email from <see cref="EmailLayout"/> (spec §11).</summary>
public static class MailHtml
{
    /// <summary>The HTML for an email, from the same inputs as its plain text (<see cref="OutboxSender.Render"/>).</summary>
    public static async Task<string> RenderAsync(NotificationContent content, Audience audience, Event ev, string publicUrl)
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<EmailLayout>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(EmailLayout.Content)] = content,
                [nameof(EmailLayout.Audience)] = audience,
                [nameof(EmailLayout.Event)] = ev,
                [nameof(EmailLayout.PublicUrl)] = publicUrl,
            }));
            return output.ToHtmlString();
        });
    }
}
