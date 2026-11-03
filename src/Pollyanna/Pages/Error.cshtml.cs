using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Pollyanna.Pages;

[IgnoreAntiforgeryToken]
public sealed class ErrorModel : PageModel
{
    public string Heading { get; private set; } = "Something went wrong";
    public string Message { get; private set; } = "Pollyanna hit a problem. Try again in a moment.";

    public void OnGet(int? code) => Describe(code);
    public void OnPost(int? code) => Describe(code);

    void Describe(int? code)
    {
        switch (code)
        {
            case 404:
                Heading = "Not here";
                Message = "There's nothing at this address, or it isn't yours to see.";
                break;
            case 400:
                Heading = "That didn't go through";
                Message = "The page may have been open too long. Go back, reload and try again.";
                break;
            case 403:
                Heading = "Not yours to see";
                Message = "You don't have access to this page.";
                break;
        }
        if (code is { } c)
            Response.StatusCode = c;
    }
}
