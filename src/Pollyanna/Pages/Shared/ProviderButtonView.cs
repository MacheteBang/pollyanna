using Pollyanna.Auth;

namespace Pollyanna.Pages.Shared;

/// <summary>
/// One provider's button (spec §3, §7): <paramref name="Text"/>, such as "Sign in with Google" or
/// "Add Google", with the provider's icon beside it when the operator supplied one. A link to
/// <paramref name="Href"/>, or with no href a submit button for the form it sits in.
/// <paramref name="Outline"/> draws the outlined variant (the refused page, the account page).
/// </summary>
public sealed record ProviderButtonView(Provider Provider, string Text, string? Href, bool Outline = false);
